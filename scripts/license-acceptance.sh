#!/usr/bin/env bash
# 授权机制验收（在香港服务器执行；只用 8081 测试实例 + /tmp 数据目录，不动正式实例）
# 用法: bash license-acceptance.sh <SHORT码> <错误部署绑定的码> <正式令牌> <正式部署ID>
set -uo pipefail
SHORT="${1:?需要短时激活码}"
WRONG="${2:?需要绑定错误部署的激活码}"
TOKEN="${3:-testtoken}"
DEPLOY_ID="${4:-}"

# 前置清理：确保没有残留的 8081 测试实例（否则会误判：请求打到旧实例上）
pkill -f "addr :8081" 2>/dev/null || true
sleep 1

RC=/opt/rcserver/rcserver
DATA=/var/lib/rcserver
TMP=/tmp/lict
TESTTOK=testtoken
PASS=0; FAIL=0
chk() { # chk 描述 实际 期望
  if [[ "$2" == "$3" ]]; then echo "  [PASS] $1 （$2）"; PASS=$((PASS+1));
  else echo "  [FAIL] $1 （实际 $2，期望 $3）"; FAIL=$((FAIL+1)); fi
}

echo "===== A. 篡改授权文件（把到期时间改大 10 年）必须被拒绝 ====="
cp "$DATA/license.json" /tmp/license.json.bak
python3 - <<'PY'
import base64, json, io
p = "/var/lib/rcserver/license.json"
rec = json.load(io.open(p))
parts = rec["code"].split(".")
b64d = lambda s: base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))
payload = json.loads(b64d(parts[1]))
old = payload["exp"]
payload["exp"] = old + 10 * 365 * 24 * 3600
new_body = base64.urlsafe_b64encode(json.dumps(payload, separators=(",", ":")).encode()).decode().rstrip("=")
rec["code"] = "RC1." + new_body + "." + parts[2]
io.open(p, "w").write(json.dumps(rec, indent=2))
print(f"  已把 exp 从 {old} 篡改为 {payload['exp']}（签名不变）")
PY
systemctl restart rcserver; sleep 2
STATE=$($RC -data $DATA -license-status 2>&1 | sed -n 's/^授权状态 *: *//p')
echo "  授权状态: $STATE"
CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8080/api/agents?token=$TOKEN")
chk "篡改后拒绝通信" "$CODE" "403"
[[ "$STATE" != "ok" ]] && { echo "  [PASS] 状态非 ok（$STATE）"; PASS=$((PASS+1)); } || { echo "  [FAIL] 状态仍为 ok"; FAIL=$((FAIL+1)); }
cp /tmp/license.json.bak "$DATA/license.json"; systemctl restart rcserver; sleep 2
chk "恢复原授权后放行" "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8080/api/agents?token=$TOKEN")" "200"

echo
echo "===== B. 绑定到别的部署的激活码必须装不上 ====="
rm -rf $TMP; mkdir -p $TMP
OUT=$($RC -data $TMP -license "$WRONG" 2>&1 || true)
echo "  $OUT" | head -2
if echo "$OUT" | grep -q "绑定"; then echo "  [PASS] 安装被拒（绑定不符）"; PASS=$((PASS+1));
else echo "  [FAIL] 竟然装上了绑定到别处的激活码"; FAIL=$((FAIL+1)); fi

echo
echo "===== C. 未激活的实例必须拒绝一切通信（8081 测试实例）====="
rm -rf $TMP; mkdir -p $TMP
setsid $RC -addr :8081 -data $TMP -token $TESTTOK >/tmp/lict-nolic.log 2>&1 &
sleep 2
if ! grep -q "listening on :8081" /tmp/lict-nolic.log 2>/dev/null; then
    echo "  [FAIL] 测试实例未起来（端口被占？）"; cat /tmp/lict-nolic.log | head -3
fi
chk "/healthz 仍可用" "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8081/healthz)" "200"
chk "/api/agents 被拒" "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8081/api/agents?token=$TESTTOK")" "403"
echo "  拒绝原因: $(curl -s "http://127.0.0.1:8081/api/agents?token=$TESTTOK")"
pkill -f "addr :8081" || true; sleep 1

echo
echo "===== D. 短时授权：有效期内放行，到期后自动停止 ====="
rm -rf $TMP; mkdir -p $TMP
pkill -f "addr :8081" 2>/dev/null || true; sleep 1
setsid $RC -addr :8081 -data $TMP -token $TESTTOK >/tmp/lict-short.log 2>&1 &
sleep 2
grep -q "listening on :8081" /tmp/lict-short.log || { echo "  [FAIL] 测试实例未起来"; head -3 /tmp/lict-short.log; }
$RC -data $TMP -license "$SHORT" >/tmp/lict-install.log 2>&1
sed -n '1,3p' /tmp/lict-install.log
pkill -f "addr :8081" || true; sleep 1
# 用同一份数据目录重启（授权已持久化）
setsid $RC -addr :8081 -data $TMP -token $TESTTOK >/tmp/lict-short2.log 2>&1 &
sleep 2
START=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8081/api/agents?token=$TESTTOK")
chk "有效期内放行" "$START" "200"
echo "  等待授权到期（轮询最多 300 秒）…"
EXPIRED_AT=""
for i in $(seq 1 60); do
    sleep 5
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8081/api/agents?token=$TESTTOK")" == "403" ]]; then
        EXPIRED_AT="$((i*5))"
        break
    fi
done
echo "  到期后在第 ${EXPIRED_AT:-未} 秒被拒绝"
chk "到期后拒绝通信" "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8081/api/agents?token=$TESTTOK")" "403"
echo "  到期后原因: $(curl -s "http://127.0.0.1:8081/api/agents?token=$TESTTOK")"
chk "到期后 /healthz 仍可用（便于排障）" "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8081/healthz)" "200"
pkill -f "addr :8081" || true; rm -rf $TMP

echo
echo "===== 结果：通过 $PASS，失败 $FAIL ====="
[[ $FAIL -eq 0 ]] || exit 1
