package main

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// 授权（时间限制 + 激活码）
//
// 设计要点（对抗"用 AI 反编译二进制后打补丁/伪造激活码"）：
//
//  1. **非对称签名**：激活码由厂商上位机用私钥签发，中继里只有**公钥**。
//     私钥从不出现在任何交付物里 → 即使把中继二进制完全反编译，也**无法伪造**激活码，
//     只能去改校验代码本身。这是最重要的一条：没有可提取的对称密钥。
//  2. **时间窗写进签名内**（nbf/exp）：到期时间不可篡改（改了就签名失效）。
//     所以"删掉授权文件重来""把过期时间改大"都无效。
//  3. **部署绑定**（srv = 部署ID / 可选 IP）：一个客户的激活码拿到别的服务器上无效。
//  4. **时钟回拨检测**：持久化"见过的最大时间"，时间倒退超过容忍值即锁定，
//     防"把系统时间调回去继续白嫖"。
//  5. **客户端二次校验**：中继把授权状态与原始激活码一起发给被控端/主控端，
//     由客户端用同一把公钥再验一次 → 只改中继二进制也不够，还得改客户端。
//  6. **失败即锁**：授权文件缺失/损坏/读不出来一律按"未授权"处理，绝不默认放行。
//  7. **时间只能前移**：安装新码时校验它的签发/到期时间不早于当前生效授权 ——
//     换下来的旧码（期限往往更长）装不回去，也不能无声把授权缩短；
//     真要回退/缩短，先显式撤销当前码。管理入口（/admin）只认独立的管理令牌
//     （RC_ADMIN_TOKEN）：中继令牌随客户端分发包发给了客户，不能当管理凭据。
// ---------------------------------------------------------------------------

// licensePublicKeyB64 是厂商公钥（ECDSA P-256 未压缩点，即 0x04||X||Y）。
// 由上位机（LicenseKeygen）生成密钥对后把公钥填到这里；私钥只留在厂商机器上。
var licensePublicKeyB64 = "BONwhpMRJYtkD91ULHKeW9hltBLfWSJtMEvU5/cbwRAKhgLUx+uoh90uVAtH/BMfNkGHUZ18OOA5BYjaf9jWXY8="

// 时钟回拨容忍值：NTP 微调可能让时间小幅倒退，超过这个量就认为是人为回拨。
const clockBackTolerance = 6 * time.Hour

const licensePrefix = "RC1"

// LicensePayload 是激活码里携带的授权信息（签名的内容部分）。
type LicensePayload struct {
	V          int      `json:"v"`
	Lic        string   `json:"lic"`              // 授权编号（订单/客户编号）
	Sub        string   `json:"sub,omitempty"`    // 客户名/备注
	Srv        string   `json:"srv,omitempty"`    // 绑定的部署ID（空=不绑定）
	IP         string   `json:"ip,omitempty"`     // 绑定的公网IP（空=不绑定）
	MaxViewers int      `json:"maxview,omitempty"`
	Feat       []string `json:"feat,omitempty"`
	Nbf        int64    `json:"nbf"` // 生效时间（Unix 秒）
	Exp        int64    `json:"exp"` // 到期时间（Unix 秒）
	Iat        int64    `json:"iat"` // 签发时间
}

func (p *LicensePayload) ExpiresAt() time.Time { return time.Unix(p.Exp, 0).UTC() }
func (p *LicensePayload) NotBefore() time.Time { return time.Unix(p.Nbf, 0).UTC() }

// LicenseState 是当前授权状态。
type LicenseState string

const (
	LicenseOK           LicenseState = "ok"            // 正常
	LicenseMissing      LicenseState = "missing"       // 没有安装激活码
	LicenseInvalid      LicenseState = "invalid"       // 签名/格式/绑定不匹配
	LicenseExpired      LicenseState = "expired"       // 已过期
	LicenseNotYet       LicenseState = "not_yet"       // 尚未生效
	LicenseClockAnomaly LicenseState = "clock_anomaly" // 系统时间被回拨
	LicenseRevoked      LicenseState = "revoked"       // 被厂商撤销（上位机里删除了这条码）
)

// licenseRecord 是落盘的授权文件内容。
type licenseRecord struct {
	Code         string `json:"code"`
	DeploymentID string `json:"deploymentId"`
	InstalledAt  int64  `json:"installedAt"`
	LastSeenUnix int64  `json:"lastSeenUnix"`
	IP           string `json:"ip,omitempty"`
}

// LicenseManager 持有授权状态并负责定期复验。
type LicenseManager struct {
	mu           sync.RWMutex
	path         string
	revPath      string // 撤销名单（revoked.json）
	deploymentID string
	logger       *log.Logger
	rec          licenseRecord
	state        LicenseState
	reason       string
	payload      *LicensePayload
	revoked      map[string]revokedEntry // 指纹 → 撤销记录（受 mu 保护）
	// onLocked 在授权从"可用"变为"不可用"时回调（用来踢掉已建立的连接）
	onLocked func(reason string)
	// notified 标记"当前这一次失效"是否已经触发过踢连回调。
	// 多个并发 Refresh（复验循环 + 请求到期边界 + 安装/撤销）可能同时发现
	// "由可用变不可用"，靠它保证只踢连一次；重新安装有效码变回 OK 时清除，
	// 这样下一次失效仍能再次触发。
	notified bool
}

func NewLicenseManager(dataDir, deploymentID, forcedIP string, logger *log.Logger) (*LicenseManager, error) {
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return nil, err
	}
	m := &LicenseManager{
		path:         filepath.Join(dataDir, "license.json"),
		revPath:      filepath.Join(dataDir, "revoked.json"),
		deploymentID: deploymentID,
		logger:       logger,
		state:        LicenseMissing,
		reason:       "尚未安装激活码",
	}
	m.loadRevocationsLocked()
	if data, err := os.ReadFile(m.path); err == nil {
		if err := json.Unmarshal(data, &m.rec); err != nil {
			// 损坏 → 按未授权处理（失败即锁），绝不默认放行
			m.state = LicenseInvalid
			m.reason = "授权文件损坏：" + err.Error()
			return m, nil
		}
	}
	if forcedIP != "" {
		m.rec.IP = forcedIP
	}
	m.Refresh()
	return m, nil
}

// DeploymentID 返回本机部署ID（绑定用）。
func (m *LicenseManager) DeploymentID() string { return m.deploymentID }

// currentOKPayload 返回当前"生效中"授权的负载；状态不是 ok（过期/撤销/损坏/未安装）
// 时返回 nil —— 那些状态下装码是自救，不做"时间不倒退"约束。
func (m *LicenseManager) currentOKPayload() *LicensePayload {
	m.mu.RLock()
	defer m.mu.RUnlock()
	if m.state != LicenseOK || m.payload == nil {
		return nil
	}
	return m.payload
}

// checkTimeForward 保证新码的时间不落后于当前生效授权（见 Install 内注释）。
func checkTimeForward(cur, next *LicensePayload) error {
	const day = "2006-01-02"
	if cur.Exp > 0 && next.Exp > 0 && next.Exp < cur.Exp {
		return fmt.Errorf("该激活码的到期时间（%s）早于当前授权（%s）：不允许把授权无声缩短；"+
			"如确需缩短，请先撤销当前激活码再安装",
			next.ExpiresAt().Format(day), cur.ExpiresAt().Format(day))
	}
	if cur.Iat > 0 && next.Iat > 0 && next.Iat < cur.Iat {
		return fmt.Errorf("该激活码比当前已装授权更旧（签发于 %s UTC，当前授权签发于 %s UTC）："+
			"不允许装回旧码把授权改回旧期限；如确需回退，请先撤销当前激活码再安装",
			time.Unix(next.Iat, 0).UTC().Format(day), time.Unix(cur.Iat, 0).UTC().Format(day))
	}
	return nil
}

// Install 校验并安装一个新的激活码。
func (m *LicenseManager) Install(code string) error {
	code = strings.TrimSpace(code)
	if code == "" {
		return errors.New("激活码为空")
	}
	// 撤销优先于一切校验：被删掉的码绝不能靠"重新粘贴一遍"复活
	if entry, bad := m.IsRevoked(code); bad {
		return errors.New(revokeReason(entry))
	}
	payload, err := VerifyLicense(code, m.deploymentID, m.rec.IP, time.Now())
	if err != nil {
		return err
	}
	// 时间不倒退：授权只能往前走。只对"当前授权仍生效"的情况生效 ——
	// 过期/撤销/未授权时装码是自救，不受这条约束（那也是显式缩短授权的正路：
	// 先撤销当前码，再装想要的码）。
	//   1) 不得装回**更旧**的码（iat 更早）：否则换下来的旧码（往往期限更长）
	//      能随时装回去，把授权时间"改"回旧期限；
	//   2) 不得无声**缩短**（exp 更早）：防止误装把客户的授权缩水；
	//   3) 与当前同一条码（iat/exp 均相同）→ 允许重复安装（重装/排障是常态）。
	if cur := m.currentOKPayload(); cur != nil {
		if err := checkTimeForward(cur, payload); err != nil {
			return err
		}
	}
	m.mu.Lock()
	m.rec.Code = code
	m.rec.DeploymentID = m.deploymentID
	m.rec.InstalledAt = time.Now().Unix()
	if m.rec.LastSeenUnix == 0 {
		m.rec.LastSeenUnix = time.Now().Unix()
	}
	m.payload = payload
	m.mu.Unlock()
	if err := m.save(); err != nil {
		return err
	}
	// 关键：安装后必须立刻复验状态，否则 Check()/Status() 还会停留在"未授权"
	m.Refresh()
	if state, reason, _, _ := m.Status(); state != LicenseOK {
		return fmt.Errorf("激活码已保存但状态异常：%s（%s）", state, reason)
	}
	return nil
}

// Clear 卸载当前激活码（排障/测试用：卸载后中继立即回到"未授权"，拒绝一切通信）。
// 部署ID与"见过的最大时间"保留，避免清空后时钟回拨检测被绕过。
func (m *LicenseManager) Clear() error {
	m.mu.Lock()
	m.rec.Code = ""
	m.payload = nil
	m.mu.Unlock()
	if err := m.save(); err != nil {
		return err
	}
	m.Refresh()
	return nil
}

func (m *LicenseManager) save() error {
	m.mu.RLock()
	rec := m.rec
	m.mu.RUnlock()
	data, err := json.MarshalIndent(rec, "", "  ")
	if err != nil {
		return err
	}
	tmp := m.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, m.path)
}

// Refresh 重新校验授权（并推进"见过的最大时间"，检测时钟回拨）。
func (m *LicenseManager) Refresh() LicenseState {
	now := time.Now()
	m.mu.Lock()
	prevState := m.state
	prevReason := m.reason
	prevLastSeen := m.rec.LastSeenUnix

	// 时钟回拨检测：把"见过的最大时间"落盘，时间倒退超过容忍值即锁定
	if m.rec.LastSeenUnix > 0 {
		last := time.Unix(m.rec.LastSeenUnix, 0)
		if now.Add(clockBackTolerance).Before(last) {
			m.state = LicenseClockAnomaly
			m.reason = fmt.Sprintf("检测到系统时间回拨（上次见到 %s，当前 %s）。请校时后重新安装激活码",
				last.UTC().Format(time.RFC3339), now.UTC().Format(time.RFC3339))
			m.mu.Unlock()
			m.notifyIfLocked(prevState, prevReason)
			return m.state
		}
	}
	if now.Unix() > m.rec.LastSeenUnix {
		m.rec.LastSeenUnix = now.Unix()
	}

	switch {
	case m.rec.Code == "":
		m.state = LicenseMissing
		m.reason = "尚未安装激活码（打开 /admin 粘贴激活码，或执行 rcserver -license <激活码>）"
	default:
		payload, err := VerifyLicense(m.rec.Code, m.deploymentID, m.rec.IP, now)
		if err != nil {
			switch {
			case errors.Is(err, errExpired):
				m.state = LicenseExpired
			case errors.Is(err, errNotYet):
				m.state = LicenseNotYet
			default:
				m.state = LicenseInvalid
			}
			m.reason = err.Error()
			m.payload = nil
		} else {
			if entry, bad := m.isRevokedLocked(m.rec.Code); bad {
				// 已装着的码被撤销：立刻变 revoked（notifyIfLocked 会踢掉在线连接）
				m.state = LicenseRevoked
				m.reason = revokeReason(entry)
				m.payload = nil
			} else {
				m.state = LicenseOK
				m.reason = ""
				m.payload = payload
				// 重新生效：清除"已踢连"标记，下次失效仍能再次踢掉在线连接。
				m.notified = false
			}
		}
	}
	state := m.state
	// 只有"见过的最大时间"真的推进了才落盘：复验循环、到期边界的并发请求
	// 都可能在极短时间内各自跑一次 Refresh，若每次都写盘会造成写放大
	//（tmp 写入 + rename + 目录 fsync）。第一个推进 LastSeen 的 goroutine
	// 写盘后，后续并发的 Refresh 发现没有变化即跳过，避免重复落盘。
	dirty := m.rec.LastSeenUnix != prevLastSeen
	m.mu.Unlock()

	if dirty {
		_ = m.save()
	}
	m.notifyIfLocked(prevState, prevReason)
	return state
}

// notifyIfLocked 在"由可用变为不可用"时触发一次踢连回调。
//
// Refresh 有多个并发入口（复验循环、Check 的到期边界、Install/Revoke/…），
// 多个 goroutine 可能同时带着 prevState==LicenseOK 进来。用 notified 标志
// 保证同一次失效只回调一次，避免对在线连接重复遍历、重复写 close 帧。
func (m *LicenseManager) notifyIfLocked(prevState LicenseState, prevReason string) {
	m.mu.Lock()
	nowState, nowReason := m.state, m.reason
	cb := m.onLocked
	if prevState == LicenseOK && nowState != LicenseOK && !m.notified {
		m.notified = true
		m.mu.Unlock()
		if cb != nil {
			cb(nowReason)
		}
		return
	}
	m.mu.Unlock()
}

// SetOnLocked 注册"由可用变为不可用"的回调。
func (m *LicenseManager) SetOnLocked(cb func(reason string)) {
	m.mu.Lock()
	m.onLocked = cb
	m.mu.Unlock()
}

// Check 供请求路径快速判断；返回 (是否允许, 状态, 原因)。
//
// 除了读缓存状态，这里还会**当场**比对时间窗与时钟回拨：
// 授权到期必须"立刻"生效，不能等到下一次 30 秒复验才拦。
func (m *LicenseManager) Check() (bool, LicenseState, string) {
	now := time.Now()
	m.mu.RLock()
	state, reason, payload, lastSeen := m.state, m.reason, m.payload, m.rec.LastSeenUnix
	m.mu.RUnlock()

	stale := state == LicenseOK && payload != nil &&
		((payload.Exp > 0 && now.Unix() > payload.Exp) ||
			(lastSeen > 0 && now.Add(clockBackTolerance).Before(time.Unix(lastSeen, 0))))
	if stale {
		m.Refresh()
		m.mu.RLock()
		state, reason = m.state, m.reason
		m.mu.RUnlock()
	}
	return state == LicenseOK, state, reason
}

// Status 返回状态快照（含激活码原文，供客户端二次校验）。
func (m *LicenseManager) Status() (LicenseState, string, *LicensePayload, string) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.state, m.reason, m.payload, m.rec.Code
}

// StartRecheckLoop 每 interval 复验一次（让"到期"能即时生效，而不必重启）。
func (m *LicenseManager) StartRecheckLoop(done <-chan struct{}, interval time.Duration) {
	go func() {
		t := time.NewTicker(interval)
		defer t.Stop()
		for {
			select {
			case <-done:
				return
			case <-t.C:
				state := m.Refresh()
				if state != LicenseOK {
					m.logger.Printf("license: state=%s", state)
				}
			}
		}
	}()
}

// ---------------------------------------------------------------------------
// 激活码编解码与验签
// ---------------------------------------------------------------------------

var (
	errExpired = errors.New("激活码已过期")
	errNotYet  = errors.New("激活码尚未生效")
)

func b64urlDecode(s string) ([]byte, error) {
	return base64.RawURLEncoding.DecodeString(strings.TrimRight(s, "="))
}

func b64urlEncode(b []byte) string {
	return base64.RawURLEncoding.EncodeToString(b)
}

// LicensePublicKey 解析内置公钥。
func LicensePublicKey() (*ecdsa.PublicKey, error) {
	return ParsePublicKey(licensePublicKeyB64)
}

// ParsePublicKey 解析未压缩点格式（0x04||X||Y）的 base64 公钥。
func ParsePublicKey(b64 string) (*ecdsa.PublicKey, error) {
	raw, err := base64.StdEncoding.DecodeString(strings.TrimSpace(b64))
	if err != nil {
		return nil, fmt.Errorf("公钥 base64 解析失败：%w", err)
	}
	x, y := elliptic.Unmarshal(elliptic.P256(), raw)
	if x == nil {
		return nil, errors.New("公钥不是合法的 P-256 未压缩点")
	}
	return &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}, nil
}

// SignLicense 用私钥签发激活码（只在上位机侧使用；中继不包含此逻辑的调用）。
func SignLicense(priv *ecdsa.PrivateKey, payload LicensePayload) (string, error) {
	payload.V = 1
	if payload.Iat == 0 {
		payload.Iat = time.Now().Unix()
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return "", err
	}
	signingInput := licensePrefix + "." + b64urlEncode(body)
	digest := sha256.Sum256([]byte(signingInput))
	r, s, err := ecdsa.Sign(rand.Reader, priv, digest[:])
	if err != nil {
		return "", err
	}
	sig := make([]byte, 64) // r||s 各 32 字节
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return signingInput + "." + b64urlEncode(sig), nil
}

// VerifyLicense 校验激活码：格式 → 签名 → 绑定 → 时间窗。
// 任何一步失败都返回错误（调用方不得把错误当成"通过"）。
func VerifyLicense(code, deploymentID, ip string, now time.Time) (*LicensePayload, error) {
	parts := strings.Split(strings.TrimSpace(code), ".")
	if len(parts) != 3 || parts[0] != licensePrefix {
		return nil, errors.New("激活码格式不正确（应为 RC1.<payload>.<signature>）")
	}
	body, err := b64urlDecode(parts[1])
	if err != nil {
		return nil, fmt.Errorf("激活码负载解码失败：%w", err)
	}
	sig, err := b64urlDecode(parts[2])
	if err != nil || len(sig) != 64 {
		return nil, errors.New("激活码签名长度不正确")
	}
	pub, err := LicensePublicKey()
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256([]byte(licensePrefix + "." + parts[1]))
	r := new(big.Int).SetBytes(sig[:32])
	s := new(big.Int).SetBytes(sig[32:])
	if !ecdsa.Verify(pub, digest[:], r, s) {
		return nil, errors.New("激活码签名校验失败（无效或已被篡改）")
	}

	var payload LicensePayload
	if err := json.Unmarshal(body, &payload); err != nil {
		return nil, fmt.Errorf("激活码内容解析失败：%w", err)
	}
	if payload.V != 1 {
		return nil, fmt.Errorf("不支持的激活码版本 %d", payload.V)
	}
	if payload.Srv != "" && deploymentID != "" &&
		subtle.ConstantTimeCompare([]byte(payload.Srv), []byte(deploymentID)) != 1 {
		return nil, fmt.Errorf("激活码绑定的部署 %s 与本机部署 %s 不一致", payload.Srv, deploymentID)
	}
	if payload.IP != "" && ip != "" && payload.IP != ip {
		return nil, fmt.Errorf("激活码绑定的 IP %s 与本机 %s 不一致", payload.IP, ip)
	}
	if payload.Nbf > 0 && now.Unix() < payload.Nbf {
		return nil, fmt.Errorf("%w（生效时间 %s）", errNotYet, payload.NotBefore().Format(time.RFC3339))
	}
	if payload.Exp > 0 && now.Unix() > payload.Exp {
		return nil, fmt.Errorf("%w（到期时间 %s）", errExpired, payload.ExpiresAt().Format(time.RFC3339))
	}
	return &payload, nil
}

// LicenseStatusMessage 是发给客户端的授权状态（含原始激活码，客户端用同一公钥再验一次）。
type LicenseStatusMessage struct {
	Type    string `json:"type"`
	State   string `json:"state"`
	Reason  string `json:"reason,omitempty"`
	Code    string `json:"code,omitempty"`
	Expires string `json:"expires,omitempty"`
	Lic     string `json:"lic,omitempty"`
}

func (m *LicenseManager) StatusMessage() LicenseStatusMessage {
	state, reason, payload, code := m.Status()
	msg := LicenseStatusMessage{Type: "license", State: string(state), Reason: reason, Code: code}
	if payload != nil {
		msg.Expires = payload.ExpiresAt().Format(time.RFC3339)
		msg.Lic = payload.Lic
	}
	return msg
}

// StatusMessageNoCode 与 StatusMessage 相同，但**不带激活码原文**。
// 用于"授权已不可用"时的主动广播：客户端只看到 state（revoked/expired/…）就会本地停机，
// 不需要、也不该再拿到那串已经作废的码。
func (m *LicenseManager) StatusMessageNoCode() LicenseStatusMessage {
	state, reason, payload, _ := m.Status()
	msg := LicenseStatusMessage{Type: "license", State: string(state), Reason: reason}
	if payload != nil {
		msg.Expires = payload.ExpiresAt().Format(time.RFC3339)
		msg.Lic = payload.Lic
	}
	return msg
}
