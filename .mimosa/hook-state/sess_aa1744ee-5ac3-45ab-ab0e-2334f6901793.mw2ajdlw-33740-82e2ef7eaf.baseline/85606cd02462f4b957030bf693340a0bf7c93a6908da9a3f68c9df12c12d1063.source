package main

// admin_token_test.go —— 管理入口与中继令牌必须分离，授权时间只能前移。
//
// 背景（2026-09 线上事故）：中继令牌（RC_TOKEN）随主控端分发包（viewer-defaults.txt）
// 发给了每个客户，而 /admin 的鉴权用的也是它 —— 客户拿自己的令牌就能打开管理页：
// 恢复已撤销的激活码、把"旧的长有效期码"重新装上盖掉新码。这两组用例把修复锁死：
//  1. /admin* 只认独立的管理令牌；客户手里的中继令牌（含各种传法）一律 401；
//  2. 没配管理令牌的旧部署：仅本机回环 + 中继令牌可用（SSH 排障逃生口），公网拒绝；
//  3. Install 的时间不倒退：旧码（iat 更早）装不回去、到期更早的码装不上（无声缩短），
//     同一条码可重装；显式撤销当前码后（自救/显式回退）不受限。

import (
	"bytes"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func newAdminTwoTokenServer(t *testing.T, lm *LicenseManager, relayToken, adminToken string) *httptest.Server {
	t.Helper()
	logger := discardLogger()
	hub := NewHub(logger)
	hub.Token = relayToken
	hub.AdminToken = adminToken
	hub.License = lm
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	srv := httptest.NewServer(withLicense(newMux(hub, store), lm, hub))
	t.Cleanup(srv.Close)
	return srv
}

// 客户手里的中继令牌（随主控端分发包分发）绝不能打开管理页或改授权状态。
func TestAdminRejectsRelayToken(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMINTOK", "")
	srv := newAdminTwoTokenServer(t, lm, "CLIENT-TOKEN", "ADMIN-TOKEN")

	code := mustSign(t, priv, LicensePayload{
		Lic: "AT-0001", Srv: "DEP-ADMINTOK",
		Nbf: time.Now().Add(-time.Hour).Unix(),
		Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}

	// GET /admin：中继令牌的每一种传法都必须 401，管理令牌放行
	for _, tok := range []string{"CLIENT-TOKEN", "WRONG"} {
		for _, u := range []string{
			srv.URL + "/admin?token=" + url.QueryEscape(tok),
		} {
			resp, err := http.Get(u)
			if err != nil {
				t.Fatal(err)
			}
			resp.Body.Close()
			if resp.StatusCode != http.StatusUnauthorized {
				t.Fatalf("中继令牌 %q 访问 /admin 应 401，实际 %d", tok, resp.StatusCode)
			}
		}
		req, _ := http.NewRequest(http.MethodGet, srv.URL+"/admin", nil)
		req.Header.Set("X-RC-Token", "CLIENT-TOKEN")
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusUnauthorized {
			t.Fatalf("X-RC-Token 带中继令牌访问 /admin 应 401，实际 %d", resp.StatusCode)
		}
	}
	if body := httpGetBody(t, srv.URL+"/admin?token=ADMIN-TOKEN"); !strings.Contains(body, "授权管理") {
		t.Fatalf("管理令牌应能打开 /admin，实际：\n%s", body)
	}

	// 所有**改状态**的管理接口：中继令牌一律 401
	noRedirect := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error {
		return http.ErrUseLastResponse
	}}
	posts := []struct {
		name, path string
		form       url.Values
	}{
		{"install", "/admin/install", url.Values{"code": {code}}},
		{"revoke", "/admin/revoke", url.Values{"code": {code}}},
		{"unrevoke", "/admin/unrevoke", url.Values{"hash": {licenseCodeHash(code)}}},
		{"clear", "/admin/clear", url.Values{}},
	}
	for _, p := range posts {
		resp, err := noRedirect.PostForm(srv.URL+p.path+"?token=CLIENT-TOKEN", p.form)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusUnauthorized {
			t.Fatalf("中继令牌调 %s 应 401，实际 %d", p.name, resp.StatusCode)
		}
	}
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/admin/revoked/sync",
		bytes.NewBufferString(`{"add":[]}`))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-RC-Token", "CLIENT-TOKEN")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("中继令牌调 revoked/sync 应 401，实际 %d", resp.StatusCode)
	}

	// 中继令牌没能改任何状态：授权仍是装着的 code
	if ok, state, _ := lm.Check(); !ok || state != LicenseOK {
		t.Fatalf("中继令牌不应能改变授权状态，实际 ok=%v state=%s", ok, state)
	}
	if lm.RevokedCount() != 0 {
		t.Fatalf("中继令牌不应能撤销/恢复任何激活码，名单=%d", lm.RevokedCount())
	}

	// 客户端正常接口不受影响：中继令牌在 /api/agents 上依然有效
	resp2, err := http.Get(srv.URL + "/api/agents?token=CLIENT-TOKEN")
	if err != nil {
		t.Fatal(err)
	}
	resp2.Body.Close()
	if resp2.StatusCode != http.StatusOK {
		t.Fatalf("中继令牌在业务接口上应照常放行，实际 %d", resp2.StatusCode)
	}

	// 管理令牌做同样的操作应该成功（303 PRG）
	resp, err = noRedirect.PostForm(srv.URL+"/admin/revoke?token=ADMIN-TOKEN", url.Values{"code": {code}, "note": {"管理令牌撤销"}})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusSeeOther {
		t.Fatalf("管理令牌撤销应 303，实际 %d", resp.StatusCode)
	}
	if ok, state, _ := lm.Check(); ok || state != LicenseRevoked {
		t.Fatalf("管理令牌撤销后应立刻 revoked，实际 ok=%v state=%s", ok, state)
	}
}

// 未配置管理令牌（旧部署兼容）：仅"本机回环 + 中继令牌"放行，公网一律拒绝。
func TestAdminLoopbackFallbackWithoutAdminToken(t *testing.T) {
	withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMINTOK2", "")

	logger := discardLogger()
	hub := NewHub(logger)
	hub.Token = "CLIENT-TOKEN" // 未配置 AdminToken
	hub.License = lm
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	handler := withLicense(newMux(hub, store), lm, hub)

	call := func(remote string) int {
		req := httptest.NewRequest(http.MethodGet, "/admin?token=CLIENT-TOKEN", nil)
		req.RemoteAddr = remote
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)
		return rec.Code
	}

	if got := call("127.0.0.1:5555"); got != http.StatusOK {
		t.Fatalf("本机回环 + 中继令牌应放行（排障逃生口），实际 %d", got)
	}
	if got := call("[::1]:5555"); got != http.StatusOK {
		t.Fatalf("IPv6 回环 + 中继令牌应放行，实际 %d", got)
	}
	for _, remote := range []string{"203.0.113.7:4444", "192.0.2.10:9999"} {
		if got := call(remote); got != http.StatusUnauthorized {
			t.Fatalf("公网地址 %s 带中继令牌应 401，实际 %d", remote, got)
		}
	}
}

// 时间不倒退：旧码装不回去、授权不能被无声缩短；显式撤销当前码后不受限。
func TestInstallTimeCannotGoBackwards(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-TIME", "")
	now := time.Now()

	current := mustSign(t, priv, LicensePayload{
		Lic: "TIME-NEW", Srv: "DEP-TIME",
		Nbf: now.Add(-time.Hour).Unix(),
		Exp: now.Add(365 * 24 * time.Hour).Unix(),
		Iat: now.Add(-24 * time.Hour).Unix(),
	})
	older := mustSign(t, priv, LicensePayload{
		Lic: "TIME-OLD", Srv: "DEP-TIME",
		Nbf: now.Add(-time.Hour).Unix(),
		Exp: now.Add(730 * 24 * time.Hour).Unix(), // 期限更长 —— 正是"旧码续命"的诱惑所在
		Iat: now.Add(-96 * time.Hour).Unix(),      // 签发更早
	})
	shorter := mustSign(t, priv, LicensePayload{
		Lic: "TIME-SHORT", Srv: "DEP-TIME",
		Nbf: now.Add(-time.Hour).Unix(),
		Exp: now.Add(30 * 24 * time.Hour).Unix(),
		Iat: now.Unix(),
	})

	if err := lm.Install(current); err != nil {
		t.Fatalf("安装当前码应成功：%v", err)
	}

	// 旧码（签发更早、期限更长）必须装不回去
	err := lm.Install(older)
	if err == nil || !strings.Contains(err.Error(), "旧") {
		t.Fatalf("装回更旧的码必须被拒绝，实际：%v", err)
	}
	// 无声缩短（到期更早）必须被拒绝
	err = lm.Install(shorter)
	if err == nil || !strings.Contains(err.Error(), "缩短") {
		t.Fatalf("装到期更早的码必须被拒绝，实际：%v", err)
	}
	// 同一条码可以重装（重装/排障是常态）
	if err := lm.Install(current); err != nil {
		t.Fatalf("重装当前同一条码应成功：%v", err)
	}
	if ok, state, _ := lm.Check(); !ok || state != LicenseOK {
		t.Fatalf("被拒的安装不应影响当前授权，实际 ok=%v state=%s", ok, state)
	}

	// 显式撤销当前码（自救/显式回退）之后，旧码才装得回来 —— 这是唯一的正路
	if _, err := lm.Revoke(current, "测试：显式回退"); err != nil {
		t.Fatal(err)
	}
	if ok, state, _ := lm.Check(); ok || state != LicenseRevoked {
		t.Fatalf("撤销后应为 revoked，实际 ok=%v state=%s", ok, state)
	}
	if err := lm.Install(older); err != nil {
		t.Fatalf("显式撤销当前码后应允许安装其它码：%v", err)
	}
	if ok, state, _ := lm.Check(); !ok || state != LicenseOK {
		t.Fatalf("回退后应为 ok，实际 ok=%v state=%s", ok, state)
	}
	if _, _, payload, _ := lm.Status(); payload == nil || payload.Lic != "TIME-OLD" {
		t.Fatalf("回退后装着的应是旧码，实际 %+v", payload)
	}

	// 旧码装回去之后，比它期限更长的续期码照样能装 —— 时间继续往前走
	renewed := mustSign(t, priv, LicensePayload{
		Lic: "TIME-RENEW", Srv: "DEP-TIME",
		Nbf: now.Add(-time.Minute).Unix(),
		Exp: now.Add(800 * 24 * time.Hour).Unix(), // 比回退后的 730 天更晚：合法续期
		Iat: now.Unix(),
	})
	if err := lm.Install(renewed); err != nil {
		t.Fatalf("正常续期应不受影响：%v", err)
	}
}
