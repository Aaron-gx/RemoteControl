package main

// revoke_test.go —— "删除即失效"（撤销名单）的核心保障。
//
// 这一组测试锁住四件事：
//  1. 被撤销的码装不回去（哪怕重新粘贴、带空白、甚至重启服务器）；
//  2. 已经装在服务器上的码被撤销时，**立刻**变成不可用（并触发断连回调）；
//  3. 撤销只杀被删的那一条，不会误伤同编号/同客户的其他码；
//  4. 在线被控端会被"主动告知"（不然它拿着本地缓存继续跑到期）。

import (
	"bytes"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

// 注意：撤销名单按"激活码原文的 sha256"记。这些测试验证归一化（首尾空白）与
// 持久化（重启后仍拦得住）都对。

func TestRevokeBlocksInstall(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-REVOKE", "")

	code := mustSign(t, priv, LicensePayload{
		Lic: "REV-0001", Sub: "退款客户", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	other := mustSign(t, priv, LicensePayload{
		Lic: "REV-0001", Sub: "退款客户", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(96 * time.Hour).Unix(),
	})

	// 撤销前能装
	if err := lm.Install(code); err != nil {
		t.Fatalf("撤销前安装应当成功：%v", err)
	}
	// 撤销后装不回去
	entry, err := lm.Revoke(code, "客户退款")
	if err != nil {
		t.Fatalf("撤销失败：%v", err)
	}
	if entry.Lic != "REV-0001" || entry.Note != "客户退款" {
		t.Fatalf("撤销记录内容不对：%+v", entry)
	}
	if err := lm.Install(code); err == nil || !strings.Contains(err.Error(), "撤销") {
		t.Fatalf("撤销后必须拒绝安装，实际：%v", err)
	}
	// 首尾空白归一：客户粘贴时带换行也要拦住
	if err := lm.Install("  " + code + "\n"); err == nil || !strings.Contains(err.Error(), "撤销") {
		t.Fatalf("带空白的同一条码也必须被拦，实际：%v", err)
	}
	// 只杀被删的那一条：同编号、同客户、只是到期时间不同的另一条码不受影响
	if err := lm.Install(other); err != nil {
		t.Fatalf("同编号的其他码不该被误伤：%v", err)
	}
	// 幂等：再撤一次不报错、不重复
	again, err := lm.Revoke(code, "再撤一次")
	if err != nil || again.Hash != entry.Hash {
		t.Fatalf("重复撤销应当幂等，实际 entry=%+v err=%v", again, err)
	}
	if n := lm.RevokedCount(); n != 1 {
		t.Fatalf("名单里应当只有 1 条，实际 %d", n)
	}
}

func TestRevokeInstalledCodeLocksImmediately(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-REVOKE2", "")

	code := mustSign(t, priv, LicensePayload{
		Lic: "REV-0002", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatalf("安装失败：%v", err)
	}
	if ok, state, _ := lm.Check(); !ok || state != LicenseOK {
		t.Fatalf("安装后应当可用，实际 ok=%v state=%s", ok, state)
	}

	locked := make(chan string, 4)
	lm.SetOnLocked(func(reason string) { locked <- reason })

	if _, err := lm.Revoke(code, "停止服务"); err != nil {
		t.Fatalf("撤销失败：%v", err)
	}

	// 立刻不可用（不等 30 秒复验）
	ok, state, reason := lm.Check()
	if ok || state != LicenseRevoked {
		t.Fatalf("撤销后应当立刻不可用，实际 ok=%v state=%s reason=%s", ok, state, reason)
	}
	if !strings.Contains(reason, "撤销") {
		t.Fatalf("原因里应当写明被撤销，实际：%s", reason)
	}
	// 触发"由可用变不可用"回调（中继据此踢掉所有连接）
	select {
	case r := <-locked:
		if !strings.Contains(r, "撤销") {
			t.Fatalf("回调原因不对：%s", r)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("撤销后没有触发 OnLocked 回调（在线连接不会被踢掉）")
	}
}

func TestRevokePersistsAcrossRestart(t *testing.T) {
	priv := withTestKey(t)
	dir := t.TempDir()
	lm, err := NewLicenseManager(dir, "DEP-REVOKE3", "", log.New(io.Discard, "", 0))
	if err != nil {
		t.Fatal(err)
	}
	code := mustSign(t, priv, LicensePayload{
		Lic: "REV-0003", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if _, err := lm.Revoke(code, "删除记录"); err != nil {
		t.Fatalf("撤销失败：%v", err)
	}

	// 重启（换一个实例读同一个 data 目录）：名单必须还在
	lm2, err := NewLicenseManager(dir, "DEP-REVOKE3", "", log.New(io.Discard, "", 0))
	if err != nil {
		t.Fatal(err)
	}
	if _, bad := lm2.IsRevoked(code); !bad {
		t.Fatal("重启后撤销名单丢失了：被删除的激活码又能用了")
	}
	if err := lm2.Install(code); err == nil {
		t.Fatal("重启后仍必须拒绝安装被撤销的激活码")
	}
	if n := lm2.RevokedCount(); n != 1 {
		t.Fatalf("重启后名单条数应当为 1，实际 %d", n)
	}
}

func TestUnrevokeRestoresInstall(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-REVOKE4", "")

	code := mustSign(t, priv, LicensePayload{
		Lic: "REV-0004", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if _, err := lm.Revoke(code, "误删"); err != nil {
		t.Fatal(err)
	}
	ok, err := lm.Unrevoke(code) // 直接用激活码原文恢复
	if err != nil || !ok {
		t.Fatalf("恢复失败：ok=%v err=%v", ok, err)
	}
	if err := lm.Install(code); err != nil {
		t.Fatalf("恢复后应当可以重新安装：%v", err)
	}
	if n := lm.RevokedCount(); n != 0 {
		t.Fatalf("恢复后名单应为空，实际 %d", n)
	}
	if ok, _ := lm.Unrevoke(code); ok {
		t.Fatal("重复恢复不该再返回 true")
	}
}

func TestAdminRevokeRequiresTokenAndTakesEffect(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-ADMIN-REV", "")
	code := mustSign(t, priv, LicensePayload{
		Lic: "REV-ADMIN-1", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	srv := newAdminTestServer(t, lm, "SECRET-TOKEN")

	// 不跟随重定向，便于断言 PRG 的 303
	noRedirect := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error {
		return http.ErrUseLastResponse
	}}

	// 没有令牌 → 401，且什么都没发生
	resp, err := noRedirect.PostForm(srv.URL+"/admin/revoke", url.Values{"code": {code}})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("没有令牌应当 401，实际 %d", resp.StatusCode)
	}
	if _, bad := lm.IsRevoked(code); bad {
		t.Fatal("无令牌的请求不该生效")
	}

	// 带令牌 → 303 且立即失效
	resp, err = noRedirect.PostForm(srv.URL+"/admin/revoke?token=SECRET-TOKEN", url.Values{"code": {code}, "note": {"网页撤销"}})
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusSeeOther {
		t.Fatalf("带令牌应当 303，实际 %d", resp.StatusCode)
	}
	if ok, state, _ := lm.Check(); ok || state != LicenseRevoked {
		t.Fatalf("网页撤销后应当立刻不可用，实际 ok=%v state=%s", ok, state)
	}
	// 公开的 /api/license 也要能看到 revoked（上位机/排障都靠它）
	body, err := http.Get(srv.URL + "/api/license")
	if err != nil {
		t.Fatal(err)
	}
	defer body.Body.Close()
	var api struct {
		State        string `json:"state"`
		Hash         string `json:"hash"`
		RevokedCount int    `json:"revokedCount"`
	}
	if err := json.NewDecoder(body.Body).Decode(&api); err != nil {
		t.Fatal(err)
	}
	if api.State != string(LicenseRevoked) || api.RevokedCount != 1 {
		t.Fatalf("/api/license 状态不对：%+v", api)
	}
	// 装不了
	if err := lm.Install(code); err == nil {
		t.Fatal("网页撤销后也必须装不回去")
	}
	// 管理员页面上能看到这条记录
	page, err := http.Get(srv.URL + "/admin?token=SECRET-TOKEN")
	if err != nil {
		t.Fatal(err)
	}
	html, _ := io.ReadAll(page.Body)
	page.Body.Close()
	if !strings.Contains(string(html), "REV-ADMIN-1") {
		t.Fatal("撤销名单没有渲染到管理页面")
	}
}

func TestRevokedSyncEndpoint(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-SYNC", "")
	codeA := mustSign(t, priv, LicensePayload{
		Lic: "SYNC-A", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	codeB := mustSign(t, priv, LicensePayload{
		Lic: "SYNC-B", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if err := lm.Install(codeA); err != nil {
		t.Fatal(err)
	}
	srv := newAdminTestServer(t, lm, "SYNC-TOKEN")

	post := func(body string, token string) *http.Response {
		req, err := http.NewRequest(http.MethodPost, srv.URL+"/admin/revoked/sync", bytes.NewBufferString(body))
		if err != nil {
			t.Fatal(err)
		}
		req.Header.Set("Content-Type", "application/json")
		if token != "" {
			req.Header.Set("X-RC-Token", token)
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		return resp
	}

	// 无令牌 → 401
	resp := post(`{"add":[]}`, "")
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("无令牌应当 401，实际 %d", resp.StatusCode)
	}

	// 上位机推两条（A 用指纹、B 用别的方式算出的指纹），并集生效
	body, _ := json.Marshal(map[string]any{
		"add": []map[string]any{
			{"hash": licenseCodeHash(codeA), "lic": "SYNC-A", "note": "上位机删除", "at": time.Now().Unix()},
			{"hash": licenseCodeHash(codeB), "lic": "SYNC-B", "at": time.Now().Unix()},
		},
	})
	resp = post(string(body), "SYNC-TOKEN")
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("同步应当 200，实际 %d", resp.StatusCode)
	}
	var out struct {
		Count   int            `json:"count"`
		Revoked []revokedEntry `json:"revoked"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if out.Count != 2 {
		t.Fatalf("同步后应当有 2 条，实际 %d", out.Count)
	}
	if ok, state, _ := lm.Check(); ok || state != LicenseRevoked {
		t.Fatalf("正在使用的 A 被撤销后应当立刻不可用，实际 ok=%v state=%s", ok, state)
	}
	// 幂等：再推一次不重复
	resp = post(string(body), "SYNC-TOKEN")
	resp.Body.Close()
	if n := lm.RevokedCount(); n != 2 {
		t.Fatalf("重复同步不该重复计数，实际 %d", n)
	}
	// 移除一条（上位机里"恢复"）
	removeBody, _ := json.Marshal(map[string]any{"remove": []string{licenseCodeHash(codeB)}})
	resp = post(string(removeBody), "SYNC-TOKEN")
	resp.Body.Close()
	if n := lm.RevokedCount(); n != 1 {
		t.Fatalf("移除后应当剩 1 条，实际 %d", n)
	}
	if err := lm.Install(codeB); err != nil {
		t.Fatalf("恢复后的码应当能装：%v", err)
	}
}

// 在线被控端必须被"叫醒"：只断连接的话，它会退避重连、拿着本地缓存的旧码继续跑到期。
func TestRevokedIsPushedToConnectedAgent(t *testing.T) {
	priv := withTestKey(t)
	lm := mustNewLM(t, "DEP-PUSH", "")
	code := mustSign(t, priv, LicensePayload{
		Lic: "PUSH-0001", Nbf: time.Now().Add(-time.Hour).Unix(), Exp: time.Now().Add(72 * time.Hour).Unix(),
	})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}

	logger := discardLogger()
	hub := NewHub(logger)
	hub.Token = "PUSH-TOKEN"
	hub.License = lm
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	// 与 main.go 完全一致的回调接线
	lm.SetOnLocked(func(reason string) {
		hub.PushLicenseAll()
		hub.CloseAll("license: " + reason)
	})
	srv := httptest.NewServer(withLicense(newMux(hub, store), lm, hub))
	t.Cleanup(srv.Close)

	conn, _, err := websocket.DefaultDialer.Dial(wsURL(srv, "/ws?role=agent&id=push-agent&token=PUSH-TOKEN"), nil)
	if err != nil {
		t.Fatalf("被控端应当能连上（授权正常）：%v", err)
	}
	defer conn.Close()

	// 连上后会收到一条授权状态（带激活码，客户端据此二次校验）
	_, first := readFrame(t, conn, "连上后的授权状态")
	var okMsg LicenseStatusMessage
	if err := json.Unmarshal(first, &okMsg); err != nil || okMsg.Type != "license" {
		t.Fatalf("第一条应当是 license 消息：%s", string(first))
	}
	if okMsg.State != string(LicenseOK) || okMsg.Code == "" {
		t.Fatalf("授权正常时应当带激活码原文：%+v", okMsg)
	}

	// 撤销 → 服务器应当先把状态推过来（不带码），然后断开
	if _, err := lm.Revoke(code, "停止服务"); err != nil {
		t.Fatal(err)
	}
	_ = conn.SetReadDeadline(time.Now().Add(testTimeout))
	_, pushed := readFrame(t, conn, "撤销后的授权状态")
	var revMsg LicenseStatusMessage
	if err := json.Unmarshal(pushed, &revMsg); err != nil {
		t.Fatalf("推送的内容不是 JSON：%s", string(pushed))
	}
	if revMsg.State != string(LicenseRevoked) {
		t.Fatalf("应当把 revoked 推给在线被控端，实际 %+v", revMsg)
	}
	if revMsg.Code != "" {
		t.Fatal("撤销推送不该再带上已经作废的激活码原文")
	}
	if !strings.Contains(revMsg.Reason, "撤销") {
		t.Fatalf("推送里应当写明撤销原因，实际：%s", revMsg.Reason)
	}
}
