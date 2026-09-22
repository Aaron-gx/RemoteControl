package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// 测试用密钥对：临时替换内置公钥，避免测试依赖厂商私钥。
func withTestKey(t *testing.T) *ecdsa.PrivateKey {
	t.Helper()
	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	old := licensePublicKeyB64
	raw := elliptic.Marshal(elliptic.P256(), priv.PublicKey.X, priv.PublicKey.Y)
	licensePublicKeyB64 = base64.StdEncoding.EncodeToString(raw)
	t.Cleanup(func() { licensePublicKeyB64 = old })
	return priv
}

func mustSign(t *testing.T, priv *ecdsa.PrivateKey, p LicensePayload) string {
	t.Helper()
	code, err := SignLicense(priv, p)
	if err != nil {
		t.Fatal(err)
	}
	return code
}

func TestLicenseVerifyAndBind(t *testing.T) {
	priv := withTestKey(t)
	now := time.Now()
	base := LicensePayload{
		Lic: "ORD-2026-0001",
		Sub: "测试客户",
		Srv: "DEP-ABC",
		IP:  "1.2.3.4",
		Nbf: now.Add(-time.Hour).Unix(),
		Exp: now.Add(24 * time.Hour).Unix(),
	}
	code := mustSign(t, priv, base)

	// 正常
	if _, err := VerifyLicense(code, "DEP-ABC", "1.2.3.4", now); err != nil {
		t.Fatalf("合法激活码应通过，却失败：%v", err)
	}
	// 不绑定时任意部署都通过
	unbound := mustSign(t, priv, LicensePayload{Lic: "X", Nbf: base.Nbf, Exp: base.Exp})
	if _, err := VerifyLicense(unbound, "DEP-ANY", "", now); err != nil {
		t.Fatalf("未绑定部署的激活码应通过：%v", err)
	}
	// 绑定不匹配
	if _, err := VerifyLicense(code, "DEP-OTHER", "1.2.3.4", now); err == nil {
		t.Fatal("部署不匹配应被拒绝")
	}
	if _, err := VerifyLicense(code, "DEP-ABC", "9.9.9.9", now); err == nil {
		t.Fatal("IP 不匹配应被拒绝")
	}
	// 过期 / 未生效
	expired := mustSign(t, priv, LicensePayload{Lic: "X", Nbf: now.Add(-2 * time.Hour).Unix(), Exp: now.Add(-time.Hour).Unix()})
	if _, err := VerifyLicense(expired, "", "", now); err == nil {
		t.Fatal("过期激活码应被拒绝")
	}
	future := mustSign(t, priv, LicensePayload{Lic: "X", Nbf: now.Add(time.Hour).Unix(), Exp: now.Add(2 * time.Hour).Unix()})
	if _, err := VerifyLicense(future, "", "", now); err == nil {
		t.Fatal("未生效激活码应被拒绝")
	}
	// 篡改负载（把到期时间改大）→ 签名失效
	parts := strings.Split(code, ".")
	var p LicensePayload
	body, _ := base64.RawURLEncoding.DecodeString(parts[1])
	_ = json.Unmarshal(body, &p)
	p.Exp = now.Add(3650 * 24 * time.Hour).Unix()
	tamperedBody, _ := json.Marshal(p)
	tampered := "RC1." + base64.RawURLEncoding.EncodeToString(tamperedBody) + "." + parts[2]
	if _, err := VerifyLicense(tampered, "DEP-ABC", "1.2.3.4", now); err == nil {
		t.Fatal("篡改到期时间的激活码应被拒绝")
	}
	// 伪造签名
	if _, err := VerifyLicense("RC1."+parts[1]+"."+base64.RawURLEncoding.EncodeToString(make([]byte, 64)), "DEP-ABC", "1.2.3.4", now); err == nil {
		t.Fatal("伪造签名应被拒绝")
	}
	// 换个密钥签发（模拟"自己生成密钥对来签"）→ 也必须失败
	attacker, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	fake := mustSign(t, attacker, base)
	if _, err := VerifyLicense(fake, "DEP-ABC", "1.2.3.4", now); err == nil {
		t.Fatal("非厂商私钥签发的激活码应被拒绝")
	}
}

func TestLicenseGateBlocksThenAllows(t *testing.T) {
	priv := withTestKey(t)
	logger := log.New(io.Discard, "", 0)
	dir := t.TempDir()

	lm, err := NewLicenseManager(dir, "DEP-TEST", "", logger)
	if err != nil {
		t.Fatal(err)
	}
	if lm.Refresh() != LicenseMissing {
		t.Fatalf("初始应为 missing，实际 %s", lm.Refresh())
	}

	hub := NewHub(logger)
	hub.License = lm
	store, err := NewFileStore(dir, logger)
	if err != nil {
		t.Fatal(err)
	}
	srv := httptest.NewServer(withLicense(newMux(hub, store), lm, hub))
	defer srv.Close()

	// 未授权：被控端/主控端的 WS 握手、文件接口、在线列表全部拒绝
	for _, path := range []string{"/api/agents", "/api/upload", "/api/download/whatever"} {
		resp, err := http.Get(srv.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusForbidden {
			t.Fatalf("%s 未授权时应 403，实际 %d", path, resp.StatusCode)
		}
	}
	// healthz 与 license 状态页始终可用
	for _, path := range []string{"/healthz", "/api/license"} {
		resp, err := http.Get(srv.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusOK {
			t.Fatalf("%s 应始终可用，实际 %d", path, resp.StatusCode)
		}
	}

	// 安装合法激活码 → 放行
	now := time.Now()
	code := mustSign(t, priv, LicensePayload{Lic: "ORD-1", Srv: "DEP-TEST",
		Nbf: now.Add(-time.Minute).Unix(), Exp: now.Add(time.Hour).Unix()})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	if state := lm.Refresh(); state != LicenseOK {
		t.Fatalf("安装后应为 ok，实际 %s（%s）", state, lm.reason)
	}
	resp, err := http.Get(srv.URL + "/api/agents")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("授权后 /api/agents 应 200，实际 %d", resp.StatusCode)
	}
}

func TestLicenseExpiryStopsCommunication(t *testing.T) {
	priv := withTestKey(t)
	logger := log.New(io.Discard, "", 0)
	lm, err := NewLicenseManager(t.TempDir(), "DEP-TEST", "", logger)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	// 2 秒后到期
	code := mustSign(t, priv, LicensePayload{Lic: "SHORT", Nbf: now.Add(-time.Minute).Unix(), Exp: now.Add(2 * time.Second).Unix()})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	if ok, state, _ := lm.Check(); !ok {
		t.Fatalf("刚安装应可用，状态 %s", state)
	}
	time.Sleep(4 * time.Second)   // 授权 2 秒后到期；睡足 4 秒避开整秒边界
	ok2, state2, reason2 := lm.Check()
	if ok2 {
		st, rn, pl, _ := lm.Status()
		t.Fatalf("到期后不应再放行（state=%s reason=%s payload=%+v）", st, rn, pl)
	} else if state2 != LicenseExpired {
		t.Fatalf("到期后状态应为 expired，实际 %s（%s）", state2, reason2)
	}
	// 到期后重装同一条（已过期）也应被拒绝
	if err := lm.Install(code); err == nil {
		t.Fatal("过期激活码不应能安装")
	}
}

func TestLicenseClockRollbackIsDetected(t *testing.T) {
	priv := withTestKey(t)
	logger := log.New(io.Discard, "", 0)
	lm, err := NewLicenseManager(t.TempDir(), "DEP-TEST", "", logger)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	code := mustSign(t, priv, LicensePayload{Lic: "X", Nbf: now.Add(-time.Hour).Unix(), Exp: now.Add(720 * time.Hour).Unix()})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	// 模拟"曾经见过 7 小时后"的时间点（真实场景：用户把系统时间调到未来再调回来）
	lm.mu.Lock()
	lm.rec.LastSeenUnix = now.Add(7 * time.Hour).Unix()
	lm.mu.Unlock()
	if state := lm.Refresh(); state != LicenseClockAnomaly {
		t.Fatalf("时间回拨应被检出，实际状态 %s", state)
	}
	if ok, _, _ := lm.Check(); ok {
		t.Fatal("检出时间回拨后不应放行")
	}
}

func TestLicenseStatusMessageCarriesCode(t *testing.T) {
	priv := withTestKey(t)
	logger := log.New(io.Discard, "", 0)
	lm, err := NewLicenseManager(t.TempDir(), "DEP-TEST", "", logger)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	code := mustSign(t, priv, LicensePayload{Lic: "ORD-9", Sub: "客户甲", Nbf: now.Add(-time.Minute).Unix(), Exp: now.Add(time.Hour).Unix()})
	if err := lm.Install(code); err != nil {
		t.Fatal(err)
	}
	msg := lm.StatusMessage()
	if msg.Type != "license" || msg.State != string(LicenseOK) {
		t.Fatalf("状态消息不对：%+v", msg)
	}
	if msg.Code != code {
		t.Fatal("状态消息应带上原始激活码（供客户端二次校验）")
	}
	if msg.Lic != "ORD-9" || msg.Expires == "" {
		t.Fatalf("缺少授权编号/到期时间：%+v", msg)
	}
}

func TestLicenseFileCorruptMeansLocked(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	dir := t.TempDir()
	// 写一个损坏的授权文件
	if err := os.WriteFile(filepath.Join(dir, "license.json"), []byte("{ not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	lm, err := NewLicenseManager(dir, "DEP-TEST", "", logger)
	if err != nil {
		t.Fatal(err)
	}
	if ok, state, _ := lm.Check(); ok {
		t.Fatal("授权文件损坏时必须按未授权处理（失败即锁）")
	} else if state != LicenseInvalid {
		t.Fatalf("损坏文件应为 invalid，实际 %s", state)
	}
}
