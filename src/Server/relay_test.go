package main

// relay_test.go drives the relay over real websocket connections: every test
// dials the same handler chain main serves (newMux) on an httptest server, so
// the routing, the upgrade and the forwarding are all exercised for real.

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"mime/multipart"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

const (
	testTimeout = 5 * time.Second
	// stressTimeout bounds a single read in the concurrency test.
	stressTimeout = 15 * time.Second
)

// dialWithRetry connects and optionally waits for the agent registry to catch
// up, so the concurrency test does not mistake a slow registration for a bug.
func dialWithRetry(server *httptest.Server, path string) (*websocket.Conn, error) {
	var lastErr error
	for attempt := 0; attempt < 20; attempt++ {
		conn, _, err := websocket.DefaultDialer.Dial(wsURL(server, path), nil)
		if err == nil {
			return conn, nil
		}
		lastErr = err
		time.Sleep(20 * time.Millisecond)
	}
	return nil, lastErr
}

// waitForAgentErr is waitForAgent without t.Fatalf, for use from goroutines.
func waitForAgentErr(server *httptest.Server, id string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		resp, err := http.Get(server.URL + "/api/agents")
		if err == nil {
			var payload agentsResponse
			decodeErr := json.NewDecoder(resp.Body).Decode(&payload)
			resp.Body.Close()
			if decodeErr == nil {
				for _, agent := range payload.Agents {
					if agent.ID == id {
						return nil
					}
				}
			}
		}
		time.Sleep(5 * time.Millisecond)
	}
	return fmt.Errorf("agent %q never appeared in /api/agents", id)
}

func discardLogger() *log.Logger { return log.New(io.Discard, "", 0) }

// newTestServer starts the whole server (relay + staging area) on a random
// port, exactly as main wires it.
func newTestServer(t *testing.T) (*Hub, *FileStore, *httptest.Server) {
	t.Helper()
	store := newTestStore(t, DefaultMaxFileSize, DefaultFileTTL, DefaultCleanupInterval)
	return newTestServerWithStore(t, store)
}

func newTestServerWithStore(t *testing.T, store *FileStore) (*Hub, *FileStore, *httptest.Server) {
	t.Helper()
	hub := NewHub(discardLogger())
	server := httptest.NewServer(newMux(hub, store))
	t.Cleanup(server.Close)
	return hub, store, server
}

func wsURL(server *httptest.Server, path string) string {
	return "ws" + strings.TrimPrefix(server.URL, "http") + path
}

func dialWS(t *testing.T, server *httptest.Server, path string) *websocket.Conn {
	t.Helper()
	conn, resp, err := websocket.DefaultDialer.Dial(wsURL(server, path), nil)
	if err != nil {
		status := 0
		if resp != nil {
			status = resp.StatusCode
		}
		t.Fatalf("dial %s: %v (http status %d)", path, err, status)
	}
	_ = conn.SetReadDeadline(time.Now().Add(testTimeout))
	t.Cleanup(func() { _ = conn.Close() })
	return conn
}

// relayControlNotice reports whether a text frame is one of the relay's own
// control notices (license state / viewer count) rather than traffic the test
// itself sent. Tests skip them so they can assert on the frames that matter.
func relayControlNotice(messageType int, data []byte) bool {
	if messageType != websocket.TextMessage {
		return false
	}
	var probe struct {
		Type string `json:"type"`
	}
	if err := json.Unmarshal(data, &probe); err != nil {
		return false
	}
	return probe.Type == "viewers"   // license 通知由授权相关测试显式读取，这里不跳
}

// readFrame reads the next data frame, skipping the relay's own control notices
// (the license push on connect and the viewer-count notices pushed when viewers
// attach/detach).
func readFrame(t *testing.T, conn *websocket.Conn, context string) (int, []byte) {
	t.Helper()
	for attempt := 0; attempt < 10; attempt++ {
		_ = conn.SetReadDeadline(time.Now().Add(testTimeout))
		messageType, data, err := conn.ReadMessage()
		if err != nil {
			t.Fatalf("%s: read: %v", context, err)
		}
		if relayControlNotice(messageType, data) {
			continue
		}
		return messageType, data
	}
	t.Fatalf("%s: 只读到中继控制通知，没等到数据帧", context)
	return 0, nil
}

func assertFrame(t *testing.T, conn *websocket.Conn, wantType int, want []byte, context string) {
	t.Helper()
	gotType, got := readFrame(t, conn, context)
	if gotType != wantType {
		t.Fatalf("%s: websocket frame type = %d, want %d", context, gotType, wantType)
	}
	if !bytes.Equal(got, want) {
		t.Fatalf("%s: payload changed in transit: got %d bytes, want %d bytes", context, len(got), len(want))
	}
}

// readCloseError reads until the peer's close frame shows up, skipping any data
// frame that is still in flight.
func readCloseError(t *testing.T, conn *websocket.Conn, context string) *websocket.CloseError {
	t.Helper()
	deadline := time.Now().Add(testTimeout)
	for time.Now().Before(deadline) {
		_ = conn.SetReadDeadline(time.Now().Add(testTimeout))
		_, _, err := conn.ReadMessage()
		if err == nil {
			continue // a data frame can still be in flight before the close
		}
		var closeErr *websocket.CloseError
		if !errors.As(err, &closeErr) {
			t.Fatalf("%s: expected a websocket close, got %v", context, err)
		}
		return closeErr
	}
	t.Fatalf("%s: no close frame within %s", context, testTimeout)
	return nil
}

// expectClose reads until the peer's close frame shows up and checks its code.
func expectClose(t *testing.T, conn *websocket.Conn, wantCode int, context string) {
	t.Helper()
	closeErr := readCloseError(t, conn, context)
	if closeErr.Code != wantCode {
		t.Fatalf("%s: close code = %d (%q), want %d", context, closeErr.Code, closeErr.Text, wantCode)
	}
}

func fetchAgents(t *testing.T, server *httptest.Server) []AgentInfo {
	t.Helper()
	resp, err := http.Get(server.URL + "/api/agents")
	if err != nil {
		t.Fatalf("GET /api/agents: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("GET /api/agents: status %d", resp.StatusCode)
	}
	if ct := resp.Header.Get("Content-Type"); !strings.HasPrefix(ct, "application/json") {
		t.Fatalf("GET /api/agents: content type %q", ct)
	}
	var payload agentsResponse
	if err := json.NewDecoder(resp.Body).Decode(&payload); err != nil {
		t.Fatalf("GET /api/agents: decode: %v", err)
	}
	return payload.Agents
}

// waitForAgent polls /api/agents until the agent is registered: the upgrade
// response reaches the client slightly before the handler registers the id.
func waitForAgent(t *testing.T, server *httptest.Server, id string) {
	t.Helper()
	deadline := time.Now().Add(testTimeout)
	for time.Now().Before(deadline) {
		for _, agent := range fetchAgents(t, server) {
			if agent.ID == id {
				return
			}
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("agent %q never appeared in /api/agents", id)
}

func TestRelayForwardsFramesVerbatimBothWays(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=laptop-01")
	waitForAgent(t, server, "laptop-01")
	viewer := dialWS(t, server, "/ws?role=viewer&target=laptop-01")

	binary := []byte{0x00, 0x01, 0xfe, 0xff, 0x7f, 0x80, 'R', 'C'}
	agent.WriteMessage(websocket.BinaryMessage, binary) //nolint:errcheck
	assertFrame(t, viewer, websocket.BinaryMessage, binary, "agent -> viewer (binary)")

	back := []byte("viewer-to-agent-binary\x00\x01\x02")
	viewer.WriteMessage(websocket.BinaryMessage, back) //nolint:errcheck
	assertFrame(t, agent, websocket.BinaryMessage, back, "viewer -> agent (binary)")

	text := "text frame / 文本帧 / {\"not\":\"parsed\"}"
	viewer.WriteMessage(websocket.TextMessage, []byte(text)) //nolint:errcheck
	assertFrame(t, agent, websocket.TextMessage, []byte(text), "viewer -> agent (text)")

	agent.WriteMessage(websocket.TextMessage, []byte(text)) //nolint:errcheck
	assertFrame(t, viewer, websocket.TextMessage, []byte(text), "agent -> viewer (text)")
}

func TestRelayForwardsLargeAndEmptyFrames(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=laptop-02")
	waitForAgent(t, server, "laptop-02")
	viewer := dialWS(t, server, "/ws?role=viewer&target=laptop-02")

	large := bytes.Repeat([]byte("rc"), 200*1024)      // 400 KiB, crosses the 4 KiB write buffer
	agent.WriteMessage(websocket.BinaryMessage, large) //nolint:errcheck
	assertFrame(t, viewer, websocket.BinaryMessage, large, "agent -> viewer (400 KiB)")

	empty := []byte{}
	viewer.WriteMessage(websocket.BinaryMessage, empty) //nolint:errcheck
	gotType, got := readFrame(t, agent, "viewer -> agent (empty)")
	if gotType != websocket.BinaryMessage || len(got) != 0 {
		t.Fatalf("empty binary frame came back as type=%d len=%d", gotType, len(got))
	}
}

func TestViewerOfUnknownAgentGetsOfflineErrorThenClose(t *testing.T) {
	_, _, server := newTestServer(t)

	viewer := dialWS(t, server, "/ws?role=viewer&target=ghost-agent")

	messageType, payload := readFrame(t, viewer, "offline error")
	if messageType != websocket.TextMessage {
		t.Fatalf("offline notice frame type = %d, want %d", messageType, websocket.TextMessage)
	}
	const want = `{"type":"error","code":"agent_offline","agentId":"ghost-agent"}`
	if string(payload) != want {
		t.Fatalf("offline notice = %s, want %s", payload, want)
	}
	expectClose(t, viewer, closeCodeRelay, "offline viewer")
}

func TestAgentDisconnectClosesPairedViewer(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=box-1")
	waitForAgent(t, server, "box-1")
	viewer := dialWS(t, server, "/ws?role=viewer&target=box-1")

	// make sure the pairing is live before the agent drops
	agent.WriteMessage(websocket.BinaryMessage, []byte("ping-before-close")) //nolint:errcheck
	assertFrame(t, viewer, websocket.BinaryMessage, []byte("ping-before-close"), "pairing check")

	if err := agent.Close(); err != nil {
		t.Fatalf("close agent: %v", err)
	}
	expectClose(t, viewer, closeCodeRelay, "viewer after agent disconnect")

	if agents := fetchAgents(t, server); len(agents) != 0 {
		t.Fatalf("agent list after disconnect = %+v, want it empty", agents)
	}
}

// TestViewerDisconnectKeepsAgentOnline is the core of the main-control use
// case: the controller quits, the controlled machine must stay registered and
// be ready for the next viewer.
func TestViewerDisconnectKeepsAgentOnline(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=box-2")
	waitForAgent(t, server, "box-2")
	viewer := dialWS(t, server, "/ws?role=viewer&target=box-2")

	agent.WriteMessage(websocket.BinaryMessage, []byte("hello")) //nolint:errcheck
	assertFrame(t, viewer, websocket.BinaryMessage, []byte("hello"), "pairing check")

	if err := viewer.Close(); err != nil {
		t.Fatalf("close viewer: %v", err)
	}

	// The agent must stay connected and registered: no close frame, and the
	// registry still lists it with no viewers.
	deadline := time.Now().Add(testTimeout)
	for {
		agents := fetchAgents(t, server)
		if len(agents) == 1 && agents[0].ID == "box-2" && agents[0].Viewers == 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("agent registry after the viewer left = %+v, want box-2 with 0 viewers", agents)
		}
		time.Sleep(10 * time.Millisecond)
	}

	// A second viewer pairs with the very same agent connection, and the agent
	// is still able to relay on it.
	viewer2 := dialWS(t, server, "/ws?role=viewer&target=box-2")
	agent.WriteMessage(websocket.BinaryMessage, []byte("second viewer")) //nolint:errcheck
	assertFrame(t, viewer2, websocket.BinaryMessage, []byte("second viewer"), "second viewer receives")

	viewer2.WriteMessage(websocket.TextMessage, []byte("from second viewer")) //nolint:errcheck
	assertFrame(t, agent, websocket.TextMessage, []byte("from second viewer"), "agent receives from second viewer")

	// and the agent is still the same live connection
	if agents := fetchAgents(t, server); len(agents) != 1 || agents[0].Viewers != 1 {
		t.Fatalf("agent registry with the second viewer = %+v, want one agent with 1 viewer", agents)
	}
}

// TestAgentServesMultipleViewersAtOnce pins the one-to-many behaviour: each
// viewer gets its own pairing, frames from the agent reach all of them, and one
// viewer leaving does not disturb the others.
func TestAgentServesMultipleViewersAtOnce(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=desk-3")
	waitForAgent(t, server, "desk-3")

	first := dialWS(t, server, "/ws?role=viewer&target=desk-3")
	second := dialWS(t, server, "/ws?role=viewer&target=desk-3")

	deadline := time.Now().Add(testTimeout)
	for {
		agents := fetchAgents(t, server)
		if len(agents) == 1 && agents[0].Viewers == 2 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("viewer count = %+v, want 2 viewers on one agent", agents)
		}
		time.Sleep(10 * time.Millisecond)
	}

	// the agent's frame reaches both viewers
	agent.WriteMessage(websocket.BinaryMessage, []byte("broadcast")) //nolint:errcheck
	assertFrame(t, first, websocket.BinaryMessage, []byte("broadcast"), "first viewer")
	assertFrame(t, second, websocket.BinaryMessage, []byte("broadcast"), "second viewer")

	// each viewer has its own channel back to the agent
	first.WriteMessage(websocket.TextMessage, []byte("from-first")) //nolint:errcheck
	assertFrame(t, agent, websocket.TextMessage, []byte("from-first"), "agent <- first viewer")
	second.WriteMessage(websocket.TextMessage, []byte("from-second")) //nolint:errcheck
	assertFrame(t, agent, websocket.TextMessage, []byte("from-second"), "agent <- second viewer")

	// one viewer leaving leaves the other one working
	if err := first.Close(); err != nil {
		t.Fatalf("close first viewer: %v", err)
	}
	agent.WriteMessage(websocket.BinaryMessage, []byte("still here")) //nolint:errcheck
	assertFrame(t, second, websocket.BinaryMessage, []byte("still here"), "surviving viewer")

	second.WriteMessage(websocket.TextMessage, []byte("still talking")) //nolint:errcheck
	assertFrame(t, agent, websocket.TextMessage, []byte("still talking"), "agent after one viewer left")
}

// TestAgentDisconnectClosesEveryViewer is the other half of the rule: when the
// agent really goes away its viewers are closed with 1011 and an explicit
// reason, so a client can tell the two situations apart.
func TestAgentDisconnectClosesEveryViewer(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=box-9")
	waitForAgent(t, server, "box-9")
	first := dialWS(t, server, "/ws?role=viewer&target=box-9")
	second := dialWS(t, server, "/ws?role=viewer&target=box-9")

	agent.WriteMessage(websocket.BinaryMessage, []byte("paired")) //nolint:errcheck
	assertFrame(t, first, websocket.BinaryMessage, []byte("paired"), "first viewer")
	assertFrame(t, second, websocket.BinaryMessage, []byte("paired"), "second viewer")

	if err := agent.Close(); err != nil {
		t.Fatalf("close agent: %v", err)
	}

	for name, viewer := range map[string]*websocket.Conn{"first": first, "second": second} {
		closeErr := readCloseError(t, viewer, name+" viewer")
		if closeErr.Code != closeCodeRelay {
			t.Fatalf("%s viewer close code = %d, want %d", name, closeErr.Code, closeCodeRelay)
		}
		if closeErr.Text != closeReasonAgentGone {
			t.Fatalf("%s viewer close reason = %q, want %q", name, closeErr.Text, closeReasonAgentGone)
		}
	}
}

func TestAgentReconnectReplacesOldConnectionAndAllowsNewPairing(t *testing.T) {
	_, _, server := newTestServer(t)

	first := dialWS(t, server, "/ws?role=agent&id=phone-7")
	waitForAgent(t, server, "phone-7")
	viewer := dialWS(t, server, "/ws?role=viewer&target=phone-7")

	first.WriteMessage(websocket.BinaryMessage, []byte("before")) //nolint:errcheck
	assertFrame(t, viewer, websocket.BinaryMessage, []byte("before"), "viewer before reconnect")

	// The same id connects again: the new connection takes over.
	second := dialWS(t, server, "/ws?role=agent&id=phone-7")
	expectClose(t, first, closeCodeRelay, "replaced agent connection")
	expectClose(t, viewer, closeCodeRelay, "viewer of the replaced agent")

	// No stale state: a fresh viewer pairs with the new connection.
	viewer2 := dialWS(t, server, "/ws?role=viewer&target=phone-7")
	second.WriteMessage(websocket.BinaryMessage, []byte("after")) //nolint:errcheck
	assertFrame(t, viewer2, websocket.BinaryMessage, []byte("after"), "viewer after reconnect")

	agents := fetchAgents(t, server)
	if len(agents) != 1 || agents[0].ID != "phone-7" {
		t.Fatalf("agent list after reconnect = %+v, want exactly one phone-7", agents)
	}
	if agents[0].Viewers != 1 {
		t.Fatalf("viewer count after reconnect = %d, want 1", agents[0].Viewers)
	}
}

func TestAgentsEndpointReportsViewerCountAndTimestamps(t *testing.T) {
	_, _, server := newTestServer(t)

	if agents := fetchAgents(t, server); len(agents) != 0 {
		t.Fatalf("fresh server reports %+v, want no agents", agents)
	}

	dialWS(t, server, "/ws?role=agent&id=alpha")
	waitForAgent(t, server, "alpha")
	dialWS(t, server, "/ws?role=agent&id=beta")
	waitForAgent(t, server, "beta")

	viewer := dialWS(t, server, "/ws?role=viewer&target=alpha")
	viewer.WriteMessage(websocket.TextMessage, []byte("pair me")) //nolint:errcheck

	deadline := time.Now().Add(testTimeout)
	var agents []AgentInfo
	for time.Now().Before(deadline) {
		agents = fetchAgents(t, server)
		if len(agents) == 2 && agents[0].Viewers == 1 {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}

	if len(agents) != 2 {
		t.Fatalf("agent list = %+v, want two entries", agents)
	}
	if agents[0].ID != "alpha" || agents[1].ID != "beta" {
		t.Fatalf("agent ids = %q, %q; want alpha, beta", agents[0].ID, agents[1].ID)
	}
	if agents[0].Viewers != 1 || agents[1].Viewers != 0 {
		t.Fatalf("viewer counts = %d, %d; want 1, 0", agents[0].Viewers, agents[1].Viewers)
	}
	for _, agent := range agents {
		connectedAt, err := time.Parse(time.RFC3339, agent.ConnectedAt)
		if err != nil {
			t.Fatalf("connectedAt %q is not RFC3339: %v", agent.ConnectedAt, err)
		}
		if time.Since(connectedAt) > time.Minute {
			t.Fatalf("connectedAt %s looks wrong", connectedAt)
		}
	}
}

func TestWebsocketRejectsBadRequests(t *testing.T) {
	_, _, server := newTestServer(t)

	cases := []struct {
		name string
		path string
	}{
		{"no role", "/ws"},
		{"unknown role", "/ws?role=wat&id=x"},
		{"agent without id", "/ws?role=agent"},
		{"viewer without target", "/ws?role=viewer"},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			conn, resp, err := websocket.DefaultDialer.Dial(wsURL(server, testCase.path), nil)
			if conn != nil {
				_ = conn.Close()
			}
			if err == nil {
				t.Fatalf("dial %s succeeded, want a handshake failure", testCase.path)
			}
			if resp == nil {
				t.Fatalf("dial %s: no http response: %v", testCase.path, err)
			}
			if resp.StatusCode != http.StatusBadRequest {
				t.Fatalf("dial %s: status %d, want 400", testCase.path, resp.StatusCode)
			}
		})
	}
}

func TestHealthz(t *testing.T) {
	_, _, server := newTestServer(t)

	resp, err := http.Get(server.URL + "/healthz")
	if err != nil {
		t.Fatalf("GET /healthz: %v", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read /healthz: %v", err)
	}
	if resp.StatusCode != http.StatusOK || string(body) != "ok" {
		t.Fatalf("/healthz = %d %q, want 200 \"ok\"", resp.StatusCode, body)
	}
}

func TestOfflineMessageShape(t *testing.T) {
	got := offlineMessage("agent-42")
	const want = `{"type":"error","code":"agent_offline","agentId":"agent-42"}`
	if got != want {
		t.Fatalf("offlineMessage = %s, want %s", got, want)
	}
}

// TestSilentPeerIsDroppedAfterPongWait covers the read-timeout half of the
// keepalive policy: a peer that never answers a ping is dropped.
func TestSilentPeerIsDroppedAfterPongWait(t *testing.T) {
	heartbeat := Heartbeat{PingPeriod: 40 * time.Millisecond, PongWait: 250 * time.Millisecond}
	hub := NewHubWithHeartbeat(heartbeat, discardLogger())
	store := newTestStore(t, DefaultMaxFileSize, DefaultFileTTL, DefaultCleanupInterval)
	server := httptest.NewServer(newMux(hub, store))
	defer server.Close()

	agent := dialWS(t, server, "/ws?role=agent&id=silent-1")
	// A peer that swallows pings instead of answering them.
	agent.SetPingHandler(func(string) error { return nil })
	waitForAgent(t, server, "silent-1")

	// The client sends nothing at all while the pings pile up: the server has
	// to drop it once the pong wait expires.
	time.Sleep(heartbeat.PongWait + heartbeat.PongWait/2)

	expectClose(t, agent, closeCodeRelay, "silent agent")

	deadline := time.Now().Add(testTimeout)
	for {
		if agents := fetchAgents(t, server); len(agents) == 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("the silent agent is still registered after the pong wait")
		}
		time.Sleep(10 * time.Millisecond)
	}
}

// TestHeartbeatDefaults pins the documented policy.
func TestHeartbeatDefaults(t *testing.T) {
	if DefaultHeartbeat.PingPeriod != 30*time.Second {
		t.Fatalf("ping period = %s, want 30s", DefaultHeartbeat.PingPeriod)
	}
	if DefaultHeartbeat.PongWait != 60*time.Second {
		t.Fatalf("pong wait = %s, want 60s", DefaultHeartbeat.PongWait)
	}
}

// TestConcurrentRelayStress keeps many pairs busy at the same time: it is the
// guard for the hub's locking (and it is the interesting test under -race).
func TestConcurrentRelayStress(t *testing.T) {
	_, _, server := newTestServer(t)

	const (
		pairs         = 16
		framesPerPair = 25
	)

	// 容量留足余量（pairs*8）而非恰好够用：一旦某条 goroutine 因新增的失败
	// 路径多推一条错误，容量不足会让 wait.Wait() 永久阻塞（生产里是 goroutine
	// 泄漏 + 测试挂死）。宁可多给，也不要贴着上限。
	errors := make(chan string, pairs*8)
	var wait sync.WaitGroup
	for i := 0; i < pairs; i++ {
		wait.Add(1)
		go func(i int) {
			defer wait.Done()
			agentID := fmt.Sprintf("stress-%02d", i)

			agent, err := dialWithRetry(server, "/ws?role=agent&id="+agentID)
			if err != nil {
				errors <- fmt.Sprintf("agent %s: %v", agentID, err)
				return
			}
			defer agent.Close()

			if err := waitForAgentErr(server, agentID, testTimeout); err != nil {
				errors <- err.Error()
				return
			}
			viewer, err := dialWithRetry(server, "/ws?role=viewer&target="+agentID)
			if err != nil {
				errors <- fmt.Sprintf("viewer %s: %v", agentID, err)
				return
			}
			defer viewer.Close()

			toViewer := []byte("agent-to-viewer-" + agentID)
			toAgent := []byte("viewer-to-agent-" + agentID)

			var exchange sync.WaitGroup
			exchange.Add(4)
			go func() {
				defer exchange.Done()
				for frame := 0; frame < framesPerPair; frame++ {
					if err := agent.WriteMessage(websocket.BinaryMessage, toViewer); err != nil {
						return
					}
				}
			}()
			go func() {
				defer exchange.Done()
				for frame := 0; frame < framesPerPair; frame++ {
					if err := viewer.WriteMessage(websocket.BinaryMessage, toAgent); err != nil {
						return
					}
				}
			}()
			go func() {
				defer exchange.Done()
				for frame := 0; frame < framesPerPair; frame++ {
					_ = viewer.SetReadDeadline(time.Now().Add(stressTimeout))
					messageType, data, err := viewer.ReadMessage()
					if err != nil || messageType != websocket.BinaryMessage || !bytes.Equal(data, toViewer) {
						return
					}
				}
			}()
			go func() {
				defer exchange.Done()
				for frame := 0; frame < framesPerPair; frame++ {
					_ = agent.SetReadDeadline(time.Now().Add(stressTimeout))
					messageType, data, err := agent.ReadMessage()
					if err != nil || messageType != websocket.BinaryMessage || !bytes.Equal(data, toAgent) {
						return
					}
				}
			}()
			exchange.Wait()
		}(i)
	}
	wait.Wait()
	close(errors)
	for message := range errors {
		t.Errorf("stress: %s", message)
	}

	// every pair is gone: the registry must drain, with no stale entries
	deadline := time.Now().Add(testTimeout)
	for {
		agents := fetchAgents(t, server)
		if len(agents) == 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("registry still holds %d agents after the stress run", len(agents))
		}
		time.Sleep(10 * time.Millisecond)
	}
}

// TestGracefulShutdown drives the same run() main uses and checks that
// cancelling the signal context stops the listener instead of killing it.
func TestGracefulShutdown(t *testing.T) {
	addr := freeAddr(t)
	logger := log.New(io.Discard, "", 0)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- run(ctx, addr, t.TempDir(), "", "", logger) }()

	deadline := time.Now().Add(testTimeout)
	for {
		resp, err := http.Get("http://" + addr + "/healthz")
		if err == nil {
			resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				break
			}
		}
		if time.Now().After(deadline) {
			cancel()
			t.Fatal("server never became ready")
		}
		time.Sleep(10 * time.Millisecond)
	}

	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("run() returned %v, want nil after a graceful stop", err)
		}
	case <-time.After(testTimeout):
		t.Fatal("run() did not return after the context was cancelled")
	}

	if resp, err := http.Get("http://" + addr + "/healthz"); err == nil {
		resp.Body.Close()
		t.Fatalf("the listener is still accepting connections after shutdown")
	}
}

// freeAddr reserves a port and releases it again so run() can bind it.
func freeAddr(t *testing.T) string {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("reserve port: %v", err)
	}
	addr := listener.Addr().String()
	if err := listener.Close(); err != nil {
		t.Fatalf("release port: %v", err)
	}
	return addr
}

// ---- token auth ----

func TestTokenRequiredOnWSAndAgentsAPI(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	hub := NewHub(logger)
	hub.Token = "s3cret"
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	store.Token = "s3cret"
	srv := httptest.NewServer(newMux(hub, store))
	defer srv.Close()
	wsBase := "ws" + strings.TrimPrefix(srv.URL, "http")

	// 无令牌：/api/agents 401
	resp, err := http.Get(srv.URL + "/api/agents")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("/api/agents without token: got %d, want 401", resp.StatusCode)
	}

	// 错令牌：401
	resp, err = http.Get(srv.URL + "/api/agents?token=wrong")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("/api/agents wrong token: got %d, want 401", resp.StatusCode)
	}

	// 无令牌的 WS：握手被拒（非 101）
	_, wsResp, err := websocket.DefaultDialer.Dial(wsBase+"/ws?role=agent&id=a1", nil)
	if err == nil {
		t.Fatal("ws dial without token should fail")
	}
	if wsResp == nil || wsResp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("ws dial without token: got %v, want 401", wsResp)
	}

	// 正确令牌：/api/agents 200
	resp, err = http.Get(srv.URL + "/api/agents?token=s3cret")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/api/agents with token: got %d, want 200", resp.StatusCode)
	}

	// 正确令牌的 agent + viewer 可以正常配对
	agent, _, err := websocket.DefaultDialer.Dial(wsBase+"/ws?role=agent&id=a1&token=s3cret", nil)
	if err != nil {
		t.Fatalf("agent dial with token: %v", err)
	}
	defer agent.Close()
	viewer, _, err := websocket.DefaultDialer.Dial(wsBase+"/ws?role=viewer&target=a1&token=s3cret", nil)
	if err != nil {
		t.Fatalf("viewer dial with token: %v", err)
	}
	defer viewer.Close()

	if err := agent.WriteMessage(websocket.BinaryMessage, []byte("hi")); err != nil {
		t.Fatal(err)
	}
	_ = viewer.SetReadDeadline(time.Now().Add(2 * time.Second))
	_, data, err := viewer.ReadMessage()
	if err != nil {
		t.Fatalf("relay with token: %v", err)
	}
	if string(data) != "hi" {
		t.Fatalf("got %q, want hi", data)
	}
}

func TestTokenGuardsFileAPI(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	store, err := NewFileStore(t.TempDir(), logger)
	if err != nil {
		t.Fatal(err)
	}
	store.Token = "s3cret"
	srv := httptest.NewServer(newMux(NewHub(logger), store))
	defer srv.Close()

	// 无令牌上传 → 401
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	part, _ := mw.CreateFormFile("file", "a.txt")
	part.Write([]byte("hello"))
	mw.Close()
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/api/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("upload without token: got %d, want 401", resp.StatusCode)
	}

	// 带令牌上传 → 200 且能下载
	body.Reset()
	mw = multipart.NewWriter(&body)
	part, _ = mw.CreateFormFile("file", "a.txt")
	part.Write([]byte("hello"))
	mw.Close()
	req, _ = http.NewRequest(http.MethodPost, srv.URL+"/api/upload?token=s3cret", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	resp, err = http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode != http.StatusOK {
		b, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		t.Fatalf("upload with token: got %d (%s), want 200", resp.StatusCode, b)
	}
	var meta struct {
		FileID string `json:"fileId"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&meta); err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()

	dl, err := http.Get(srv.URL + "/api/download/" + meta.FileID + "?token=s3cret")
	if err != nil {
		t.Fatal(err)
	}
	defer dl.Body.Close()
	if dl.StatusCode != http.StatusOK {
		t.Fatalf("download with token: got %d, want 200", dl.StatusCode)
	}
	got, _ := io.ReadAll(dl.Body)
	if string(got) != "hello" {
		t.Fatalf("download body %q, want hello", got)
	}

	// 无令牌下载 → 401
	dl2, err := http.Get(srv.URL + "/api/download/" + meta.FileID)
	if err != nil {
		t.Fatal(err)
	}
	dl2.Body.Close()
	if dl2.StatusCode != http.StatusUnauthorized {
		t.Fatalf("download without token: got %d, want 401", dl2.StatusCode)
	}
}

// 主控端数量通知：被控端据此决定视频路由（多于一个主控端时统一走中继，
// 否则直连那一路会"独占"视频、其他主控端黑屏 —— 真机实测缺陷）。
func TestRelayPushesViewerCountToAgent(t *testing.T) {
	_, _, server := newTestServer(t)

	agent := dialWS(t, server, "/ws?role=agent&id=multi-01")
	waitForAgent(t, server, "multi-01")

	first := dialWS(t, server, "/ws?role=viewer&target=multi-01")
	if count := readViewerCount(t, agent); count != 1 {
		t.Fatalf("第一个主控端接入后应推 count=1，实际 %d", count)
	}

	second := dialWS(t, server, "/ws?role=viewer&target=multi-01")
	if count := readViewerCount(t, agent); count != 2 {
		t.Fatalf("第二个主控端接入后应推 count=2，实际 %d", count)
	}

	_ = second.Close()
	if count := readViewerCount(t, agent); count != 1 {
		t.Fatalf("一个主控端断开后应推 count=1，实际 %d", count)
	}
	_ = first.Close()
}

// readViewerCount 读到下一条 viewers 通知（跳过其他控制通知与数据帧）。
func readViewerCount(t *testing.T, conn *websocket.Conn) int {
	t.Helper()
	deadline := time.Now().Add(testTimeout)
	for time.Now().Before(deadline) {
		_ = conn.SetReadDeadline(time.Now().Add(testTimeout))
		messageType, data, err := conn.ReadMessage()
		if err != nil {
			t.Fatalf("读 viewers 通知失败: %v", err)
		}
		if messageType != websocket.TextMessage {
			continue
		}
		var probe struct {
			Type  string `json:"type"`
			Count int    `json:"count"`
		}
		if err := json.Unmarshal(data, &probe); err != nil {
			continue
		}
		if probe.Type == "viewers" {
			return probe.Count
		}
	}
	t.Fatal("没等到 viewers 通知")
	return -1
}
