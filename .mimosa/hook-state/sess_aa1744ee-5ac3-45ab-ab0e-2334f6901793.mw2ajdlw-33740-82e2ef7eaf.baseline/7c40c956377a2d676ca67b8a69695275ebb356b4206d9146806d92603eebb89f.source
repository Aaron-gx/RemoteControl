#!/usr/bin/env bash
# =============================================================================
#  远程控制软件 · 中继服务器一键部署（在服务器上以 root 运行）
#
#  用法：
#     sudo bash deploy-relay.sh                                  # 自动生成令牌
#     sudo bash deploy-relay.sh --token 'MySecret'               # 指定令牌
#     sudo bash deploy-relay.sh --admin-token 'AdminSecret'      # 指定管理令牌（/admin 用）
#     sudo bash deploy-relay.sh --port 8080 --token 'MySecret'
#     sudo bash deploy-relay.sh --binary /tmp/rcserver-linux-amd64
#
#  做的事：装二进制 → 建 systemd 服务（令牌写在服务文件里，不出现在进程命令行）
#          → 生成 rcctl 管理工具 → 开机自启 → 放行端口 → 健康检查
#          → 打印「部署ID / 令牌 / 两端配置片段 / 授权（激活码）用法」
# =============================================================================
set -euo pipefail

PORT=8080
TOKEN=""
ADMIN_TOKEN=""
BINARY="/tmp/rcserver-linux-amd64"
INSTALL_DIR="/opt/rcserver"
DATA_DIR="/var/lib/rcserver"
SERVICE="rcserver"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --port)    PORT="$2"; shift 2 ;;
        --token)   TOKEN="$2"; shift 2 ;;
        --admin-token) ADMIN_TOKEN="$2"; shift 2 ;;
        --binary)  BINARY="$2"; shift 2 ;;
        --dir)     INSTALL_DIR="$2"; shift 2 ;;
        --data)    DATA_DIR="$2"; shift 2 ;;
        --service) SERVICE="$2"; shift 2 ;;
        *) echo "未知参数: $1"; exit 1 ;;
    esac
done

if [[ $EUID -ne 0 ]]; then
    echo "请用 root 运行（sudo bash $0 $*）"; exit 1
fi

if [[ -z "$TOKEN" ]]; then
    TOKEN="$(head -c 24 /dev/urandom | base64 | tr -d '=+/' | cut -c1-32)"
    echo "[i] 未指定令牌，已随机生成"
fi
if [[ -z "$ADMIN_TOKEN" ]]; then
    ADMIN_TOKEN="$(head -c 24 /dev/urandom | base64 | tr -d '=+/' | cut -c1-32)"
    echo "[i] 未指定管理令牌，已随机生成（/admin 只认它，与 RC_TOKEN 分开保管）"
fi
if [[ "$ADMIN_TOKEN" == "$TOKEN" ]]; then
    echo "管理令牌不能与中继令牌相同（中继令牌已随主控端分发包发给客户）"; exit 1
fi

echo "===== 1. 安装二进制 ====="
if [[ ! -f "$BINARY" ]]; then
    echo "找不到二进制 $BINARY（先用 scp 传上来，或用 --binary 指定路径）"; exit 1
fi
mkdir -p "$INSTALL_DIR" "$DATA_DIR"
systemctl stop "$SERVICE" 2>/dev/null || true
install -m 0755 "$BINARY" "$INSTALL_DIR/rcserver"
echo "  已安装: $INSTALL_DIR/rcserver  ($(stat -c%s "$INSTALL_DIR/rcserver") 字节)"

echo "===== 2. 写入 systemd 服务 ====="
cat > "/etc/systemd/system/${SERVICE}.service" <<SVCEOF
[Unit]
Description=RemoteControl Relay Server (WebSocket relay + file staging)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# 令牌走环境变量，不会出现在 ps 的进程命令行里
Environment=RC_TOKEN=${TOKEN}
Environment=RC_ADMIN_TOKEN=${ADMIN_TOKEN}
ExecStart=${INSTALL_DIR}/rcserver -addr :${PORT} -data ${DATA_DIR}
Restart=always
RestartSec=3
LimitNOFILE=65535
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
SVCEOF
chmod 600 "/etc/systemd/system/${SERVICE}.service"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"
sleep 2
echo "  服务状态: $(systemctl is-active $SERVICE)"

echo "===== 3. 生成 rcctl 管理工具 ====="
# 关键：CLI 必须与服务使用同一个数据目录，否则部署ID/授权会写到别处（实测踩过）
cat > "${INSTALL_DIR}/rcctl" <<'RCTLEOF'
#!/usr/bin/env bash
# 远程控制软件中继管理工具（数据目录与服务保持一致）
set -euo pipefail
BIN="__INSTALL_DIR__/rcserver"
DATA="__DATA_DIR__"
SVC="__SERVICE__"
cmd="${1:-status}"
shift || true
case "$cmd" in
  id)      "$BIN" -data "$DATA" -print-id ;;
  status)  "$BIN" -data "$DATA" -license-status ;;
  install) "$BIN" -data "$DATA" -license "$1" && systemctl restart "$SVC" && sleep 1 && "$BIN" -data "$DATA" -license-status ;;
  restart) systemctl restart "$SVC" && sleep 1 && systemctl is-active "$SVC" ;;
  logs)    journalctl -u "$SVC" -n "${1:-80}" --no-pager ;;
  info)    echo "部署ID: $("$BIN" -data "$DATA" -print-id)"; "$BIN" -data "$DATA" -license-status || true ;;
  *)       echo "用法: rcctl {id|status|install <激活码>|restart|logs [行数]|info}"; exit 1 ;;
esac
RCTLEOF
sed -i "s|__INSTALL_DIR__|${INSTALL_DIR}|g; s|__DATA_DIR__|${DATA_DIR}|g; s|__SERVICE__|${SERVICE}|g" "${INSTALL_DIR}/rcctl"
chmod +x "${INSTALL_DIR}/rcctl"
echo "  已生成: ${INSTALL_DIR}/rcctl"

echo "===== 4. 放行端口 ====="
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q active; then
    ufw allow "${PORT}/tcp" >/dev/null && echo "  ufw: 已放行 ${PORT}/tcp"
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${PORT}/tcp" >/dev/null && firewall-cmd --reload >/dev/null && echo "  firewalld: 已放行"
elif command -v iptables >/dev/null 2>&1; then
    iptables -C INPUT -p tcp --dport "${PORT}" -j ACCEPT 2>/dev/null || \
        iptables -I INPUT -p tcp --dport "${PORT}" -j ACCEPT && echo "  iptables: 已放行（重启后失效，建议用 ufw/firewalld 持久化）"
else
    echo "  未检测到防火墙工具，请自行放行 ${PORT}/tcp"
fi
echo "  ⚠ 云服务器还需在控制台「安全组」放行 ${PORT}/tcp"

echo "===== 5. 健康检查 ====="
sleep 1
if curl -fsS "http://127.0.0.1:${PORT}/healthz" >/dev/null 2>&1; then
    echo "  /healthz 正常"
else
    echo "  /healthz 失败，请查 journalctl -u ${SERVICE} -n 50"
fi
LIC_STATE="$("${INSTALL_DIR}/rcserver" -data "${DATA_DIR}" -license-status 2>&1 | sed -n 's/^授权状态 *: *//p' || true)"
DEPLOY_ID="$("${INSTALL_DIR}/rcserver" -data "${DATA_DIR}" -print-id)"
PUBLIC_IP="$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || echo '<你的服务器公网IP>')"

cat <<INFOEOF

============================================================
 部署完成
------------------------------------------------------------
 监听       : 0.0.0.0:${PORT}
 公网地址   : ${PUBLIC_IP}:${PORT}
 共享令牌   : ${TOKEN}
 部署ID     : ${DEPLOY_ID}
 授权状态   : ${LIC_STATE:-未安装激活码}
 服务名     : ${SERVICE}
 管理工具   : ${INSTALL_DIR}/rcctl
 数据目录   : ${DATA_DIR}
 日志       : journalctl -u ${SERVICE} -f
------------------------------------------------------------
 接下来：把下面三行填到「被控端」agent.json 与「主控端」viewer.json
   "ServerUrl":     "ws://${PUBLIC_IP}:${PORT}/ws"
   "FileServerUrl": "http://${PUBLIC_IP}:${PORT}"
   "AgentToken":    "${TOKEN}"
------------------------------------------------------------
 授权（时间限制；未激活时中继拒绝一切通信，/healthz 除外）：
   1) 取部署ID : ${INSTALL_DIR}/rcctl id        → ${DEPLOY_ID}
   2) 在本机上位机 LicenseKeygen.exe 填该部署ID签发激活码
   3) 安装     : ${INSTALL_DIR}/rcctl install "<激活码>"
   4) 查看     : ${INSTALL_DIR}/rcctl status
============================================================
INFOEOF
