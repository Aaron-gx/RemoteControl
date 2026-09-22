package main

// filestore.go implements the temporary file staging area that agents and
// viewers use to hand large payloads around.
//
// Endpoints:
//
//	POST   /api/upload              multipart/form-data, field "file"
//	GET    /api/download/{fileId}   stream the file back
//	DELETE /api/file/{fileId}       drop a staged file early
//
// Files live for DefaultFileTTL (10 minutes) and are then removed by a
// background janitor that scans every DefaultCleanupInterval (30 seconds).
// Metadata is kept in memory only, guarded by a mutex: no database, no object
// store, exactly as the design asks for.

import (
	"regexp"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"mime"
	"mime/multipart"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// DefaultMaxFileSize is the largest single upload accepted: 512 MiB.
	DefaultMaxFileSize int64 = 512 << 20
	// DefaultFileTTL is how long an uploaded file survives.
	DefaultFileTTL = 10 * time.Minute
	// DefaultCleanupInterval is how often the janitor scans the staging dir.
	DefaultCleanupInterval = 30 * time.Second
	// uploadFieldName is the multipart field carrying the payload.
	uploadFieldName = "file"
	// fileIDBytes is the raw entropy behind a file id; hex encoded it yields
	// the 16 character id returned by /api/upload.
	fileIDBytes = 8
	// fileIDLength is the length of the hex encoded id.
	fileIDLength = fileIDBytes * 2
	// partSuffix marks uploads that are still being written.
	partSuffix = ".part"
	// formSlack is the extra request-body allowance on top of the file limit,
	// so multipart boundaries and headers do not turn into 413s.
	formSlack int64 = 1 << 20
	// orphanGrace keeps the janitor from deleting files that are being
	// uploaded right now by a concurrent request.
	orphanGrace = 1 * time.Minute
)

// fileTooLargeError reports an upload that exceeded the configured limit.
type fileTooLargeError struct {
	size int64
	max  int64
}

func (e fileTooLargeError) Error() string {
	return fmt.Sprintf("file is larger than the %d byte limit (read %d bytes)", e.max, e.size)
}

// fileMeta is the in-memory record of one staged file. The payload on disk is
// always named after the generated id; FileName is metadata only, so a hostile
// client name can never influence the path.
type fileMeta struct {
	ID        string
	FileName  string
	Size      int64
	CreatedAt time.Time
	ExpiresAt time.Time
	Path      string
}

// uploadResponse is the wire shape of a successful POST /api/upload.
type uploadResponse struct {
	FileID    string `json:"fileId"`
	FileName  string `json:"fileName"`
	FileSize  int64  `json:"fileSize"`
	ExpiresAt string `json:"expiresAt"`
}

func (m *fileMeta) response() uploadResponse {
	return uploadResponse{
		FileID:    m.ID,
		FileName:  m.FileName,
		FileSize:  m.Size,
		ExpiresAt: m.ExpiresAt.UTC().Format(time.RFC3339),
	}
}

// FileStore is the staging area.
type FileStore struct {
	// Token, when non-empty, must match on every staging request.
	Token string

	dir      string
	logger   *log.Logger
	maxSize  int64
	ttl      time.Duration
	interval time.Duration

	mu    sync.Mutex
	files map[string]*fileMeta

	// now is swappable so tests can move the clock without sleeping.
	now func() time.Time
}

// NewFileStore creates the staging area in dir with the documented defaults.
// fileIDPattern 匹配本服务写出的暂存文件名（16 位小写十六进制）。
var fileIDPattern = regexp.MustCompile(`^[0-9a-f]{16}$`)

func NewFileStore(dir string, logger *log.Logger) (*FileStore, error) {
	return NewFileStoreWithOptions(dir, logger, DefaultMaxFileSize, DefaultFileTTL, DefaultCleanupInterval)
}

// NewFileStoreWithOptions is NewFileStore with an explicit size limit, TTL and
// janitor interval.
func NewFileStoreWithOptions(dir string, logger *log.Logger, maxSize int64, ttl, interval time.Duration) (*FileStore, error) {
	if logger == nil {
		logger = log.New(io.Discard, "", 0)
	}
	if maxSize <= 0 {
		maxSize = DefaultMaxFileSize
	}
	if ttl <= 0 {
		ttl = DefaultFileTTL
	}
	if interval <= 0 {
		interval = DefaultCleanupInterval
	}

	abs, err := filepath.Abs(dir)
	if err != nil {
		return nil, fmt.Errorf("resolve data dir %q: %w", dir, err)
	}
	if err := os.MkdirAll(abs, 0o755); err != nil {
		return nil, fmt.Errorf("create data dir %q: %w", abs, err)
	}

	return &FileStore{
		dir:      abs,
		logger:   logger,
		maxSize:  maxSize,
		ttl:      ttl,
		interval: interval,
		files:    make(map[string]*fileMeta),
		now:      time.Now,
	}, nil
}

// Dir returns the absolute staging directory.
func (s *FileStore) Dir() string { return s.dir }

// MaxFileSize returns the per-file upload limit in bytes.
func (s *FileStore) MaxFileSize() int64 { return s.maxSize }

// Register wires the staging endpoints onto mux.
func (s *FileStore) Register(mux *http.ServeMux) {
	mux.HandleFunc("POST /api/upload", s.handleUpload)
	mux.HandleFunc("GET /api/download/{fileId}", s.handleDownload)
	mux.HandleFunc("DELETE /api/file/{fileId}", s.handleDelete)
}

// RunCleanup drives the janitor until ctx is cancelled.
func (s *FileStore) RunCleanup(ctx context.Context) {
	ticker := time.NewTicker(s.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.CleanupExpired()
		}
	}
}

// CleanupExpired removes every staged file whose TTL has passed, plus any
// leftover file in the staging directory that no metadata refers to (a partial
// upload from a crash, or the remains of a previous process). It returns the
// number of payloads removed.
func (s *FileStore) CleanupExpired() int {
	now := s.now()

	s.mu.Lock()
	expired := make([]*fileMeta, 0)
	for id, meta := range s.files {
		if !meta.ExpiresAt.After(now) {
			expired = append(expired, meta)
			delete(s.files, id)
		}
	}
	known := make(map[string]struct{}, len(s.files))
	for id := range s.files {
		known[id] = struct{}{}
	}
	s.mu.Unlock()

	removed := 0
	for _, meta := range expired {
		if err := os.Remove(meta.Path); err != nil && !errors.Is(err, os.ErrNotExist) {
			s.logger.Printf("filestore: cleanup of %s failed: %v", meta.ID, err)
			continue
		}
		removed++
		s.logger.Printf("filestore: expired %s (%s, %d bytes)", meta.ID, meta.FileName, meta.Size)
	}

	removed += s.sweep(s.now(), known)
	return removed
}

// sweep deletes staging-directory entries that are not tracked by any metadata
// and are older than the grace period.
func (s *FileStore) sweep(now time.Time, known map[string]struct{}) int {
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		s.logger.Printf("filestore: cannot scan %s: %v", s.dir, err)
		return 0
	}

	removed := 0
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		name := entry.Name()
		if _, ok := known[name]; ok {
			continue
		}
		// 只清理"本服务自己写出来的暂存文件"（文件名 = 16 位十六进制 fileId）。
		// 数据目录里可能还放着部署ID/授权文件等运行状态，绝不能当垃圾删掉
		// —— 实测踩过：元数据被扫掉后部署ID 重新随机生成，已激活的授权会因此失效。
		if !fileIDPattern.MatchString(name) {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			continue
		}
		age := now.Sub(info.ModTime())
		// A tracked file is never swept; an untracked one has to be quiet for
		// a while before it is considered garbage.
		if age < orphanGrace && age < s.ttl {
			continue
		}
		path := filepath.Join(s.dir, name)
		if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
			s.logger.Printf("filestore: cannot remove orphan %s: %v", name, err)
			continue
		}
		removed++
		s.logger.Printf("filestore: removed orphan %s", name)
	}
	return removed
}

func (s *FileStore) handleUpload(w http.ResponseWriter, r *http.Request) {
	if !Authorized(s.Token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, s.maxSize+formSlack)

	reader, err := r.MultipartReader()
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid_multipart", err.Error())
		return
	}

	for {
		part, err := reader.NextPart()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			s.drain(r)
			if isTooLarge(err) {
				s.rejectTooLarge(w)
				return
			}
			writeJSONError(w, http.StatusBadRequest, "invalid_multipart", err.Error())
			return
		}
		if part.FormName() != uploadFieldName {
			_ = part.Close()
			continue
		}

		meta, err := s.savePart(part)
		_ = part.Close()
		if err != nil {
			s.drain(r)
			var tooLarge fileTooLargeError
			if errors.As(err, &tooLarge) || isTooLarge(err) {
				s.rejectTooLarge(w)
				return
			}
			s.logger.Printf("filestore: upload failed: %v", err)
			writeJSONError(w, http.StatusInternalServerError, "store_failed", err.Error())
			return
		}

		s.logger.Printf("filestore: stored %s (%s, %d bytes, expires %s)",
			meta.ID, meta.FileName, meta.Size, meta.ExpiresAt.UTC().Format(time.RFC3339))
		writeJSON(w, http.StatusOK, meta.response())
		// The payload is in hand; anything still in flight is not ours.
		return
	}

	writeJSONError(w, http.StatusBadRequest, "missing_file", `multipart field "file" is required`)
}

// savePart streams one multipart part into the staging directory. The payload
// is written under .part first and renamed once it is complete, so a
// half-written upload is never downloadable.
func (s *FileStore) savePart(part *multipart.Part) (*fileMeta, error) {
	id, err := newFileID()
	if err != nil {
		return nil, err
	}
	fileName := sanitizeFileName(part.FileName())
	finalPath := filepath.Join(s.dir, id)
	tempPath := finalPath + partSuffix

	file, err := os.OpenFile(tempPath, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		return nil, err
	}

	written, copyErr := io.Copy(file, io.LimitReader(part, s.maxSize+1))
	closeErr := file.Close()
	if copyErr == nil {
		copyErr = closeErr
	}
	if copyErr != nil {
		_ = os.Remove(tempPath)
		return nil, copyErr
	}
	if written > s.maxSize {
		_ = os.Remove(tempPath)
		return nil, fileTooLargeError{size: written, max: s.maxSize}
	}
	if err := os.Rename(tempPath, finalPath); err != nil {
		_ = os.Remove(tempPath)
		return nil, err
	}

	now := s.now()
	meta := &fileMeta{
		ID:        id,
		FileName:  fileName,
		Size:      written,
		CreatedAt: now,
		ExpiresAt: now.Add(s.ttl),
		Path:      finalPath,
	}

	s.mu.Lock()
	s.files[id] = meta
	s.mu.Unlock()

	return meta, nil
}

func (s *FileStore) handleDownload(w http.ResponseWriter, r *http.Request) {
	if !Authorized(s.Token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	id := r.PathValue("fileId")
	meta, err := s.lookup(id)
	if err != nil {
		writeJSONError(w, http.StatusNotFound, "not_found", "")
		return
	}

	file, err := os.Open(meta.Path)
	if err != nil {
		// The payload disappeared underneath us (manual cleanup, crash): forget
		// the entry and report the file as gone.
		s.forget(id)
		writeJSONError(w, http.StatusNotFound, "not_found", "")
		return
	}
	defer file.Close()

	info, err := file.Stat()
	if err != nil || info.IsDir() {
		s.forget(id)
		writeJSONError(w, http.StatusNotFound, "not_found", "")
		return
	}

	header := w.Header()
	header.Set("Content-Type", contentTypeFor(meta.FileName))
	header.Set("Content-Length", strconv.FormatInt(info.Size(), 10))
	header.Set("Content-Disposition", "attachment; filename*=UTF-8''"+escapeFileNameAttr(meta.FileName))
	w.WriteHeader(http.StatusOK)
	if _, err := io.Copy(w, file); err != nil {
		s.logger.Printf("filestore: download of %s interrupted: %v", id, err)
		return
	}
	s.logger.Printf("filestore: served %s (%s, %d bytes)", id, meta.FileName, info.Size())
}

func (s *FileStore) handleDelete(w http.ResponseWriter, r *http.Request) {
	if !Authorized(s.Token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	id := r.PathValue("fileId")
	meta, err := s.lookup(id)
	if err != nil {
		writeJSONError(w, http.StatusNotFound, "not_found", "")
		return
	}
	s.forget(id)
	if err := os.Remove(meta.Path); err != nil && !errors.Is(err, os.ErrNotExist) {
		s.logger.Printf("filestore: delete of %s failed: %v", id, err)
		writeJSONError(w, http.StatusInternalServerError, "delete_failed", err.Error())
		return
	}
	s.logger.Printf("filestore: deleted %s (%s)", id, meta.FileName)
	writeJSON(w, http.StatusOK, map[string]any{"deleted": true, "fileId": id})
}

func (s *FileStore) lookup(id string) (*fileMeta, error) {
	if !validFileID(id) {
		return nil, os.ErrNotExist
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	meta, ok := s.files[id]
	if !ok {
		return nil, os.ErrNotExist
	}
	return meta, nil
}

func (s *FileStore) forget(id string) *fileMeta {
	s.mu.Lock()
	defer s.mu.Unlock()
	meta, ok := s.files[id]
	if !ok {
		return nil
	}
	delete(s.files, id)
	return meta
}

// rejectTooLarge answers 413 and, when the rest of the body is small enough,
// drains it so the client still sees a clean response instead of a reset.
func (s *FileStore) rejectTooLarge(w http.ResponseWriter) {
	s.logger.Printf("filestore: upload rejected: larger than the %d byte limit", s.maxSize)
	writeJSON(w, http.StatusRequestEntityTooLarge, map[string]any{
		"error":   "file_too_large",
		"maxSize": s.maxSize,
	})
}

// drain consumes what is left of a request body within the allowance, so the
// connection can be reused and the client can read the error response.
func (s *FileStore) drain(r *http.Request) {
	_, _ = io.Copy(io.Discard, io.LimitReader(r.Body, formSlack))
}

func isTooLarge(err error) bool {
	var maxBytes *http.MaxBytesError
	return errors.As(err, &maxBytes)
}

// newFileID returns a 16 character hex id backed by 8 bytes of entropy.
func newFileID() (string, error) {
	buf := make([]byte, fileIDBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", fmt.Errorf("generate file id: %w", err)
	}
	return hex.EncodeToString(buf), nil
}

// validFileID accepts exactly the ids newFileID mints: 16 lowercase hex
// characters. Anything else cannot name a staged file.
func validFileID(id string) bool {
	if len(id) != fileIDLength {
		return false
	}
	for i := 0; i < len(id); i++ {
		c := id[i]
		if (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') {
			continue
		}
		return false
	}
	return true
}

// sanitizeFileName strips everything that could turn a client supplied name
// into a path, and normalises the empty/odd cases.
func sanitizeFileName(name string) string {
	name = strings.ReplaceAll(name, "\\", "/")
	name = path.Base(name)
	name = strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return -1
		}
		return r
	}, name)
	name = strings.TrimSpace(name)
	if name == "" || name == "." || name == ".." || name == "/" {
		return "unnamed"
	}
	// Keep the name well inside filesystem limits even though it is only
	// metadata.
	const maxRunes = 200
	if runes := []rune(name); len(runes) > maxRunes {
		name = string(runes[:maxRunes])
	}
	return name
}

// escapeFileNameAttr encodes a name for RFC 5987 filename*=UTF-8”<value>.
func escapeFileNameAttr(name string) string {
	const attrChars = "!#$&+-.^_`|~"
	var b strings.Builder
	for i := 0; i < len(name); i++ {
		c := name[i]
		switch {
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z', c >= '0' && c <= '9':
			b.WriteByte(c)
		case strings.IndexByte(attrChars, c) >= 0:
			b.WriteByte(c)
		default:
			fmt.Fprintf(&b, "%%%02X", c)
		}
	}
	return b.String()
}

func contentTypeFor(fileName string) string {
	if ext := path.Ext(fileName); ext != "" {
		if ct := mime.TypeByExtension(ext); ct != "" {
			return ct
		}
	}
	return "application/octet-stream"
}

// writeJSON writes payload as JSON with no trailing newline, so bodies match
// the documented shapes byte for byte.
func writeJSON(w http.ResponseWriter, status int, payload any) {
	body, err := json.Marshal(payload)
	if err != nil {
		http.Error(w, `{"error":"internal"}`, http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(status)
	_, _ = w.Write(body)
}

type jsonError struct {
	Error  string `json:"error"`
	Detail string `json:"detail,omitempty"`
}

func writeJSONError(w http.ResponseWriter, status int, code, detail string) {
	writeJSON(w, status, jsonError{Error: code, Detail: detail})
}
