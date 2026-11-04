package main

// relay.go implements the websocket relay that sits between the agents
// (controlled machines) and the viewers.
//
// Endpoints:
//
//	GET /ws?role=agent&id=AGENT_ID     agent (controlled machine) side
//	GET /ws?role=viewer&target=AGENT_ID viewer side
//	GET /api/agents                    online agent list, for viewer UI state
//
// Frames are never parsed: whatever one side sends is written to the other
// side with the very same websocket frame type.
//
// Pairing rules:
//
//   - an agent id maps to exactly one live agent connection; connecting again
//     with the same id replaces the previous connection (1011, "replaced by a
//     newer agent connection");
//   - one agent serves any number of viewers at once, each with its own
//     independent pairing;
//   - a viewer that goes away only drops its own pairing - the agent keeps its
//     connection and stays registered;
//   - an agent that goes away closes every viewer it serves (1011, "agent
//     disconnected").

import (
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	// defaultPingPeriod is how often the server pings an idle peer.
	defaultPingPeriod = 30 * time.Second
	// defaultPongWait is how long a peer may stay silent (no pong, no frame)
	// before the connection is considered dead and torn down. It must be
	// greater than the ping period.
	defaultPongWait = 60 * time.Second
	// writeWait bounds a single write to a peer.
	writeWait = 10 * time.Second
	// closeCodeRelay is the websocket close code used whenever the relay
	// takes a connection down itself (1011 = internal server error).
	closeCodeRelay = websocket.CloseInternalServerErr

	// Close reasons, so a client can tell why the relay hung up on it.
	// closeReasonAgentGone is what a viewer sees when its agent drops.
	closeReasonAgentGone = "agent disconnected"
	// closeReasonReplaced is what an agent sees when the same id reconnects.
	closeReasonReplaced = "replaced by a newer agent connection"
	// closeReasonOffline is what a viewer sees when its target is not online.
	closeReasonOffline = "agent offline"
	// closeReasonViewerGone is what a viewer sees when it is torn down itself.
	closeReasonViewerGone = "viewer disconnected"
)

// Heartbeat is the keepalive policy applied to every relayed connection.
type Heartbeat struct {
	// PingPeriod is how often a ping frame is sent to an idle peer.
	PingPeriod time.Duration
	// PongWait is how long the peer may stay silent before it is dropped.
	PongWait time.Duration
}

// DefaultHeartbeat is the documented policy: ping every 30s, drop a peer that
// has said nothing for 60s.
var DefaultHeartbeat = Heartbeat{PingPeriod: defaultPingPeriod, PongWait: defaultPongWait}

// upgrader turns the HTTP request into a websocket connection. The relay is
// meant to be reachable from browsers and native clients alike, so the origin
// check is deliberately permissive.
var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	CheckOrigin:     func(*http.Request) bool { return true },
}

// wsConn serialises the writes of one websocket connection. gorilla allows a
// single concurrent writer, and relayed frames can otherwise race with a ping
// or with a close frame.
//
// 并发契约（依赖 gorilla 的实现保证，勿破坏）：
//   - 读方向（ReadMessage / SetReadDeadline / PongHandler）由**单一 reader**
//     独占：serveAgent / serveViewer 各自在一个 goroutine 里循环 ReadMessage，
//     心跳只在写方向做，绝不会并发读同一个 conn；
//   - 写方向（WriteMessage / WriteControl）全部经下面的 mu 串行化，保证
//     同一时刻至多一个 goroutine 在写，避免 gorilla 的并发写 panic；
//   - Close 与正在进行的读写并发安全：Close 会让阻塞中的 ReadMessage
//     立刻返回错误，写会因连接关闭而失败——调用方据此退出循环并 shutdown。
//
// 关于帧序：写原子性由 mu 保证，但"控制帧（授权状态/主控端数量）相对数据帧
// 的位置"取决于各 goroutine 抢到锁的顺序，无法跨调用者强排序。控制帧都是
// 独立的、带 type 字段的 JSON 文本帧，与二进制数据帧互不干扰，客户端按
// type 区分即可，因此不依赖严格帧序。
type wsConn struct {
	conn *websocket.Conn
	mu   sync.Mutex
}

func (c *wsConn) writeMessage(messageType int, data []byte) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if err := c.conn.SetWriteDeadline(time.Now().Add(writeWait)); err != nil {
		return err
	}
	return c.conn.WriteMessage(messageType, data)
}

func (c *wsConn) writeText(text string) error {
	return c.writeMessage(websocket.TextMessage, []byte(text))
}

func (c *wsConn) writeClose(code int, reason string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn.WriteControl(websocket.CloseMessage,
		websocket.FormatCloseMessage(code, reason), time.Now().Add(writeWait))
}

func (c *wsConn) writePing() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn.WriteControl(websocket.PingMessage, nil, time.Now().Add(writeWait))
}

func (c *wsConn) close() {
	_ = c.conn.Close()
}

// configureConn applies the keepalive policy: every pong pushes the read
// deadline forward, so a peer that stops answering the pings is dropped after
// the pong wait.
func configureConn(conn *websocket.Conn, pongWait time.Duration) {
	_ = conn.SetReadDeadline(time.Now().Add(pongWait))
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(pongWait))
	})
}

// keepAlive pings the peer until done is closed or a ping fails.
func keepAlive(c *wsConn, done <-chan struct{}, pingPeriod time.Duration) {
	ticker := time.NewTicker(pingPeriod)
	defer ticker.Stop()
	for {
		select {
		case <-done:
			return
		case <-ticker.C:
			if err := c.writePing(); err != nil {
				return
			}
		}
	}
}

// agentConn is one registered agent (controlled machine).
type agentConn struct {
	id          string
	ws          *wsConn
	connectedAt time.Time
	done        chan struct{}

	closeOnce sync.Once
	mu        sync.Mutex
	viewers   map[*viewerConn]struct{}
	closed    bool
}

func newAgentConn(id string, conn *websocket.Conn, heartbeat Heartbeat) *agentConn {
	configureConn(conn, heartbeat.PongWait)
	return &agentConn{
		id:          id,
		ws:          &wsConn{conn: conn},
		connectedAt: time.Now(),
		done:        make(chan struct{}),
		viewers:     make(map[*viewerConn]struct{}),
	}
}

// addViewer attaches a viewer. It reports false when the agent is already gone,
// in which case the caller must treat the agent as offline.
func (a *agentConn) addViewer(v *viewerConn) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.closed {
		return false
	}
	a.viewers[v] = struct{}{}
	return true
}

// removeViewer detaches a viewer and returns how many are left.
func (a *agentConn) removeViewer(v *viewerConn) int {
	a.mu.Lock()
	defer a.mu.Unlock()
	delete(a.viewers, v)
	return len(a.viewers)
}

func (a *agentConn) viewerCount() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return len(a.viewers)
}

func (a *agentConn) isClosed() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.closed
}

// broadcast forwards one frame read from the agent to every paired viewer.
func (a *agentConn) broadcast(messageType int, data []byte) {
	a.mu.Lock()
	viewers := make([]*viewerConn, 0, len(a.viewers))
	for v := range a.viewers {
		viewers = append(viewers, v)
	}
	a.mu.Unlock()

	for _, v := range viewers {
		if err := v.ws.writeMessage(messageType, data); err != nil {
			v.shutdown(closeCodeRelay, "agent write failed")
		}
	}
}

// shutdown closes the agent socket and takes every paired viewer down with it.
// It is idempotent: the first caller wins, later ones are no-ops.
func (a *agentConn) shutdown(code int, reason string) {
	a.closeOnce.Do(func() {
		a.mu.Lock()
		a.closed = true
		viewers := make([]*viewerConn, 0, len(a.viewers))
		for v := range a.viewers {
			viewers = append(viewers, v)
		}
		a.viewers = make(map[*viewerConn]struct{})
		a.mu.Unlock()

		close(a.done)
		_ = a.ws.writeClose(code, reason)
		a.ws.close()
		for _, v := range viewers {
			// The viewer gets a distinct code and reason, so a client can tell
			// "the agent I was watching went away" from "I was dropped".
			v.shutdown(closeCodeRelay, closeReasonAgentGone)
		}
	})
}

// viewerConn is one viewer paired with an agent.
type viewerConn struct {
	target string
	ws     *wsConn
	done   chan struct{}
	// agent is written once, before the relay goroutines start.
	agent *agentConn

	closeOnce sync.Once
}

func newViewerConn(target string, conn *websocket.Conn, heartbeat Heartbeat) *viewerConn {
	configureConn(conn, heartbeat.PongWait)
	return &viewerConn{
		target: target,
		ws:     &wsConn{conn: conn},
		done:   make(chan struct{}),
	}
}

func (v *viewerConn) shutdown(code int, reason string) {
	v.closeOnce.Do(func() {
		close(v.done)
		_ = v.ws.writeClose(code, reason)
		v.ws.close()
	})
}

// Hub owns the agent registry. All map access goes through its mutex.
type Hub struct {
	mu        sync.Mutex
	agents    map[string]*agentConn
	logger    *log.Logger
	heartbeat Heartbeat
	// License 非空时，配对成功后会向双方推送当前授权状态（供客户端二次校验）。
	License *LicenseManager

	// Token, when non-empty, is required on every /ws and /api/agents request.
	// It keeps a publicly reachable relay from being usable by strangers
	// (the agent id alone is not a secret). Empty means "no auth" (dev/loopback).
	Token string

	// AdminToken, when non-empty, is the only token accepted on /admin*.
	// It must be a DIFFERENT secret from Token: the relay token ships inside
	// every viewer/agent package (viewer-defaults.txt / agent.json), so every
	// customer already holds it — it must never open the license admin page.
	// Empty means "relay token accepted from loopback only" (SSH rescue path).
	AdminToken string
}

// NewHub builds a hub with the documented keepalive policy.
func NewHub(logger *log.Logger) *Hub {
	return NewHubWithHeartbeat(DefaultHeartbeat, logger)
}

// NewHubWithHeartbeat builds a hub with an explicit keepalive policy.
func NewHubWithHeartbeat(heartbeat Heartbeat, logger *log.Logger) *Hub {
	if logger == nil {
		logger = log.New(io.Discard, "", 0)
	}
	if heartbeat.PingPeriod <= 0 {
		heartbeat.PingPeriod = DefaultHeartbeat.PingPeriod
	}
	if heartbeat.PongWait <= 0 {
		heartbeat.PongWait = DefaultHeartbeat.PongWait
	}
	return &Hub{
		agents:    make(map[string]*agentConn),
		logger:    logger,
		heartbeat: heartbeat,
	}
}

// Register wires the relay endpoints onto mux.
func (h *Hub) Register(mux *http.ServeMux) {
	mux.HandleFunc("/ws", h.handleWS)
	mux.HandleFunc("GET /api/agents", h.handleListAgents)
}

// CloseAll 断开所有已建立的连接（授权失效时调用，让"到期"即时生效）。
func (h *Hub) CloseAll(reason string) {
	h.mu.Lock()
	agents := make([]*agentConn, 0, len(h.agents))
	for _, a := range h.agents {
		agents = append(agents, a)
	}
	h.mu.Unlock()
	for _, a := range agents {
		a.shutdown(websocket.ClosePolicyViolation, reason)
	}
}

// pushLicense 把授权状态推给一方（含原始激活码，客户端会用同一把公钥再验一次，
// 这样"只改中继二进制"也不足以绕过授权）。
func (h *Hub) pushLicense(c *wsConn) {
	if h.License == nil {
		return
	}
	msg := h.License.StatusMessage()
	data, err := json.Marshal(msg)
	if err != nil {
		return
	}
	_ = c.writeText(string(data))
}

// PushLicenseAll 把"当前授权已不可用"广播给所有在线客户端（被控端 + 主控端）。
//
// 为什么要有它：授权在运行中被撤销/到期时，只 close 连接是不够的。被控端断开后
// 会退避重连，可重连会因为授权不 ok 被握手拒绝（HTTP 403）—— 而那条路径上它收不到
// 任何授权消息，于是**继续拿着本地缓存里的旧码跑到期**（还能走 P2P 直连）。
// 所以这里主动把 state（不带激活码原文）推给它，客户端看到非法 state 就会本地停机。
func (h *Hub) PushLicenseAll() {
	if h.License == nil {
		return
	}
	data, err := json.Marshal(h.License.StatusMessageNoCode())
	if err != nil {
		return
	}
	h.mu.Lock()
	agents := make([]*agentConn, 0, len(h.agents))
	for _, a := range h.agents {
		agents = append(agents, a)
	}
	h.mu.Unlock()

	text := string(data)
	for _, a := range agents {
		_ = a.ws.writeText(text)
		a.mu.Lock()
		viewers := make([]*viewerConn, 0, len(a.viewers))
		for v := range a.viewers {
			viewers = append(viewers, v)
		}
		a.mu.Unlock()
		for _, v := range viewers {
			_ = v.ws.writeText(text)
		}
	}
}

// AgentInfo is one entry of GET /api/agents.
type AgentInfo struct {
	ID          string `json:"id"`
	Viewers     int    `json:"viewers"`
	ConnectedAt string `json:"connectedAt"`
}

type agentsResponse struct {
	Agents []AgentInfo `json:"agents"`
}

// wsError is the message a viewer receives when its target is not online.
type wsError struct {
	Type    string `json:"type"`
	Code    string `json:"code"`
	AgentID string `json:"agentId"`
}

var errAgentOffline = errors.New("agent offline")

func (h *Hub) handleWS(w http.ResponseWriter, r *http.Request) {
	query := r.URL.Query()
	if !Authorized(h.Token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	switch query.Get("role") {
	case "agent":
		h.handleAgent(w, r, strings.TrimSpace(query.Get("id")))
	case "viewer":
		h.handleViewer(w, r, strings.TrimSpace(query.Get("target")))
	default:
		writeJSONError(w, http.StatusBadRequest, "invalid_role", `role must be "agent" or "viewer"`)
	}
}

func (h *Hub) handleAgent(w http.ResponseWriter, r *http.Request, id string) {
	if id == "" {
		writeJSONError(w, http.StatusBadRequest, "missing_id", "an agent connection requires an id")
		return
	}

	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		// Upgrade already answered the request.
		h.logger.Printf("relay: agent %q upgrade failed: %v", id, err)
		return
	}

	agent := newAgentConn(id, conn, h.heartbeat)
	h.registerAgent(agent, r.RemoteAddr)
	h.logger.Printf("relay: agent %q connected (remote=%s)", id, r.RemoteAddr)

	go keepAlive(agent.ws, agent.done, h.heartbeat.PingPeriod)
	h.pushLicense(agent.ws)
	go h.serveAgent(agent)
}

func (h *Hub) handleViewer(w http.ResponseWriter, r *http.Request, target string) {
	if target == "" {
		writeJSONError(w, http.StatusBadRequest, "missing_target", "a viewer connection requires a target")
		return
	}

	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		h.logger.Printf("relay: viewer upgrade failed: %v", err)
		return
	}

	viewer := newViewerConn(target, conn, h.heartbeat)
	agent, err := h.pairViewer(viewer)
	if err != nil {
		h.logger.Printf("relay: viewer rejected: agent %q is offline (remote=%s)", target, r.RemoteAddr)
		// 先同步发出离线通知帧，再走 shutdown 收尾关闭连接：
		// 不要在这里直接 conn.Close() —— 那会绕过 wsConn.mu，且可能与
		// writeClose 的写入相撞，还可能把刚发出的离线通知掐掉。
		_ = viewer.ws.writeText(offlineMessage(target))
		viewer.shutdown(closeCodeRelay, closeReasonOffline)
		return
	}

	viewer.agent = agent
	h.logger.Printf("relay: viewer paired with agent %q (viewers=%d, remote=%s)",
		agent.id, agent.viewerCount(), r.RemoteAddr)

	h.pushViewerCount(agent)
	h.pushLicense(viewer.ws)
	go keepAlive(viewer.ws, viewer.done, h.heartbeat.PingPeriod)
	go h.serveViewer(viewer)
}

// registerAgent publishes agent as the current connection for its id. A second
// connection for the same id replaces the previous one, which is closed (1011)
// together with the viewers it had paired.
func (h *Hub) registerAgent(agent *agentConn, remote string) {
	h.mu.Lock()
	previous := h.agents[agent.id]
	h.agents[agent.id] = agent
	h.mu.Unlock()

	if previous == nil || previous == agent {
		return
	}
	h.logger.Printf("relay: agent %q reconnected from %s, replacing the previous connection", agent.id, remote)
	previous.shutdown(closeCodeRelay, closeReasonReplaced)
}

// unregisterAgent drops agent from the registry, but only while it is still
// the current connection for that id: a reconnect must not remove the
// replacement. The return value reports whether the entry was removed.
func (h *Hub) unregisterAgent(agent *agentConn) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if current, ok := h.agents[agent.id]; !ok || current != agent {
		return false
	}
	delete(h.agents, agent.id)
	return true
}

func (h *Hub) lookupAgent(id string) *agentConn {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.agents[id]
}

// pairViewer attaches viewer to the agent named by its target. It returns
// errAgentOffline when no agent is registered for that target.
func (h *Hub) pairViewer(viewer *viewerConn) (*agentConn, error) {
	for attempt := 0; attempt < 4; attempt++ {
		agent := h.lookupAgent(viewer.target)
		if agent == nil {
			return nil, errAgentOffline
		}
		if agent.addViewer(viewer) {
			return agent, nil
		}
		// The agent vanished between the lookup and the attach: it is
		// reconnecting, so give its replacement a moment to register before
		// falling back to the offline error.
		time.Sleep(10 * time.Millisecond)
	}
	return nil, errAgentOffline
}

// serveAgent pumps frames from the agent to its viewers until the agent goes
// away, then tears everything down.
func (h *Hub) serveAgent(agent *agentConn) {
	for {
		messageType, data, err := agent.ws.conn.ReadMessage()
		if err != nil {
			break
		}
		agent.broadcast(messageType, data)
	}

	if h.unregisterAgent(agent) {
		h.logger.Printf("relay: agent %q disconnected", agent.id)
	}
	agent.shutdown(closeCodeRelay, closeReasonAgentGone)
}

// serveViewer pumps frames from the viewer to its agent until one of the two
// sides goes away.
//
// A viewer leaving only ends its own pairing: the agent keeps its websocket and
// stays registered, ready for the next viewer. An agent leaving takes every
// viewer it serves down with it (1011, reason closeReasonAgentGone).
func (h *Hub) serveViewer(viewer *viewerConn) {
	agent := viewer.agent
	for {
		messageType, data, err := viewer.ws.conn.ReadMessage()
		if err != nil {
			remaining := agent.removeViewer(viewer)
			if agent.isClosed() {
				h.logger.Printf("relay: viewer of agent %q closed (agent is gone)", agent.id)
			} else {
				h.logger.Printf("relay: viewer of agent %q disconnected (agent still online, viewers left: %d)",
					agent.id, remaining)
			}
			viewer.shutdown(closeCodeRelay, closeReasonViewerGone)
			h.pushViewerCount(agent)
			return
		}
		if err := agent.ws.writeMessage(messageType, data); err != nil {
			// The agent is gone; its own teardown closes every viewer.
			agent.shutdown(closeCodeRelay, closeReasonAgentGone)
			return
		}
	}
}

func offlineMessage(agentID string) string {
	payload, err := json.Marshal(wsError{Type: "error", Code: "agent_offline", AgentID: agentID})
	if err != nil {
		// json.Marshal cannot fail on plain strings; keep a usable fallback.
		return `{"type":"error","code":"agent_offline"}`
	}
	return string(payload)
}

// pushViewerCount 把当前配对的主控端数量推给被控端。
// 用途：被控端只有一路视频出口 —— 直连生效时它会**只**往直连发，
// 于是中继上的其他主控端就拿不到画面了。知道数量后，被控端可以在
// "多于一个主控端"时改用中继转发，保证每个主控端都有画面。
func (h *Hub) pushViewerCount(agent *agentConn) {
	if agent == nil || agent.isClosed() {
		return
	}
	count := agent.viewerCount()
	payload, err := json.Marshal(map[string]any{"type": "viewers", "count": count})
	if err != nil {
		return
	}
	if err := agent.ws.writeText(string(payload)); err != nil {
		h.logger.Printf("relay: push viewer count to agent %q failed: %v", agent.id, err)
	}
}

func (h *Hub) handleListAgents(w http.ResponseWriter, r *http.Request) {
	if !Authorized(h.Token, r) {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized", "invalid or missing token")
		return
	}
	writeJSON(w, http.StatusOK, agentsResponse{Agents: h.listAgents()})
}

// Logger 返回 hub 的日志器（授权管理页共用同一份日志）。
func (h *Hub) Logger() *log.Logger { return h.logger }

// Authorized reports whether r may use a relay protected by want.
// An empty want disables the check. The token may be carried as ?token=,
// as X-RC-Token, or as "Authorization: Bearer <token>" — the first form is what
// the WebSocket clients use (browsers/ClientWebSocket cannot set headers on the
// handshake easily), the header forms suit the HTTP file endpoints.
func Authorized(want string, r *http.Request) bool {
	if want == "" {
		return true
	}
	got := r.URL.Query().Get("token")
	if got == "" {
		got = r.Header.Get("X-RC-Token")
	}
	if got == "" {
		if auth := r.Header.Get("Authorization"); strings.HasPrefix(auth, "Bearer ") {
			got = strings.TrimSpace(strings.TrimPrefix(auth, "Bearer "))
		}
	}
	return subtle.ConstantTimeCompare([]byte(got), []byte(want)) == 1
}

func (h *Hub) listAgents() []AgentInfo {
	h.mu.Lock()
	agents := make([]*agentConn, 0, len(h.agents))
	for _, agent := range h.agents {
		agents = append(agents, agent)
	}
	h.mu.Unlock()

	out := make([]AgentInfo, 0, len(agents))
	for _, agent := range agents {
		out = append(out, AgentInfo{
			ID:          agent.id,
			Viewers:     agent.viewerCount(),
			ConnectedAt: agent.connectedAt.UTC().Format(time.RFC3339),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}
