package main

import (
	"bytes"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func newUpdateTestServer(t *testing.T) (*UpdateStore, *httptest.Server) {
	t.Helper()
	dir := t.TempDir()
	us, err := NewUpdateStore(dir, "tok", log.New(io.Discard, "", 0))
	if err != nil {
		t.Fatalf("NewUpdateStore: %v", err)
	}
	mux := http.NewServeMux()
	// adminAuthorized 需要 hub；测试直接注册业务端点，管理端点单独覆盖
	mux.HandleFunc("GET /api/update/manifest", us.handleManifest)
	mux.HandleFunc("POST /api/update/manifest", us.handleManifestUpload)
	mux.HandleFunc("GET /api/update/file/{name}", us.handleFile)
	return us, httptest.NewServer(mux)
}

func TestUpdateManifest_NoManifestIs404(t *testing.T) {
	_, srv := newUpdateTestServer(t)
	defer srv.Close()

	resp, err := http.Get(srv.URL + "/api/update/manifest?token=tok")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("空库应 404（客户端约定=无更新），得 %d", resp.StatusCode)
	}
}

func TestUpdateManifest_TokenRequired(t *testing.T) {
	_, srv := newUpdateTestServer(t)
	defer srv.Close()

	resp, err := http.Get(srv.URL + "/api/update/manifest")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("无 token 应 401，得 %d", resp.StatusCode)
	}
}

func TestUpdateManifest_ServeAndRollout(t *testing.T) {
	us, srv := newUpdateTestServer(t)
	defer srv.Close()

	full := `{"agent":{"code":"RCU1.a.b","packageUrl":"/api/update/file/agent-1.2.0.zip"},"rolloutPercent":50}`
	if err := os.WriteFile(us.manifestPath(), []byte(full), 0o644); err != nil {
		t.Fatal(err)
	}
	us.manifest = []byte(full)

	get := func(url string) (int, string) {
		resp, err := http.Get(url)
		if err != nil {
			t.Fatal(err)
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(resp.Body)
		return resp.StatusCode, string(b)
	}

	// 不带 id：拿到完整清单
	code, body := get(srv.URL + "/api/update/manifest?token=tok")
	if code != 200 || !strings.Contains(body, "RCU1.a.b") {
		t.Fatalf("不带 id 应给完整清单，得 %d %s", code, body)
	}

	// 灰度 50%：同一 id 的结果必须稳定，且 50% 的 id 拿到空清单
	withAgent, without := 0, 0
	for i := 0; i < 100; i++ {
		id := "AGENT-" + strings.Repeat("x", i) + string(rune('a'+i%26))
		_, b := get(srv.URL + "/api/update/manifest?token=tok&id=" + id)
		first := b
		_, second := get(srv.URL + "/api/update/manifest?token=tok&id=" + id)
		if first != second {
			t.Fatalf("灰度判定不稳定：id=%s 先 %s 后 %s", id, first, second)
		}
		if strings.Contains(first, "RCU1") {
			withAgent++
		} else {
			without++
		}
	}
	if withAgent == 0 || without == 0 {
		t.Fatalf("灰度 50%% 应两边都有：有更新 %d / 无更新 %d", withAgent, without)
	}
}

func TestUpdateFile_ServeAndPathTraversal(t *testing.T) {
	us, srv := newUpdateTestServer(t)
	defer srv.Close()

	if err := os.WriteFile(filepath.Join(us.dir, "agent-1.2.0.zip"), []byte("ZIPDATA"), 0o644); err != nil {
		t.Fatal(err)
	}

	resp, err := http.Get(srv.URL + "/api/update/file/agent-1.2.0.zip?token=tok")
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(resp.Body)
	_ = resp.Body.Close()
	if resp.StatusCode != 200 || string(b) != "ZIPDATA" {
		t.Fatalf("正常取包失败：%d %q", resp.StatusCode, b)
	}

	// 路径穿越必须被挡住（mux 的 {name} 不含 /，这里再防显式构造）
	for _, name := range []string{"..%5Cmanifest.json", "manifest.json"} {
		// manifest.json 不该通过 /file/ 下发（管理内容；即便知道名字也不给）
		resp2, err := http.Get(srv.URL + "/api/update/file/" + name + "?token=tok")
		if err != nil {
			t.Fatal(err)
		}
		_ = resp2.Body.Close()
		if resp2.StatusCode == 200 {
			t.Fatalf("不应下发 %s", name)
		}
	}
}

func TestUpdateManifestUpload_ShapeCheck(t *testing.T) {
	us, srv := newUpdateTestServer(t)
	defer srv.Close()

	// 不是 JSON → 拒绝
	resp, err := http.Post(srv.URL+"/api/update/manifest", "application/json", bytes.NewReader([]byte("not json")))
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("非 JSON 应 400，得 %d", resp.StatusCode)
	}

	// 空清单（既无 agent 也无 viewer）→ 拒绝
	resp2, err := http.Post(srv.URL+"/api/update/manifest", "application/json",
		bytes.NewReader([]byte(`{"rolloutPercent":100}`)))
	if err != nil {
		t.Fatal(err)
	}
	_ = resp2.Body.Close()
	if resp2.StatusCode != http.StatusBadRequest {
		t.Fatalf("空清单应 400（防手滑传空把发布清掉），得 %d", resp2.StatusCode)
	}
	_ = us
}
