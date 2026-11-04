package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"hash/fnv"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

// UpdateStore 承载被控端/主控端的自动更新分发（2026-10 新增）。
//
// 设计要点：
//   - 清单（manifest.json）由**厂商上位机签名后**整个上传，服务器只做存储与转发 ——
//     服务器被攻破也伪造不了更新（客户端用内置 ECDSA 公钥验签，与授权码同一套）。
//   - 与 /api/upload 的 10 分钟暂存不同，更新目录是**持久**的（data/updates/），
//     不参与 TTL 清理。
//   - 灰度：清单里的 rolloutPercent 由服务器执行 —— 按 AgentId 哈希决定这台机器
//     本轮能不能看到新版本（改灰度不需要重签清单）。
type UpdateStore struct {
	dir    string
	token  string
	logger *log.Logger

	mu       sync.RWMutex
	manifest []byte // 当前生效的 manifest.json（nil = 从未配置）
}

const updateManifestLimit = 64 * 1024   // 清单很小，硬上限 64KB
const updatePackageLimit = 512 << 20    // 更新包上限 512MB（与文件暂存一致）

func NewUpdateStore(dir, token string, logger *log.Logger) (*UpdateStore, error) {
	if logger == nil {
		logger = log.Default()
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, fmt.Errorf("update store: %w", err)
	}
	us := &UpdateStore{dir: dir, token: token, logger: logger}
	// 启动时加载已保存的清单（重启服务不丢发布状态）
	if b, err := os.ReadFile(us.manifestPath()); err == nil {
		us.manifest = b
		logger.Printf("update: manifest loaded (%d bytes)", len(b))
	}
	return us, nil
}

func (s *UpdateStore) manifestPath() string { return filepath.Join(s.dir, "manifest.json") }

// Register wires the update endpoints. /api/update/* 走 RC_TOKEN（与其它业务接口一致），
// /admin/update/* 走管理令牌（adminAuthorized，与授权管理页同一道门）。
func (s *UpdateStore) Register(mux *http.ServeMux, hub *Hub) {
	mux.HandleFunc("GET /api/update/manifest", s.handleManifest)
	mux.HandleFunc("GET /api/update/file/{name}", s.handleFile)

	mux.HandleFunc("POST /admin/update/package", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeJSONError(w, http.StatusUnauthorized, "unauthorized", "admin token required")
			return
		}
		s.handlePackageUpload(w, r)
	})
	mux.HandleFunc("POST /admin/update/manifest", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeJSONError(w, http.StatusUnauthorized, "unauthorized", "admin token required")
			return
		}
		s.handleManifestUpload(w, r)
	})
	mux.HandleFunc("GET /admin/update/state", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeJSONError(w, http.StatusUnauthorized, "unauthorized", "admin token required")
			return
		}
		s.handleState(w, r)
	})
	mux.HandleFunc("GET /admin/update", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			http.Error(w, "admin token required (?token=...)", http.StatusUnauthorized)
			return
		}
		s.handlePage(w, r)
	})
}

// ---------------------------------------------------------------- 业务接口（被控端/主控端）

// handleManifest 返回当前清单。?id=<AgentId> 用于灰度判定：
// fnv32(id) % 100 >= rolloutPercent 的机器拿到一份"没有条目"的清单（= 无更新）。
func (s *UpdateStore) handleManifest(w http.ResponseWriter, r *http.Request) {
	if !Authorized(s.token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	s.mu.RLock()
	manifest := s.manifest
	s.mu.RUnlock()
	if len(manifest) == 0 {
		// 服务器从未配置更新 = 功能未启用；客户端约定把 404 当"无更新"，绝不当错误
		writeJSONError(w, http.StatusNotFound, "no_manifest", "update manifest not configured")
		return
	}

	var head struct {
		RolloutPercent int `json:"rolloutPercent"`
	}
	_ = json.Unmarshal(manifest, &head)
	if head.RolloutPercent <= 0 {
		head.RolloutPercent = 100
	}
	if id := r.URL.Query().Get("id"); id != "" && head.RolloutPercent < 100 {
		if int(fnv32(id)%100) >= head.RolloutPercent {
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusOK)
			_, _ = io.WriteString(w, fmt.Sprintf(`{"rolloutPercent":%d}`, head.RolloutPercent))
			return
		}
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(manifest)
}

// handleFile 下发更新包。文件名只允许一层（防路径穿越），且必须已经在 updates 目录里。
func (s *UpdateStore) handleFile(w http.ResponseWriter, r *http.Request) {
	if !Authorized(s.token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	name := filepath.Base(r.PathValue("name")) // Base() 剥掉一切路径成分
	if name == "" || name == "." || name == ".." || strings.ContainsAny(name, `/\`) {
		writeJSONError(w, http.StatusBadRequest, "bad_name", "invalid package name")
		return
	}
	full := filepath.Join(s.dir, name)
	if _, err := os.Stat(full); err != nil {
		writeJSONError(w, http.StatusNotFound, "no_file", "package not found")
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	http.ServeFile(w, r, full)
}

// ---------------------------------------------------------------- 管理接口

// handlePackageUpload 上传更新包（multipart 字段 file）。返回 sha256 ——
// 上位机签清单要用的哈希直接在这里算好，省一步本地计算。
func (s *UpdateStore) handlePackageUpload(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, updatePackageLimit+(4<<20))
	file, header, err := r.FormFile("file")
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid_multipart", err.Error())
		return
	}
	defer func() { _ = file.Close() }()

	name := filepath.Base(header.Filename)
	if name == "" || strings.ContainsAny(name, `/\`) {
		writeJSONError(w, http.StatusBadRequest, "bad_name", "invalid file name")
		return
	}
	tmp := filepath.Join(s.dir, name+".part")
	dst, err := os.Create(tmp)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "save_failed", err.Error())
		return
	}
	h := sha256.New()
	size, err := io.Copy(io.MultiWriter(dst, h), file)
	if cerr := dst.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		_ = os.Remove(tmp)
		writeJSONError(w, http.StatusInternalServerError, "save_failed", err.Error())
		return
	}
	if err := os.Rename(tmp, filepath.Join(s.dir, name)); err != nil {
		writeJSONError(w, http.StatusInternalServerError, "save_failed", err.Error())
		return
	}
	s.logger.Printf("update: package %s stored (%d bytes, sha256=%s)", name, size, hex.EncodeToString(h.Sum(nil)))
	writeJSON(w, http.StatusOK, map[string]any{
		"fileName": name,
		"size":     size,
		"sha256":   hex.EncodeToString(h.Sum(nil)),
	})
}

// handleManifestUpload 保存新清单（body 即 manifest.json 全文）。只做 JSON 形状校验，
// 签名由客户端验 —— 但这里顺手检查 agent/viewer 条目至少存在一个，防止手滑传空清单。
func (s *UpdateStore) handleManifestUpload(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, updateManifestLimit)
	body, err := io.ReadAll(r.Body)
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, "read_failed", err.Error())
		return
	}
	var m struct {
		Agent  *json.RawMessage `json:"agent"`
		Viewer *json.RawMessage `json:"viewer"`
	}
	if err := json.Unmarshal(body, &m); err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid_json", err.Error())
		return
	}
	if m.Agent == nil && m.Viewer == nil {
		writeJSONError(w, http.StatusBadRequest, "empty_manifest", "manifest has neither agent nor viewer entry")
		return
	}
	if err := os.WriteFile(s.manifestPath(), body, 0o644); err != nil {
		writeJSONError(w, http.StatusInternalServerError, "save_failed", err.Error())
		return
	}
	s.mu.Lock()
	s.manifest = body
	s.mu.Unlock()
	s.logger.Printf("update: manifest updated (%d bytes)", len(body))
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *UpdateStore) handleState(w http.ResponseWriter, _ *http.Request) {
	s.mu.RLock()
	manifest := s.manifest
	s.mu.RUnlock()
	entries, _ := os.ReadDir(s.dir)
	files := make([]string, 0, len(entries))
	for _, e := range entries {
		if !e.IsDir() && e.Name() != "manifest.json" && !strings.HasSuffix(e.Name(), ".part") {
			info, err := e.Info()
			if err == nil {
				files = append(files, fmt.Sprintf("%s (%d bytes)", e.Name(), info.Size()))
			}
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"manifest": string(manifest),
		"files":    files,
	})
}

// handlePage 极简管理页：传包 → 拿 sha256 → 上位机签清单 → 贴回来保存。
func (s *UpdateStore) handlePage(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = io.WriteString(w, `<!doctype html><html><head><meta charset="utf-8"><title>更新分发</title></head>
<body style="font-family:system-ui;max-width:760px;margin:24px auto;line-height:1.6">
<h2>更新分发（热更新）</h2>
<p>流程：① 传更新包（zip）→ 拿到 sha256；② 在上位机 LicenseKeygen「更新签名」页签
生成清单（带这个 sha256）；③ 把清单 JSON 贴到下面保存。清单里的 rolloutPercent 控制灰度。</p>
<h3>① 上传更新包</h3>
<input type="file" id="pkg"> <button onclick="upPkg()">上传</button> <span id="pkgRs"></span>
<h3>② 保存清单 manifest.json</h3>
<textarea id="mf" style="width:100%;height:180px" placeholder='{"agent":{"code":"RCU1...","packageUrl":"/api/update/file/agent-1.2.0.zip"},"rolloutPercent":10}'></textarea><br>
<button onclick="saveMf()">保存清单</button> <span id="mfRs"></span>
<h3>当前状态</h3><pre id="state">…</pre>
<script>
const q=new URLSearchParams(location.search).get('token')||'';
async function upPkg(){const f=document.getElementById('pkg').files[0];if(!f)return;
const fd=new FormData();fd.append('file',f);
document.getElementById('pkgRs').textContent='上传中…';
const r=await fetch('/admin/update/package?token='+q,{method:'POST',body:fd});
document.getElementById('pkgRs').textContent=JSON.stringify(await r.json());load()}
async function saveMf(){const r=await fetch('/admin/update/manifest?token='+q,
{method:'POST',body:document.getElementById('mf').value});
document.getElementById('mfRs').textContent=JSON.stringify(await r.json());load()}
async function load(){const r=await fetch('/admin/update/state?token='+q);
document.getElementById('state').textContent=JSON.stringify(await r.json(),null,2)}
load()
</script></body></html>`)
}

func fnv32(s string) uint32 {
	h := fnv.New32a()
	_, _ = h.Write([]byte(s))
	return h.Sum32()
}
