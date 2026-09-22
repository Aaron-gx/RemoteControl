package main

// filestore_test.go exercises the staging area through HTTP: uploads are real
// multipart bodies, downloads are real GETs, and the bytes are compared.

import (
	"log"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

func newTestStore(t *testing.T, maxSize int64, ttl, interval time.Duration) *FileStore {
	t.Helper()
	store, err := NewFileStoreWithOptions(t.TempDir(), discardLogger(), maxSize, ttl, interval)
	if err != nil {
		t.Fatalf("NewFileStoreWithOptions: %v", err)
	}
	return store
}

// upload posts content as multipart/form-data under the given client file name.
func upload(t *testing.T, server *httptest.Server, fileName string, content []byte) (uploadResponse, *http.Response) {
	t.Helper()
	var body bytes.Buffer
	writer := multipart.NewWriter(&body)
	part, err := writer.CreateFormFile(uploadFieldName, fileName)
	if err != nil {
		t.Fatalf("CreateFormFile: %v", err)
	}
	if _, err := part.Write(content); err != nil {
		t.Fatalf("write part: %v", err)
	}
	if err := writer.Close(); err != nil {
		t.Fatalf("close writer: %v", err)
	}

	resp, err := http.Post(server.URL+"/api/upload", writer.FormDataContentType(), &body)
	if err != nil {
		t.Fatalf("POST /api/upload: %v", err)
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read upload response: %v", err)
	}

	var decoded uploadResponse
	if resp.StatusCode == http.StatusOK {
		if err := json.Unmarshal(raw, &decoded); err != nil {
			t.Fatalf("decode upload response %s: %v", raw, err)
		}
	}
	return decoded, &http.Response{StatusCode: resp.StatusCode, Header: resp.Header, Body: io.NopCloser(bytes.NewReader(raw))}
}

func uploadOK(t *testing.T, server *httptest.Server, fileName string, content []byte) uploadResponse {
	t.Helper()
	decoded, resp := upload(t, server, fileName, content)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("upload %q: status %d", fileName, resp.StatusCode)
	}
	return decoded
}

func download(t *testing.T, server *httptest.Server, fileID string) (*http.Response, []byte) {
	t.Helper()
	resp, err := http.Get(server.URL + "/api/download/" + fileID)
	if err != nil {
		t.Fatalf("GET /api/download/%s: %v", fileID, err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read download body: %v", err)
	}
	return resp, body
}

func TestUploadDownloadRoundTripWithUnicodeName(t *testing.T) {
	_, store, server := newTestServer(t)

	const fileName = "远程控制 屏幕录像-2026年09月20日 14时30分.mp4"
	content := append([]byte("RCSTAGE\x00\x01\x02"), bytes.Repeat([]byte("payload-"), 5000)...)

	got := uploadOK(t, server, fileName, content)

	if !regexp.MustCompile(`^[0-9a-f]{16}$`).MatchString(got.FileID) {
		t.Fatalf("fileId = %q, want 16 lowercase hex characters", got.FileID)
	}
	if got.FileName != fileName {
		t.Fatalf("fileName = %q, want %q", got.FileName, fileName)
	}
	if got.FileSize != int64(len(content)) {
		t.Fatalf("fileSize = %d, want %d", got.FileSize, len(content))
	}
	expiresAt, err := time.Parse(time.RFC3339, got.ExpiresAt)
	if err != nil {
		t.Fatalf("expiresAt %q is not RFC3339: %v", got.ExpiresAt, err)
	}
	if remaining := time.Until(expiresAt); remaining < 9*time.Minute || remaining > 11*time.Minute {
		t.Fatalf("expiresAt is %s away, want about %s", remaining, DefaultFileTTL)
	}

	// the payload is stored under the generated id, never under the client name
	if _, err := os.Stat(filepath.Join(store.Dir(), got.FileID)); err != nil {
		t.Fatalf("staged payload missing: %v", err)
	}

	resp, body := download(t, server, got.FileID)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("download status = %d, want 200", resp.StatusCode)
	}
	if !bytes.Equal(body, content) {
		t.Fatalf("downloaded %d bytes, uploaded %d bytes", len(body), len(content))
	}
	if resp.Header.Get("Content-Length") != strconv.Itoa(len(content)) {
		t.Fatalf("Content-Length = %q, want %d", resp.Header.Get("Content-Length"), len(content))
	}

	disposition := resp.Header.Get("Content-Disposition")
	prefix := "attachment; filename*=UTF-8''"
	if !strings.HasPrefix(disposition, prefix) {
		t.Fatalf("Content-Disposition = %q, want the RFC 5987 form", disposition)
	}
	decodedName, err := url.PathUnescape(strings.TrimPrefix(disposition, prefix))
	if err != nil {
		t.Fatalf("filename* is not a valid percent encoding: %v", err)
	}
	if decodedName != fileName {
		t.Fatalf("filename* decoded to %q, want %q", decodedName, fileName)
	}
}

func TestUploadIgnoresClientPathInFileName(t *testing.T) {
	_, store, server := newTestServer(t)

	for _, hostile := range []string{"../../evil.txt", `..\..\evil.txt`, "/etc/passwd", ".."} {
		got := uploadOK(t, server, hostile, []byte("payload"))
		if strings.ContainsAny(got.FileName, `/\`) {
			t.Fatalf("stored file name %q still contains a path separator", got.FileName)
		}
		if _, err := os.Stat(filepath.Join(store.Dir(), got.FileID)); err != nil {
			t.Fatalf("payload for %q was not staged under its id: %v", hostile, err)
		}
	}

	parent := filepath.Dir(store.Dir())
	entries, err := os.ReadDir(parent)
	if err != nil {
		t.Fatalf("read %s: %v", parent, err)
	}
	for _, entry := range entries {
		if strings.Contains(entry.Name(), "evil") || entry.Name() == "passwd" {
			t.Fatalf("a client supplied name escaped the staging dir: %s", filepath.Join(parent, entry.Name()))
		}
	}
}

func TestUploadRejectsOversizedFile(t *testing.T) {
	const limit int64 = 64 << 10
	store := newTestStore(t, limit, DefaultFileTTL, DefaultCleanupInterval)
	_, _, server := newTestServerWithStore(t, store)

	oversized := bytes.Repeat([]byte("x"), int(limit*2))
	_, resp := upload(t, server, "big.bin", oversized)
	if resp.StatusCode != http.StatusRequestEntityTooLarge {
		body, _ := io.ReadAll(resp.Body)
		t.Fatalf("upload of %d bytes returned %d (%s), want 413", len(oversized), resp.StatusCode, body)
	}

	entries, err := os.ReadDir(store.Dir())
	if err != nil {
		t.Fatalf("read staging dir: %v", err)
	}
	if len(entries) != 0 {
		t.Fatalf("rejected upload left %d files behind: %v", len(entries), entries)
	}

	// the limit itself must still be accepted
	exact := bytes.Repeat([]byte("y"), int(limit))
	if got := uploadOK(t, server, "exact.bin", exact); got.FileSize != limit {
		t.Fatalf("upload at the limit reported %d bytes, want %d", got.FileSize, limit)
	}
}

func TestDownloadUnknownFileIs404(t *testing.T) {
	_, _, server := newTestServer(t)

	for _, fileID := range []string{"deadbeefdeadbeef", "missing", strings.Repeat("f", 16)} {
		resp, body := download(t, server, fileID)
		if resp.StatusCode != http.StatusNotFound {
			t.Fatalf("download %q: status %d, want 404", fileID, resp.StatusCode)
		}
		if string(body) != `{"error":"not_found"}` {
			t.Fatalf("download %q body = %s, want {\"error\":\"not_found\"}", fileID, body)
		}
		if ct := resp.Header.Get("Content-Type"); !strings.HasPrefix(ct, "application/json") {
			t.Fatalf("download %q content type = %q, want JSON", fileID, ct)
		}
	}
}

func TestExpiredFileIsRemovedAndThenNotFound(t *testing.T) {
	store := newTestStore(t, DefaultMaxFileSize, DefaultFileTTL, DefaultCleanupInterval)
	_, _, server := newTestServerWithStore(t, store)

	content := []byte("expire me")
	got := uploadOK(t, server, "expire.txt", content)

	if removed := store.CleanupExpired(); removed != 0 {
		t.Fatalf("cleanup removed %d files before the TTL elapsed", removed)
	}
	if resp, _ := download(t, server, got.FileID); resp.StatusCode != http.StatusOK {
		t.Fatalf("download before the TTL: status %d, want 200", resp.StatusCode)
	}

	// move the clock past the TTL instead of sleeping
	store.now = func() time.Time { return time.Now().Add(DefaultFileTTL + time.Second) }

	if removed := store.CleanupExpired(); removed != 1 {
		t.Fatalf("cleanup removed %d files, want 1", removed)
	}
	if _, err := os.Stat(filepath.Join(store.Dir(), got.FileID)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("payload still on disk after cleanup: %v", err)
	}
	resp, body := download(t, server, got.FileID)
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("download after cleanup: status %d, want 404", resp.StatusCode)
	}
	if string(body) != `{"error":"not_found"}` {
		t.Fatalf("download after cleanup body = %s", body)
	}
}

func TestJanitorGoroutineRemovesFilesAfterTTL(t *testing.T) {
	store := newTestStore(t, DefaultMaxFileSize, 60*time.Millisecond, 15*time.Millisecond)
	_, _, server := newTestServerWithStore(t, store)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go store.RunCleanup(ctx)

	got := uploadOK(t, server, "janitor.txt", []byte("background cleanup"))

	deadline := time.Now().Add(5 * time.Second)
	for {
		resp, _ := download(t, server, got.FileID)
		if resp.StatusCode == http.StatusNotFound {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("file %s was still downloadable after the TTL", got.FileID)
		}
		time.Sleep(10 * time.Millisecond)
	}

	if _, err := os.Stat(filepath.Join(store.Dir(), got.FileID)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("payload still on disk after the janitor ran: %v", err)
	}
}

func TestJanitorSweepsOrphanedFiles(t *testing.T) {
	store := newTestStore(t, DefaultMaxFileSize, 60*time.Millisecond, 15*time.Millisecond)
	_, _, server := newTestServerWithStore(t, store)

	// an unreferenced leftovers file, as a crash or a restart would leave behind
	orphan := filepath.Join(store.Dir(), "0123456789abcdef")
	if err := os.WriteFile(orphan, []byte("orphan"), 0o644); err != nil {
		t.Fatalf("write orphan: %v", err)
	}
	past := time.Now().Add(-time.Hour)
	if err := os.Chtimes(orphan, past, past); err != nil {
		t.Fatalf("age orphan: %v", err)
	}

	if removed := store.CleanupExpired(); removed != 1 {
		t.Fatalf("sweep removed %d files, want 1", removed)
	}
	if _, err := os.Stat(orphan); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("orphan survived the sweep: %v", err)
	}

	// a freshly stored file is never swept
	uploadOK(t, server, "keep.txt", []byte("keep"))
	if removed := store.CleanupExpired(); removed != 0 {
		t.Fatalf("sweep removed %d live files, want 0", removed)
	}
}

func TestDeleteFile(t *testing.T) {
	_, store, server := newTestServer(t)

	got := uploadOK(t, server, "delete-me.txt", []byte("bye"))

	req, err := http.NewRequest(http.MethodDelete, server.URL+"/api/file/"+got.FileID, nil)
	if err != nil {
		t.Fatalf("build DELETE: %v", err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("DELETE /api/file/%s: %v", got.FileID, err)
	}
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("DELETE status = %d (%s), want 200", resp.StatusCode, body)
	}
	if _, err := os.Stat(filepath.Join(store.Dir(), got.FileID)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("payload still on disk after DELETE: %v", err)
	}
	if resp, _ := download(t, server, got.FileID); resp.StatusCode != http.StatusNotFound {
		t.Fatalf("download after DELETE: status %d, want 404", resp.StatusCode)
	}

	// deleting twice reports not found
	resp2, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("second DELETE: %v", err)
	}
	defer resp2.Body.Close()
	if resp2.StatusCode != http.StatusNotFound {
		t.Fatalf("second DELETE status = %d, want 404", resp2.StatusCode)
	}
}

func TestUploadWithoutFileFieldIsRejected(t *testing.T) {
	_, _, server := newTestServer(t)

	var body bytes.Buffer
	writer := multipart.NewWriter(&body)
	if err := writer.WriteField("notafile", "hello"); err != nil {
		t.Fatalf("WriteField: %v", err)
	}
	if err := writer.Close(); err != nil {
		t.Fatalf("close writer: %v", err)
	}

	resp, err := http.Post(server.URL+"/api/upload", writer.FormDataContentType(), &body)
	if err != nil {
		t.Fatalf("POST /api/upload: %v", err)
	}
	defer resp.Body.Close()
	payload, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("status = %d (%s), want 400", resp.StatusCode, payload)
	}
	if !strings.Contains(string(payload), "missing_file") {
		t.Fatalf("body = %s, want a missing_file error", payload)
	}
}

// TestConcurrentUploadsAndDownloads hammers the store from many goroutines
// while the janitor runs, so the metadata lock is exercised for real.
func TestConcurrentUploadsAndDownloads(t *testing.T) {
	store := newTestStore(t, DefaultMaxFileSize, time.Minute, 10*time.Millisecond)
	_, _, server := newTestServerWithStore(t, store)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go store.RunCleanup(ctx)

	const clients = 12
	var wait sync.WaitGroup
	problems := make(chan string, clients)

	for i := 0; i < clients; i++ {
		wait.Add(1)
		go func(i int) {
			defer wait.Done()
			fileName := fmt.Sprintf("并发客户端-%02d.bin", i)
			content := bytes.Repeat([]byte(fmt.Sprintf("payload-%02d|", i)), 2000)

			var body bytes.Buffer
			writer := multipart.NewWriter(&body)
			part, err := writer.CreateFormFile(uploadFieldName, fileName)
			if err != nil {
				problems <- err.Error()
				return
			}
			if _, err := part.Write(content); err != nil {
				problems <- err.Error()
				return
			}
			if err := writer.Close(); err != nil {
				problems <- err.Error()
				return
			}

			resp, err := http.Post(server.URL+"/api/upload", writer.FormDataContentType(), &body)
			if err != nil {
				problems <- fmt.Sprintf("client %d upload: %v", i, err)
				return
			}
			raw, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			if resp.StatusCode != http.StatusOK {
				problems <- fmt.Sprintf("client %d upload: status %d (%s)", i, resp.StatusCode, raw)
				return
			}

			var decoded uploadResponse
			if err := json.Unmarshal(raw, &decoded); err != nil {
				problems <- fmt.Sprintf("client %d decode: %v", i, err)
				return
			}
			if decoded.FileName != fileName {
				problems <- fmt.Sprintf("client %d: fileName = %q, want %q", i, decoded.FileName, fileName)
				return
			}

			for round := 0; round < 3; round++ {
				down, err := http.Get(server.URL + "/api/download/" + decoded.FileID)
				if err != nil {
					problems <- fmt.Sprintf("client %d download: %v", i, err)
					return
				}
				got, _ := io.ReadAll(down.Body)
				down.Body.Close()
				if down.StatusCode != http.StatusOK || !bytes.Equal(got, content) {
					problems <- fmt.Sprintf("client %d round %d: status %d, %d bytes",
						i, round, down.StatusCode, len(got))
					return
				}
			}
		}(i)
	}

	wait.Wait()
	close(problems)
	for message := range problems {
		t.Errorf("concurrent store: %s", message)
	}

	if removed := store.CleanupExpired(); removed != 0 {
		t.Fatalf("janitor removed %d files before their TTL, want 0", removed)
	}
}

func TestSanitizeFileName(t *testing.T) {
	cases := map[string]string{
		"report.pdf":               "report.pdf",
		"../../evil.txt":           "evil.txt",
		`..\..\evil.txt`:           "evil.txt",
		`C:\Users\panda\notes.txt`: "notes.txt",
		"/etc/passwd":              "passwd",
		"..":                       "unnamed",
		".":                        "unnamed",
		"":                         "unnamed",
		"   ":                      "unnamed",
		"a\x00b\x1fc.txt":          "abc.txt",
		"folder/subfolder/中文名.bin": "中文名.bin",
	}
	for in, want := range cases {
		if got := sanitizeFileName(in); got != want {
			t.Fatalf("sanitizeFileName(%q) = %q, want %q", in, got, want)
		}
	}

	long := strings.Repeat("界", 300) + ".txt"
	if got := sanitizeFileName(long); len([]rune(got)) != 200 {
		t.Fatalf("long name was not trimmed: %d runes", len([]rune(got)))
	}
}

func TestEscapeFileNameAttr(t *testing.T) {
	cases := map[string]string{
		"simple.txt":  "simple.txt",
		"a b.txt":     "a%20b.txt",
		"a+b&c!.txt":  "a+b&c!.txt",
		"a;b=c.txt":   "a%3Bb%3Dc.txt",
		"a'b(c)*.txt": "a%27b%28c%29%2A.txt",
		"中文.txt":      "%E4%B8%AD%E6%96%87.txt",
		"a#b$c%d.txt": "a#b$c%25d.txt",
	}
	for in, want := range cases {
		if got := escapeFileNameAttr(in); got != want {
			t.Fatalf("escapeFileNameAttr(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestValidFileID(t *testing.T) {
	if !validFileID("0123456789abcdef") {
		t.Fatal("a 16 character hex id was rejected")
	}
	for _, bad := range []string{"", "0123456789abcde", "0123456789abcdefg", "0123456789ABCDEF", "../0123456789ab"} {
		if validFileID(bad) {
			t.Fatalf("validFileID(%q) = true, want false", bad)
		}
	}
}

// 回归测试：清理只应删除"本服务写出的暂存文件"，
// 数据目录里的部署ID/授权文件等运行状态绝不能被当孤儿删掉
// （实测踩过：文件被扫掉 → 部署ID 重新随机 → 已激活的授权失效）
func TestSweepKeepsStateFiles(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	dir := t.TempDir()
	store, err := NewFileStoreWithOptions(dir, logger, 1<<20, 10*time.Millisecond, time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}

	stateFiles := []string{"deployment-id", "license.json", "license.json.tmp", "README.txt"}
	for _, n := range stateFiles {
		if err := os.WriteFile(filepath.Join(dir, n), []byte("state"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	// 一个"像暂存文件但没人认领"的孤儿（16 位十六进制），应当被清掉
	orphan := filepath.Join(dir, "0123456789abcdef")
	if err := os.WriteFile(orphan, []byte("orphan"), 0o600); err != nil {
		t.Fatal(err)
	}
	// 名字非法（不是 16 位 hex）的也应当保留
	notStaged := filepath.Join(dir, "not-a-fileid")
	if err := os.WriteFile(notStaged, []byte("keep"), 0o600); err != nil {
		t.Fatal(err)
	}
	time.Sleep(30 * time.Millisecond) // 超过 ttl 与 grace
	store.sweep(time.Now(), map[string]struct{}{})

	for _, n := range append(stateFiles, "not-a-fileid") {
		if _, err := os.Stat(filepath.Join(dir, n)); err != nil {
			t.Fatalf("状态文件 %s 被误删：%v", n, err)
		}
	}
	if _, err := os.Stat(orphan); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("真正的孤儿暂存文件应被清理")
	}
}
