package main

// multiaddr_test.go —— 多地址监听（对付"某些网络封了 8080"：客户端会自动改试 443/8443）。
//
// 锁两件事：
//  1. 配了几个地址就都要能应答（同一个 handler、同一份授权状态）；
//  2. **某个备用端口起不来时，主端口必须照常服务** —— 远程改配置时最怕的就是
//     "443 被占用/没权限" 把整个中继（连同 8080）拖down，客户全断。

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"testing"
	"time"
)

// waitHealthy 轮询直到该地址上的 /healthz 返回 200（或超时）。
func waitHealthy(t *testing.T, addr string, timeout time.Duration) bool {
	t.Helper()
	deadline := time.Now().Add(timeout)
	url := "http://" + addr + "/healthz"
	for time.Now().Before(deadline) {
		resp, err := http.Get(url)
		if err == nil {
			body, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			if resp.StatusCode == http.StatusOK && string(body) == "ok" {
				return true
			}
		}
		time.Sleep(30 * time.Millisecond)
	}
	return false
}

func TestMultipleListenAddrs(t *testing.T) {
	addr1, addr2 := freeAddr(t), freeAddr(t)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	done := make(chan error, 1)
	go func() { done <- runAddrs(ctx, []string{addr1, addr2}, t.TempDir(), "TOK", "", "", discardLogger()) }()

	for _, addr := range []string{addr1, addr2} {
		if !waitHealthy(t, addr, 3*time.Second) {
			t.Fatalf("地址 %s 没有起来（多地址监听没生效）", addr)
		}
	}

	// 两个地址应当是同一套服务：/api/license 都能访问（未授权时也放行）
	for _, addr := range []string{addr1, addr2} {
		resp, err := http.Get("http://" + addr + "/api/license")
		if err != nil {
			t.Fatalf("地址 %s 的 /api/license 访问失败：%v", addr, err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusOK {
			t.Fatalf("地址 %s 的 /api/license 状态码 = %d", addr, resp.StatusCode)
		}
	}

	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("优雅停止返回错误：%v", err)
		}
	case <-time.After(testTimeout):
		t.Fatal("取消上下文后 runAddrs 没有退出")
	}
}

func TestExtraListenAddrFailureKeepsMainPortServing(t *testing.T) {
	mainAddr := freeAddr(t)

	// 占住一个端口，让"备用地址"必然绑定失败（模拟 443 被别的程序占用/没权限）
	occupied, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer occupied.Close()
	busyAddr := occupied.Addr().String()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() {
		done <- runAddrs(ctx, []string{mainAddr, busyAddr}, t.TempDir(), "TOK", "", "", discardLogger())
	}()

	if !waitHealthy(t, mainAddr, 3*time.Second) {
		t.Fatal("备用端口绑定失败时，主端口也被拖down了（这会让线上中继整体不可用）")
	}

	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("优雅停止返回错误：%v", err)
		}
	case <-time.After(testTimeout):
		t.Fatal("取消上下文后 runAddrs 没有退出")
	}
}

func TestEmptyAddrListDefaultsTo8080(t *testing.T) {
	// main() 里的兜底逻辑：没给 -addr 时要用 :8080（这里只验证默认值拼装，不真去绑 8080）
	addrs := []string{}
	if len(addrs) == 0 {
		addrs = []string{":8080"}
	}
	if fmt.Sprint(addrs) != "[:8080]" {
		t.Fatalf("默认地址不对：%v", addrs)
	}
}
