package main

// rcserver is the Hong Kong relay server of the remote control project.
//
// It offers two services:
//
//   - a websocket relay (/ws) pairing agents with viewers, forwarding frames
//     verbatim in both directions;
//   - a temporary file staging area (/api/upload, /api/download, /api/file)
//     whose contents expire ten minutes after they are written.
//
// Usage:
//
//	rcserver -addr :8080 -data ./data

import (
	"strings"
	"path/filepath"
	"encoding/hex"
	"crypto/rand"
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

// shutdownTimeout bounds the graceful shutdown sequence.
const shutdownTimeout = 5 * time.Second

func main() {
	// -addr 可以重复指定（如 -addr :8080 -addr :443）：有些网络会封 8080，
	// 客户端内置了"同主机 443/8443 自动重试"，服务端多听一个端口，那些机器就能连上。
	var addrs []string
	flag.Func("addr", "listen address；可重复指定，例如 -addr :8080 -addr :443（默认 :8080）", func(v string) error {
		v = strings.TrimSpace(v)
		if v != "" {
			addrs = append(addrs, v)
		}
		return nil
	})
	dataDir := flag.String("data", "./data", "directory used to stage uploaded files")
	token := flag.String("token", os.Getenv("RC_TOKEN"),
		"shared secret required on /ws and the file API (env RC_TOKEN; empty = no auth)")
	adminToken := flag.String("admin-token", os.Getenv("RC_ADMIN_TOKEN"),
		"admin secret required on /admin* (env RC_ADMIN_TOKEN); MUST differ from -token: "+
			"the relay token ships to customers inside the viewer/agent packages")
	licenseCode := flag.String("license", "", "install an activation code and exit")
	licenseStatus := flag.Bool("license-status", false, "print license status and exit")
	printID := flag.Bool("print-id", false, "print this deployment's id (used to bind a license) and exit")
	publicIP := flag.String("public-ip", os.Getenv("RC_PUBLIC_IP"),
		"advertised public IP, used for license binding when the code binds an IP")
	flag.Parse()

	logger := log.New(os.Stdout, "[rcserver] ", log.LstdFlags|log.Lmsgprefix)

	// SIGINT (Ctrl+C / Ctrl+Break) and SIGTERM both ask for a graceful stop.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// 授权相关的离线命令（不需要启动服务）
	if *printID || *licenseCode != "" || *licenseStatus {
		id, err := loadOrCreateDeploymentID(*dataDir)
		if err != nil {
			logger.Fatalf("deployment id: %v", err)
		}
		lm, err := NewLicenseManager(*dataDir, id, *publicIP, logger)
		if err != nil {
			logger.Fatalf("license: %v", err)
		}
		switch {
		case *printID:
			fmt.Println(id)
		case *licenseCode != "":
			if err := lm.Install(*licenseCode); err != nil {
				fmt.Fprintf(os.Stderr, "激活失败：%v\n", err)
				os.Exit(2)
			}
			state, reason, payload, _ := lm.Status()
			fmt.Printf("激活成功：状态=%s\n", state)
			if payload != nil {
				fmt.Printf("  授权编号 : %s\n", payload.Lic)
				fmt.Printf("  客户备注 : %s\n", payload.Sub)
				fmt.Printf("  绑定部署 : %s\n", payload.Srv)
				fmt.Printf("  有效期   : %s ~ %s\n",
					payload.NotBefore().Format("2006-01-02 15:04:05"),
					payload.ExpiresAt().Format("2006-01-02 15:04:05"))
			}
			if reason != "" {
				fmt.Printf("  说明     : %s\n", reason)
			}
		default:
			state, reason, payload, _ := lm.Status()
			fmt.Printf("部署ID   : %s\n", id)
			fmt.Printf("授权状态 : %s\n", state)
			if payload != nil {
				fmt.Printf("授权编号 : %s / 客户 %s\n", payload.Lic, payload.Sub)
				fmt.Printf("有效期   : %s ~ %s\n",
					payload.NotBefore().Format("2006-01-02 15:04:05"),
					payload.ExpiresAt().Format("2006-01-02 15:04:05"))
			}
			if reason != "" {
				fmt.Printf("说明     : %s\n", reason)
			}
			if state != LicenseOK {
				os.Exit(3)
			}
		}
		return
	}

	if len(addrs) == 0 {
		addrs = []string{":8080"}
	}
	if err := runAddrs(ctx, addrs, *dataDir, *token, *adminToken, *publicIP, logger); err != nil {
		logger.Fatalf("startup: %v", err)
	}
}

// loadOrCreateDeploymentID 读取或生成部署ID（用于把激活码绑定到这台服务器）。
func loadOrCreateDeploymentID(dataDir string) (string, error) {
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return "", err
	}
	path := filepath.Join(dataDir, "deployment-id")
	if b, err := os.ReadFile(path); err == nil {
		if id := strings.TrimSpace(string(b)); id != "" {
			return id, nil
		}
	}
	buf := make([]byte, 10)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	id := "DEP-" + strings.ToUpper(hex.EncodeToString(buf))
	if err := os.WriteFile(path, []byte(id+"\n"), 0o600); err != nil {
		return "", err
	}
	return id, nil
}

// run serves until ctx is cancelled, then stops the listener gracefully. It is
// split out of main so tests can drive a real shutdown.
// run 是单地址入口（测试与既有调用方用），内部转给 runAddrs。
func run(ctx context.Context, addr, dataDir, token, publicIP string, logger *log.Logger) error {
	return runAddrs(ctx, []string{addr}, dataDir, token, "", publicIP, logger)
}

// runAddrs 在多个地址上监听同一套路由（同一个 handler、同一份授权状态）。
// 多个地址是为了对付"某些网络封了 8080"：客户端内置了同主机 443/8443 自动重试，
// 服务端多听一个端口，那些机器不用改任何配置就能连上。
func runAddrs(ctx context.Context, addrs []string, dataDir, token, adminToken, publicIP string, logger *log.Logger) error {
	store, err := NewFileStore(dataDir, logger)
	if err != nil {
		return err
	}
	deploymentID, err := loadOrCreateDeploymentID(dataDir)
	if err != nil {
		return err
	}
	lic, err := NewLicenseManager(dataDir, deploymentID, publicIP, logger)
	if err != nil {
		return err
	}
	switch state, reason, payload, _ := lic.Status(); state {
	case LicenseOK:
		logger.Printf("license: OK  id=%s  expires=%s  deployment=%s",
			payload.Lic, payload.ExpiresAt().Format(time.RFC3339), deploymentID)
	default:
		logger.Printf("license: %s — %s（中继将拒绝一切通信，直到安装有效激活码）", state, reason)
	}
	store.Token = token
	hub := NewHub(logger)
	hub.Token = token
	hub.AdminToken = adminToken
	hub.License = lic // 授权管理页（/admin）要用它读状态、装激活码
	if token != "" {
		logger.Printf("auth: shared token required")
	} else {
		logger.Printf("auth: DISABLED (no -token / RC_TOKEN) — only safe on loopback/LAN")
	}
	if adminToken != "" {
		if adminToken == token {
			logger.Printf("auth: WARNING -admin-token 与 -token 相同 —— 管理令牌必须独立（中继令牌已发给客户）")
		} else {
			logger.Printf("auth: admin endpoints require the separate admin token")
		}
	} else {
		logger.Printf("auth: WARNING 未配置 -admin-token / RC_ADMIN_TOKEN：/admin 仅限服务器本机访问；" +
			"要远程管理授权请在 systemd 环境里配置一条独立的管理令牌")
	}

	handler := withLicense(newMux(hub, store), lic, hub)

	// 先逐个绑定：某个端口被占用/没权限只记日志，不影响其它端口 ——
	// 备用端口（443/8443）本来就是"能起就赚到"，不该把主端口 8080 一起拖down。
	// 但全都没绑上就是致命的（否则 systemd 认为服务活着，实际没人连得上）。
	type listener struct {
		ln  net.Listener
		srv *http.Server
	}
	var listeners []listener
	for _, a := range addrs {
		ln, err := net.Listen("tcp", a)
		if err != nil {
			logger.Printf("warning: 监听 %s 失败（不影响其它端口）：%v", a, err)
			continue
		}
		listeners = append(listeners, listener{ln: ln, srv: &http.Server{Handler: handler, ReadHeaderTimeout: 15 * time.Second}})
	}
	if len(listeners) == 0 {
		return fmt.Errorf("没有任何监听地址成功：%v", addrs)
	}

	// 到期/失效即时生效：每 30 秒复验，一旦由可用变不可用就断开所有连接
	lic.SetOnLocked(func(reason string) {
		logger.Printf("license: 授权失效，断开所有连接（%s）", reason)
		// 先明确告诉在线客户端"授权已不可用"（客户端据此本地停机，含 P2P 直连）。
		// 只断开连接是不够的：被控端会退避重连，而重连被握手拒绝后它仍会拿着本地
		// 缓存的旧码继续跑到期 —— 撤销必须"叫醒"它，不能只踢它。
		hub.PushLicenseAll()
		hub.CloseAll("license: " + reason)
	})
	lic.StartRecheckLoop(ctx.Done(), 30*time.Second)

	go store.RunCleanup(ctx)

	serveErr := make(chan error, len(listeners))
	for _, l := range listeners {
		logger.Printf("listening on %s (data dir %s, max upload %d bytes, file ttl %s)",
			l.ln.Addr(), store.Dir(), store.MaxFileSize(), DefaultFileTTL)
		go func(l listener) {
			if err := l.srv.Serve(l.ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
				logger.Printf("warning: 监听 %s 已在服务中出错：%v", l.ln.Addr(), err)
				serveErr <- fmt.Errorf("%s: %w", l.ln.Addr(), err)
			}
		}(l)
	}

	select {
	case err := <-serveErr:
		return fmt.Errorf("serve: %w", err)
	case <-ctx.Done():
		logger.Printf("shutdown: signal received, stopping listeners")
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), shutdownTimeout)
	defer cancel()
	for _, l := range listeners {
		if err := l.srv.Shutdown(shutdownCtx); err != nil {
			logger.Printf("shutdown %s: %v", l.ln.Addr(), err)
			_ = l.srv.Close()
		}
	}
	logger.Printf("shutdown: done")
	return nil
}

// newMux wires every endpoint of the relay server. It is shared by main and by
// the tests, so both exercise exactly the same routes.
func newMux(hub *Hub, store *FileStore) *http.ServeMux {
	mux := http.NewServeMux()
	hub.Register(mux)
	store.Register(mux)
	registerAdmin(mux, hub, hub.License, hub.Logger())
	mux.HandleFunc("GET /healthz", handleHealthz)
	return mux
}

// adminPaths 是授权闸门必须放行的管理路径：授权过期时正是最需要能进来续期的时候。
// 这些路径自己校验令牌（见 registerAdmin），所以放行不等于裸奔。
func adminPaths(path string) bool {
	return path == "/admin" || strings.HasPrefix(path, "/admin/")
}

func handleHealthz(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.Header().Set("Content-Length", "2")
	w.WriteHeader(http.StatusOK)
	_, _ = io.WriteString(w, "ok")
}

// withLicense 是授权闸门：除 /healthz 与 /api/license 外，授权不通过一律拒绝。
// 放在最外层，任何新加的路由默认都被保护（"失败即锁"，不存在漏加保护的问题）。
func withLicense(next http.Handler, lic *LicenseManager, hub *Hub) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		if path == "/healthz" || path == "/api/license" || adminPaths(path) {
			// 授权状态接口在未授权时也要能访问（排障用），只回状态不回激活码原文
			if path == "/api/license" {
				state, reason, payload, _ := lic.Status()
				out := map[string]any{
					"state":      string(state),
					"reason":     reason,
					"deployment": lic.DeploymentID(),
					// hash = 当前已装激活码的 sha256 指纹（不泄露码本身，也无法从中反推）。
					// 上位机用它跟本地台账精确比对，判断"这条码正在被使用"。
					"hash":         lic.CurrentHash(),
					"revokedCount": lic.RevokedCount(),
				}
				if installed := lic.InstalledAt(); installed > 0 {
					out["installedAt"] = time.Unix(installed, 0).UTC().Format(time.RFC3339)
				}
				if payload != nil {
					out["lic"] = payload.Lic
					out["sub"] = payload.Sub
					out["expires"] = payload.ExpiresAt().Format(time.RFC3339)
				}
				writeJSON(w, http.StatusOK, out)
				return
			}
			next.ServeHTTP(w, r)
			return
		}
		if ok, state, reason := lic.Check(); !ok {
			w.Header().Set("X-RC-License", string(state))
			if r.Header.Get("Upgrade") != "" {
				// WebSocket 握手：给明确的拒绝原因
				writeJSONError(w, http.StatusForbidden, "license_"+string(state), reason)
				return
			}
			writeJSONError(w, http.StatusForbidden, "license_"+string(state), reason)
			return
		}
		// 顺便把授权状态告诉对端（供客户端二次校验）
		w.Header().Set("X-RC-License", "ok")
		next.ServeHTTP(w, r)
	})
}
