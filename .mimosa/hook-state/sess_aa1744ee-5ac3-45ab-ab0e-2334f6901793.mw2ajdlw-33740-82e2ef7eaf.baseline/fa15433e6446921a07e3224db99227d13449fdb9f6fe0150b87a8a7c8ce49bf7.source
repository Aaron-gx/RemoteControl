package main

import (
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

// 授权管理页（/admin）的行为：令牌保护 + 过期时仍可续期。
// 这几条是"服务器到期后能不能自救"的核心保障，必须锁住。

func mustNewLM(t *testing.T, deploymentID, publicIP string) *LicenseManager {
	t.Helper()
	lm, err := NewLicenseManager(t.TempDir(), deploymentID, publicIP, log.New(io.Discard, "", 0))
	if err != nil {
		t.Fatal(err)
	}
	return lm
}

func newAdminTestServer(t *testing.T, lm *LicenseManager, token string) *httptest.Server {
	t.Helper()
	logger := log.New(io.Discard, "", 0)
	hub := NewHub(logger)
	hub.Token = token
	hub.License = lm
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	srv := httptest.NewServer(withLicense(newMux(hub, store), lm, hub))
	t.Cleanup(srv.Close)
	return srv
}

func TestAdminRequiresToken(t *testing.T) {
	withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN", "")

	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	resp, err := http.Get(srv.URL + "/admin")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("无令牌访问 /admin 应 401，实际 %d", resp.StatusCode)
	}

	resp, err = http.Get(srv.URL + "/admin?token=WRONG")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("错令牌访问 /admin 应 401，实际 %d", resp.StatusCode)
	}
}

func TestAdminShowsStatusAndInstalls(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN", "")
	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	body := httpGetBody(t, srv.URL+"/admin?token=SECRET-TOKEN")
	for _, want := range []string{"授权管理", "DEP-ADMIN", "未授权"} {
		if !strings.Contains(body, want) {
			t.Fatalf("状态页应包含 %q，实际：\n%s", want, body)
		}
	}

	// 安装一个合法激活码
	code := mustSign(t, priv, LicensePayload{
		Lic: "ADMIN-0001",
		Srv: "DEP-ADMIN",
		Nbf: time.Now().Add(-time.Hour).Unix(),
		Exp: time.Now().Add(365 * 24 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatalf("安装应成功：%v", err)
	}
	body = httpGetBody(t, srv.URL+"/admin?token=SECRET-TOKEN")
	for _, want := range []string{"有效", "ADMIN-0001", "剩余"} {
		if !strings.Contains(body, want) {
			t.Fatalf("安装后状态页应包含 %q，实际：\n%s", want, body)
		}
	}
}

func TestAdminRejectsBadCode(t *testing.T) {
	withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN", "")
	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	resp, err := http.PostForm(srv.URL+"/admin/install?token=SECRET-TOKEN",
		url.Values{"code": {"RC1.not-a-real-code.not-a-signature"}})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	// 跟随重定向后应看到失败提示
	body := httpGetBody(t, srv.URL+"/admin?token=SECRET-TOKEN")
	if !strings.Contains(body, "安装失败") && !strings.Contains(body, "未授权") {
		t.Fatalf("乱码安装应被拒绝并在页面上提示，实际：\n%s", body)
	}
	if lm.Refresh() == LicenseOK {
		t.Fatal("非法激活码不应让中继变成已授权")
	}
}

// 关键回归：**授权过期后 /admin 必须仍然可用**，否则服务器到期就再也进不去续期了。
func TestAdminReachableWhenExpired(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN", "")

	// 装一个"马上就到期"的码，等它过期（更贴近真实：到期是时间走出来的，不是装出来的）
	soon := mustSign(t, priv, LicensePayload{
		Lic: "EXPIRING-0001",
		Srv: "DEP-ADMIN",
		Nbf: time.Now().Add(-time.Hour).Unix(),
		Exp: time.Now().Add(2 * time.Second).Unix(),
	})
	if err := lm.Install(soon); err != nil {
		t.Fatalf("安装应成功（只是很快到期）：%v", err)
	}
	time.Sleep(3 * time.Second)
	if got := lm.Refresh(); got != LicenseExpired {
		t.Fatalf("等待到期后状态应为 expired，实际 %s", got)
	}

	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	// 业务接口被闸门挡住
	resp, err := http.Get(srv.URL + "/api/agents?token=SECRET-TOKEN")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("过期后业务接口应 403，实际 %d", resp.StatusCode)
	}

	// 管理页仍可访问，并且能装新码完成续期
	body := httpGetBody(t, srv.URL+"/admin?token=SECRET-TOKEN")
	if !strings.Contains(body, "已过期") {
		t.Fatalf("过期后管理页应显示已过期，实际：\n%s", body)
	}

	fresh := mustSign(t, priv, LicensePayload{
		Lic: "RENEW-0002",
		Srv: "DEP-ADMIN",
		Nbf: time.Now().Add(-time.Hour).Unix(),
		Exp: time.Now().Add(365 * 24 * time.Hour).Unix(),
	})
	resp, err = http.PostForm(srv.URL+"/admin/install?token=SECRET-TOKEN", url.Values{"code": {fresh}})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if lm.Refresh() != LicenseOK {
		t.Fatalf("续期后应恢复为 ok，实际 %s", lm.Refresh())
	}
	resp, err = http.Get(srv.URL + "/api/agents?token=SECRET-TOKEN")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("续期后业务接口应放行，实际 %d", resp.StatusCode)
	}
}

func TestAdminClear(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN", "")
	code := mustSign(t, priv, LicensePayload{
		Lic: "ADMIN-CLEAR",
		Srv: "DEP-ADMIN",
		Nbf: time.Now().Add(-time.Hour).Unix(),
		Exp: time.Now().Add(24 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	resp, err := http.PostForm(srv.URL+"/admin/clear?token=SECRET-TOKEN", url.Values{})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if lm.Refresh() != LicenseMissing {
		t.Fatalf("卸载后应为 missing，实际 %s", lm.Refresh())
	}
}

func httpGetBody(t *testing.T, url string) string {
	t.Helper()
	resp, err := http.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("GET %s 应 200，实际 %d，正文：%s", url, resp.StatusCode, string(data))
	}
	return string(data)
}
