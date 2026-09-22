package main

// revoke.go —— 激活码撤销名单（"上位机里删掉这条码 → 它就不能再用"）。
//
// 为什么必须有这份名单：激活码是**自包含**的（签名 + 时间窗写死在码里），
// 中继和被控端都只靠公钥验签，没有任何"已签发台账"。所以一张仍在有效期内的码，
// 光在上位机把记录删掉是杀不掉的 —— 必须让**校验方**知道它被撤销了，
// 于是名单落在中继的 -data 目录里，在"安装"和"复验"两处都拦。
//
// 三个刻意的设计：
//   - **按激活码原文的 sha256 记，不按授权编号**：编号会被续期复用（HK-REL-0001
//     续期还是 0001），按编号撤销会误伤同编号的新码。按码文本撤销只杀被删掉的那一条；
//     而客户手里"能装得上"的码，就必然与撤销时那串完全一致（外层空白由 TrimSpace 归一）。
//   - **名单由令牌保护，随 -data 落盘**（revoked.json，原子替换），不随二进制升级丢失。
//   - **命中即不可用**：不是"仅拒绝新安装"，当前已装的码被撤销也会立刻把状态推成 revoked，
//     走既有的"失效即断连"通路踢掉在线客户端。

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"
)

// revokedEntry 是撤销名单里的一条。
type revokedEntry struct {
	Hash string `json:"hash"`           // sha256(TrimSpace(激活码)) 的十六进制
	Lic  string `json:"lic,omitempty"`  // 授权编号（展示用）
	Sub  string `json:"sub,omitempty"`  // 客户/备注
	Exp  int64  `json:"exp,omitempty"`  // 该码原本的到期时间（Unix 秒，展示用）
	At   int64  `json:"at"`             // 撤销时间（Unix 秒）
	Note string `json:"note,omitempty"` // 说明（谁、为什么删）
}

// revokedFile 是 revoked.json 的落盘结构。
type revokedFile struct {
	Revoked []revokedEntry `json:"revoked"`
}

// licenseCodeHash 计算激活码的撤销指纹。必须先 TrimSpace：客户粘贴时带首尾空白/换行
// 是常态，而 VerifyLicense 也会先 TrimSpace，所以归一后取哈希才不会漏判。
func licenseCodeHash(code string) string {
	sum := sha256.Sum256([]byte(strings.TrimSpace(code)))
	return hex.EncodeToString(sum[:])
}

// loadRevocationsLocked 读撤销名单（调用方需持有 m.mu 或处于构造阶段）。
//
// 文件损坏时**只记日志不阻止启动**：中继停摆的代价（所有客户都没法用）远大于
// "名单暂时为空"（被撤销的码会恢复可用）。而写盘走 tmp+rename，损坏概率极低。
func (m *LicenseManager) loadRevocationsLocked() {
	m.revoked = make(map[string]revokedEntry)
	data, err := os.ReadFile(m.revPath)
	if err != nil {
		if !errors.Is(err, os.ErrNotExist) {
			m.logger.Printf("revoke: 读取撤销名单失败（按空名单继续）：%v", err)
		}
		return
	}
	var f revokedFile
	if err := json.Unmarshal(data, &f); err != nil {
		m.logger.Printf("revoke: 撤销名单损坏（按空名单继续，被撤销的码会恢复可用）：%v", err)
		return
	}
	for _, e := range f.Revoked {
		if e.Hash != "" {
			m.revoked[e.Hash] = e
		}
	}
	if len(m.revoked) > 0 {
		m.logger.Printf("revoke: 已载入 %d 条撤销记录", len(m.revoked))
	}
}

// saveRevocationsLocked 原子落盘（调用方需持有 m.mu）。
func (m *LicenseManager) saveRevocationsLocked() error {
	list := make([]revokedEntry, 0, len(m.revoked))
	for _, e := range m.revoked {
		list = append(list, e)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].At < list[j].At })
	data, err := json.MarshalIndent(revokedFile{Revoked: list}, "", "  ")
	if err != nil {
		return err
	}
	tmp := m.revPath + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, m.revPath)
}

// isRevokedLocked 查名单（调用方需持有 m.mu）。
func (m *LicenseManager) isRevokedLocked(code string) (revokedEntry, bool) {
	if strings.TrimSpace(code) == "" || len(m.revoked) == 0 {
		return revokedEntry{}, false
	}
	e, ok := m.revoked[licenseCodeHash(code)]
	return e, ok
}

// IsRevoked 判断某个激活码是否已被撤销（导出给 /admin 与测试用）。
func (m *LicenseManager) IsRevoked(code string) (revokedEntry, bool) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.isRevokedLocked(code)
}

// RevokedEntries 返回撤销名单（按撤销时间升序）。
func (m *LicenseManager) RevokedEntries() []revokedEntry {
	m.mu.RLock()
	defer m.mu.RUnlock()
	list := make([]revokedEntry, 0, len(m.revoked))
	for _, e := range m.revoked {
		list = append(list, e)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].At < list[j].At })
	return list
}

// RevokedCount 返回撤销条目数（给 /api/license 展示）。
func (m *LicenseManager) RevokedCount() int {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return len(m.revoked)
}

// Revoke 把一条激活码加入撤销名单（幂等），随后立刻复验 ——
// 如果被撤销的正是当前已装的那条，状态会马上变 revoked 并踢掉在线连接。
func (m *LicenseManager) Revoke(code, note string) (revokedEntry, error) {
	code = strings.TrimSpace(code)
	if code == "" {
		return revokedEntry{}, errors.New("激活码为空")
	}
	hash := licenseCodeHash(code)

	// 能解出 payload 就顺便记下编号/客户/到期，方便在名单里认人（解不出也照样撤销）
	var lic, sub string
	var exp int64
	if payload, err := VerifyLicense(code, "", "", time.Now()); err == nil && payload != nil {
		lic, sub, exp = payload.Lic, payload.Sub, payload.Exp
	}

	m.mu.Lock()
	if existing, ok := m.revoked[hash]; ok {
		m.mu.Unlock()
		return existing, nil
	}
	entry := revokedEntry{Hash: hash, Lic: lic, Sub: sub, Exp: exp, At: time.Now().Unix(), Note: note}
	m.revoked[hash] = entry
	err := m.saveRevocationsLocked()
	m.mu.Unlock()
	if err != nil {
		return entry, err
	}
	m.logger.Printf("revoke: 已撤销激活码（编号 %s，指纹 %s）：%s", orDash(lic), hash[:12], note)
	m.Refresh()
	return entry, nil
}

// Unrevoke 把一条激活码从撤销名单移除（"恢复使用"）。按指纹或按激活码原文都行。
func (m *LicenseManager) Unrevoke(hashOrCode string) (bool, error) {
	key := strings.TrimSpace(hashOrCode)
	if key == "" {
		return false, errors.New("未指定要恢复的条目")
	}
	if len(key) != 64 {
		key = licenseCodeHash(key)
	}
	m.mu.Lock()
	entry, ok := m.revoked[key]
	if !ok {
		m.mu.Unlock()
		return false, nil
	}
	delete(m.revoked, key)
	err := m.saveRevocationsLocked()
	m.mu.Unlock()
	if err != nil {
		return false, err
	}
	m.logger.Printf("revoke: 已恢复激活码（编号 %s，指纹 %s）", orDash(entry.Lic), key[:12])
	m.Refresh()
	return true, nil
}

// MergeRevocations 合并一批撤销记录（幂等，供上位机同步用），并可同时移除若干条。
// 返回合并后的完整名单。
func (m *LicenseManager) MergeRevocations(add []revokedEntry, remove []string) ([]revokedEntry, error) {
	m.mu.Lock()
	for _, e := range add {
		if len(e.Hash) != 64 { // 只认 64 位十六进制指纹（上位机自己算好）
			continue
		}
		if _, ok := m.revoked[e.Hash]; ok {
			continue
		}
		if e.At == 0 {
			e.At = time.Now().Unix()
		}
		m.revoked[e.Hash] = e
	}
	for _, r := range remove {
		r = strings.TrimSpace(r)
		if len(r) != 64 {
			r = licenseCodeHash(r)
		}
		delete(m.revoked, r)
	}
	err := m.saveRevocationsLocked()
	m.mu.Unlock()
	if err != nil {
		return nil, err
	}
	m.Refresh()
	return m.RevokedEntries(), nil
}

// CurrentHash 返回当前已安装激活码的撤销指纹（未安装时为空串）。
// 上位机拿它跟本地台账精确比对，判断"哪一条正在被使用"。
func (m *LicenseManager) CurrentHash() string {
	m.mu.RLock()
	code := m.rec.Code
	m.mu.RUnlock()
	if strings.TrimSpace(code) == "" {
		return ""
	}
	return licenseCodeHash(code)
}

// InstalledAt 返回当前激活码的安装时间（Unix 秒，未安装为 0）。
func (m *LicenseManager) InstalledAt() int64 {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.rec.InstalledAt
}

func orDash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// revokeReason 生成"被撤销"的中文原因（带撤销时间，便于排障时一眼看出）。
func revokeReason(e revokedEntry) string {
	when := time.Unix(e.At, 0).UTC().Format("2006-01-02 15:04")
	msg := fmt.Sprintf("该激活码已被厂商撤销（撤销于 %s UTC）", when)
	if e.Note != "" {
		msg += "：" + e.Note
	}
	return msg + "。如需继续使用请联系厂商重新签发"
}
