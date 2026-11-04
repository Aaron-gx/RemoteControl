package main

// admin.go —— 授权管理网页（中继服务器上的"设置页面"）。
//
// 为什么要一个页面：激活码原本只能在服务器上敲命令安装
// （`/opt/rcserver/rcctl install "<码>"`），而实际使用中经常需要在浏览器里
// 粘贴激活码、顺手看一眼剩余天数。
//
// 两个刻意的设计：
//   - **放在授权闸门之外**（withLicense 放行 /admin*）：授权过期时正是最需要能进来续期的时候，
//     如果被闸门挡住就没法自救了。
//   - **必须带管理令牌**（?token= / X-RC-Token / Authorization: Bearer）：页面能改授权状态，
//     等同于中继的管理入口，绝不能裸奔。管理令牌（-admin-token / RC_ADMIN_TOKEN）必须是
//     一条**独立于中继令牌**的 secret：中继令牌随主控端/被控端分发包发给了客户，客户手里就有，
//     用它开管理页等于把"装码/撤销/恢复/卸载授权"全交给客户 —— 那正是"已撤销的码被恢复、
//     旧的长有效期码盖掉新码"这类时间倒挂事故的根源。

import (
	"encoding/json"
	"log"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// registerAdmin 挂载 /admin（状态+安装表单）与 /admin/install、/admin/clear、/admin/revoke*。
func registerAdmin(mux *http.ServeMux, hub *Hub, lic *LicenseManager, logger *log.Logger) {
	if lic == nil {
		// 单测里可能只建了 hub/store 而没有授权管理器：给个明确说明，别 500。
		mux.HandleFunc("GET /admin", func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			_, _ = w.Write([]byte("本中继实例未接入授权管理器\n"))
		})
		return
	}

	mux.HandleFunc("GET /admin", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		writeAdminPage(w, hub, lic, strings.TrimSpace(r.URL.Query().Get("msg")), false)
	})

	mux.HandleFunc("POST /admin/install", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		_ = r.ParseForm()
		code := strings.TrimSpace(r.FormValue("code"))
		if code == "" {
			redirectAdmin(w, r, "请先粘贴激活码")
			return
		}
		if err := lic.Install(code); err != nil {
			logger.Printf("admin: 安装激活码失败：%v", err)
			redirectAdmin(w, r, "安装失败："+err.Error())
			return
		}
		state, reason, payload, _ := lic.Status()
		detail := string(state)
		if payload != nil {
			detail = payload.Lic + "，有效期至 " + payload.ExpiresAt().Format("2006-01-02")
		}
		logger.Printf("admin: 激活码安装成功：%s（%s）", detail, reason)
		redirectAdmin(w, r, "已安装："+detail)
	})

	mux.HandleFunc("POST /admin/clear", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		if err := lic.Clear(); err != nil {
			redirectAdmin(w, r, "卸载失败："+err.Error())
			return
		}
		logger.Printf("admin: 已卸载授权（排障/测试用）")
		redirectAdmin(w, r, "已卸载授权，中继恢复为未授权状态")
	})

	// 撤销：把一条激活码拉黑（"上位机里删掉 → 它就不能再用"）。幂等。
	mux.HandleFunc("POST /admin/revoke", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		_ = r.ParseForm()
		code := strings.TrimSpace(r.FormValue("code"))
		note := strings.TrimSpace(r.FormValue("note"))
		if code == "" {
			redirectAdmin(w, r, "请先粘贴要撤销的激活码")
			return
		}
		entry, err := lic.Revoke(code, note)
		if err != nil {
			redirectAdmin(w, r, "撤销失败："+err.Error())
			return
		}
		label := entry.Lic
		if label == "" {
			label = entry.Hash[:12]
		}
		redirectAdmin(w, r, "已撤销："+label+"（该码立即失效，且无法再安装）")
	})

	// 恢复：把一条激活码从名单里拿掉。
	mux.HandleFunc("POST /admin/unrevoke", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		_ = r.ParseForm()
		key := strings.TrimSpace(r.FormValue("hash"))
		if key == "" {
			key = strings.TrimSpace(r.FormValue("code"))
		}
		ok, err := lic.Unrevoke(key)
		if err != nil {
			redirectAdmin(w, r, "恢复失败："+err.Error())
			return
		}
		if !ok {
			redirectAdmin(w, r, "名单里没有这条记录")
			return
		}
		redirectAdmin(w, r, "已恢复（重新安装该激活码即可生效）")
	})

	// 上位机同步：本地台账里"已删除"的条目推上来（并集，幂等），可同时移除若干条。
	// 用 JSON，不回 303 —— 上位机只看 HTTP 码与返回体。
	mux.HandleFunc("POST /admin/revoked/sync", func(w http.ResponseWriter, r *http.Request) {
		if !adminAuthorized(hub, r) {
			writeAdminAuthError(w)
			return
		}
		var req struct {
			Add    []revokedEntry `json:"add"`
			Remove []string       `json:"remove"`
		}
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20)).Decode(&req); err != nil {
			writeJSONError(w, http.StatusBadRequest, "bad_request", "请求体解析失败："+err.Error())
			return
		}
		list, err := lic.MergeRevocations(req.Add, req.Remove)
		if err != nil {
			writeJSONError(w, http.StatusInternalServerError, "save_failed", err.Error())
			return
		}
		if len(req.Add) > 0 || len(req.Remove) > 0 {
			logger.Printf("admin: 撤销名单已同步（新增 %d，移除 %d，现共 %d 条）",
				len(req.Add), len(req.Remove), len(list))
		}
		state, reason, _, _ := lic.Status()
		writeJSON(w, http.StatusOK, map[string]any{
			"state":   string(state),
			"reason":  reason,
			"count":   len(list),
			"revoked": list,
		})
	})
}

// redirectAdmin 用 303 回到状态页并带一条提示（PRG，避免刷新重复提交）。
func redirectAdmin(w http.ResponseWriter, r *http.Request, msg string) {
	q := url.Values{}
	if tok := r.URL.Query().Get("token"); tok != "" {
		q.Set("token", tok)
	}
	q.Set("msg", msg)
	http.Redirect(w, r, "/admin?"+q.Encode(), http.StatusSeeOther)
}

// adminAuthorized 管理接口（/admin*）的鉴权。
//
// 规则（按顺序）：
//  1. 配置了管理令牌（hub.AdminToken）→ 只认管理令牌。中继令牌哪怕从本机来也不认：
//     它已经随分发包发给了客户，不是"客户没有的秘密"。
//  2. 没配置管理令牌（兼容旧部署）→ 只放行"本机回环 + 中继令牌"，供 SSH 上去排障/续期；
//     来自公网的一律拒绝，并在启动日志里提醒配置 RC_ADMIN_TOKEN。
func adminAuthorized(hub *Hub, r *http.Request) bool {
	if hub.AdminToken != "" {
		return Authorized(hub.AdminToken, r)
	}
	return isLoopbackRequest(r) && Authorized(hub.Token, r)
}

// isLoopbackRequest 判断请求是否来自本机回环地址（127.0.0.0/8、::1）。
func isLoopbackRequest(r *http.Request) bool {
	host := r.RemoteAddr
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func writeAdminAuthError(w http.ResponseWriter) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusUnauthorized)
	_, _ = w.Write([]byte("需要管理令牌：请用 /admin?token=<管理令牌> 打开本页\n" +
		"（管理令牌是服务器 systemd 配置里的 RC_ADMIN_TOKEN，独立于发给客户的中继令牌；\n" +
		"中继令牌已随主控端分发给客户，不能用来打开本页）\n" +
		"未配置管理令牌时，可在服务器本机执行：curl -s \"http://127.0.0.1:8080/admin?token=<中继令牌>\"\n"))
}

// writeAdminPage 渲染授权管理页。errorStyle=true 时提示条用红色。
func writeAdminPage(w http.ResponseWriter, hub *Hub, lic *LicenseManager, msg string, errorStyle bool) {
	state, reason, payload, _ := lic.Status()
	token := maskAdminToken(hub)

	var b strings.Builder
	b.WriteString(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">`)
	b.WriteString(`<meta name="viewport" content="width=device-width,initial-scale=1">`)
	b.WriteString(`<title>授权管理 · 远程控制中继</title><style>`)
	b.WriteString(`body{background:#1e1e1e;color:#e6e6e6;font-family:"Microsoft YaHei UI",system-ui,sans-serif;margin:0;padding:28px}`)
	b.WriteString(`.card{max-width:760px;margin:0 auto;background:#252526;border-radius:10px;padding:22px 24px;box-shadow:0 2px 12px rgba(0,0,0,.4)}`)
	b.WriteString(`h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 8px;color:#9ad}`)
	b.WriteString(`table{border-collapse:collapse;width:100%;font-size:14px}td{padding:6px 4px;border-bottom:1px solid #333}`)
	b.WriteString(`td:first-child{color:#9a9a9a;width:130px}`)
	b.WriteString(`textarea{width:100%;min-height:110px;background:#3a3a3d;color:#e6e6e6;border:0;border-radius:6px;padding:10px;font-family:Consolas,monospace;font-size:12px;box-sizing:border-box}`)
	b.WriteString(`input[type=text]{width:100%;background:#3a3a3d;color:#e6e6e6;border:0;border-radius:6px;padding:9px;font-size:13px;box-sizing:border-box;margin-top:8px}`)
	b.WriteString(`button{background:#007acc;color:#fff;border:0;border-radius:6px;padding:10px 18px;font-size:14px;cursor:pointer;margin-right:8px}`)
	b.WriteString(`button.ghost{background:#3a3a3d}`)
	b.WriteString(`button.danger{background:#a33;padding:4px 10px;font-size:12px;margin:0}`)
	b.WriteString(`.ok{color:#9ae29a}.bad{color:#ff9a9a}.hint{color:#9a9a9a;font-size:12px;line-height:1.7}`)
	b.WriteString(`.mono{font-family:Consolas,monospace;font-size:12px;color:#bbb}`)
	b.WriteString(`.msg{background:#2d3b2d;border-left:3px solid #9ae29a;padding:10px 12px;border-radius:4px;margin:14px 0;font-size:13px}`)
	b.WriteString(`.msg.bad{background:#3b2d2d;border-left-color:#ff9a9a}`)
	b.WriteString(`</style></head><body><div class="card">`)
	b.WriteString(`<h1>授权管理 · 香港中继</h1>`)
	b.WriteString(`<div class="hint">中继的时间限制就在这里续期：粘贴上位机（LicenseKeygen）生成的激活码 → 点「安装/续期」。</div>`)

	if msg != "" {
		cls := "msg"
		if errorStyle || strings.Contains(msg, "失败") || strings.Contains(msg, "请先") {
			cls = "msg bad"
		}
		b.WriteString(`<div class="` + cls + `">` + htmlEscape(msg) + `</div>`)
	}

	b.WriteString(`<h2>当前授权</h2><table>`)
	b.WriteString(row("状态", stateBadge(string(state), reason)))
	b.WriteString(row("部署ID", esc(lic.DeploymentID())))
	if payload != nil {
		left := payload.ExpiresAt().Sub(time.Now()).Hours() / 24
		b.WriteString(row("授权编号", esc(payload.Lic)))
		if payload.Sub != "" {
			b.WriteString(row("客户/备注", esc(payload.Sub)))
		}
		b.WriteString(row("生效 / 到期", esc(payload.NotBefore().Format("2006-01-02"))+" ~ "+
			esc(payload.ExpiresAt().Format("2006-01-02"))))
		b.WriteString(row("剩余", sprintfDays(left)))
		if payload.IP != "" {
			b.WriteString(row("绑定公网IP", esc(payload.IP)))
		}
		if payload.MaxViewers > 0 {
			b.WriteString(row("最大主控端数", itoa(payload.MaxViewers)))
		}
	}
	b.WriteString(row("管理令牌", token))
	b.WriteString(`</table>`)

	b.WriteString(`<h2>安装 / 续期激活码</h2>`)
	b.WriteString(`<form method="post" action="/admin/install` + tokenQuery(hub) + `">`)
	b.WriteString(`<textarea name="code" placeholder="把激活码（RC1.xxxx.xxxx 一整行）粘到这里"></textarea>`)
	b.WriteString(`<div style="margin-top:12px"><button type="submit">安装 / 续期</button>`)
	b.WriteString(`<button class="ghost" type="button" onclick="location.reload()">刷新状态</button></form>`)
	b.WriteString(`<div class="hint" style="margin-top:10px">安装成功后：中继立即恢复可用，被控端/主控端会在 60 秒内自动重连（不用手动做任何事）。<br>`)
	b.WriteString(`也可以继续用命令行：<code>/opt/rcserver/rcctl install "&lt;激活码&gt;"</code></div>`)

	b.WriteString(`<h2>撤销名单（删除即失效）</h2>`)
	b.WriteString(`<div class="hint">在上位机（LicenseKeygen）的「激活码管理」里删掉一条码，它就会被送到这里并立刻失效：` +
		`<b>已装在服务器上的会马上停用，重新粘贴也装不回来</b>；被控端下次连上来（60 秒内）会自行停机。<br>` +
		`也可以直接用下面这个表单撤销，不必开上位机。</div>`)
	if entries := lic.RevokedEntries(); len(entries) > 0 {
		b.WriteString(`<table>`)
		for _, e := range entries {
			label := e.Lic
			if label == "" {
				label = "（无编号）"
			}
			if e.Sub != "" {
				label += " · " + e.Sub
			}
			extra := ""
			if e.Exp > 0 {
				extra = " 原到期 " + time.Unix(e.Exp, 0).UTC().Format("2006-01-02")
			}
			note := e.Note
			if note != "" {
				note = " · " + esc(note)
			}
			b.WriteString(`<tr><td>` + esc(label) + `</td><td>` +
				esc(time.Unix(e.At, 0).UTC().Format("2006-01-02 15:04")) + ` UTC` + extra + note +
				`<br><span class="mono">` + esc(e.Hash[:12]) + `</span> ` +
				`<form method="post" action="/admin/unrevoke` + tokenQuery(hub) + `" style="display:inline"` +
				` onsubmit="return confirm('恢复这条激活码？恢复后它可以被重新安装。')">` +
				`<input type="hidden" name="hash" value="` + esc(e.Hash) + `">` +
				`<button class="danger" type="submit">恢复</button></form>` +
				`</td></tr>`)
		}
		b.WriteString(`</table>`)
	} else {
		b.WriteString(`<div class="hint">名单为空：目前没有任何被撤销的激活码。</div>`)
	}
	b.WriteString(`<form method="post" action="/admin/revoke` + tokenQuery(hub) + `" onsubmit="return confirm('确定撤销这条激活码？撤销后它会立即失效，且无法再安装。')">`)
	b.WriteString(`<textarea name="code" placeholder="要撤销的激活码原文（RC1.xxxx.xxxx 一整行）"></textarea>`)
	b.WriteString(`<input type="text" name="note" placeholder="备注（可选，例如：客户退款 / 停止服务）">`)
	b.WriteString(`<div style="margin-top:12px"><button type="submit">撤销这条激活码</button></div></form>`)

	b.WriteString(`<h2>排障</h2><form method="post" action="/admin/clear` + tokenQuery(hub) + `" onsubmit="return confirm('确定卸载授权？卸载后中继会立即拒绝所有通信（客户端随之停用）。')">`)
	b.WriteString(`<button class="ghost" type="submit">卸载授权</button>`)
	b.WriteString(`<span class="hint">（仅排障/测试用：卸载后需要重新安装激活码才能通信）</span></form>`)

	if hub.AdminToken == "" {
		b.WriteString(`<div class="hint" style="margin-top:20px;color:#e6b96a">⚠ 未配置管理令牌（RC_ADMIN_TOKEN）：本页目前仅限服务器本机打开。` +
			`请在 systemd 服务环境里配置一条独立的管理令牌，否则浏览器里无法进入本页。</div>`)
	}
	b.WriteString(`<div class="hint" style="margin-top:20px">提示：本页是管理入口，只认管理令牌（RC_ADMIN_TOKEN）。` +
		`发给客户的中继令牌（RC_TOKEN）打不开本页 —— 不要把带管理令牌的链接发给别人。</div>`)
	b.WriteString(`</div></body></html>`)

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte(b.String()))
}

// tokenQuery 把管理令牌放进表单 action 的查询串（adminAuthorized 认 ?token=）。
// 页面既然是带着管理令牌打开的，表单就回填同一条令牌。
func tokenQuery(hub *Hub) string {
	token := hub.AdminToken
	if token == "" {
		token = hub.Token
	}
	if token == "" {
		return ""
	}
	return "?token=" + url.QueryEscape(token)
}

// maskAdminToken 显示管理令牌的前 4 位（未配置时明说，别让人误以为带令牌就能进来）。
func maskAdminToken(hub *Hub) string {
	if hub.AdminToken == "" {
		return "（未配置 RC_ADMIN_TOKEN：本页仅限服务器本机打开）"
	}
	return maskToken(hub.AdminToken)
}

// maskToken 只显示令牌的前 4 位，便于确认"我拿的是哪一条令牌"，又不至于泄露。
func maskToken(token string) string {
	if token == "" {
		return "（未设置：中继不做令牌校验）"
	}
	if len(token) <= 4 {
		return "****"
	}
	return token[:4] + strings.Repeat("*", 8)
}

func row(k, v string) string { return "<tr><td>" + k + "</td><td>" + v + "</td></tr>" }

func stateBadge(state, reason string) string {
	cls, text := "bad", state
	switch state {
	case "ok":
		cls, text = "ok", "有效（正常通信）"
	case "expired":
		text = "已过期（拒绝通信）"
	case "not_yet":
		text = "尚未生效"
	case "missing":
		text = "未授权（从未安装激活码）"
	case "invalid":
		text = "无效（签名/绑定不符或文件被改）"
	case "revoked":
		text = "已撤销（厂商删除了这条激活码，拒绝通信）"
	case "clock_anomaly":
		text = "时钟异常（服务器时间被往回拨，已锁定）"
	}
	out := `<span class="` + cls + `">` + esc(text) + `</span>`
	if reason != "" {
		out += ` <span class="hint">` + esc(reason) + `</span>`
	}
	return out
}

func sprintfDays(days float64) string {
	if days < 0 {
		return `<span class="bad">已过期</span>`
	}
	return itoa(int(days)) + " 天"
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var buf [20]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

// esc 做最小 HTML 转义（避免激活码里的字符破坏页面）。
func esc(s string) string {
	r := strings.NewReplacer("&", "&amp;", "<", "&lt;", ">", "&gt;", `"`, "&quot;")
	return r.Replace(s)
}

func htmlEscape(s string) string { return esc(s) }
