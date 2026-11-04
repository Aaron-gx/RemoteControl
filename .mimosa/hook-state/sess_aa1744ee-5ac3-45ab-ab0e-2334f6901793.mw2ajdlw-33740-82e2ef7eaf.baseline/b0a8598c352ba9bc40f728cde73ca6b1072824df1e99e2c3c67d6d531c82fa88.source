# -*- coding: utf-8 -*-
"""
发布热更新（命令行版，LicenseKeygen 之外的手动通道）：
  python scripts/_publish_update.py <zip路径> <版本号> [说明] [灰度%]
用 license/private.pem 签名（与上位机同一把私钥）。
"""
import base64
import hashlib
import io
import json
import sys
import time
import urllib.request
import uuid

from cryptography.hazmat.primitives.serialization import load_pem_private_key
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import hashes

# 中继地址与两个口令从 config\relay-token.txt 读（gitignore 已排除，绝不入版本库；2026-10-14 推 GitHub 前清密）
#   T = 中继连接口令（上传 API 鉴权）  A = 中继管理口令（/admin/update 推送热更）
# 格式：relay= / token= / admin=；环境变量 RC_RELAY/RC_TOKEN/RC_ADMIN 可覆盖
import os as _os
_rcfg = {"relay": "", "token": "", "admin": ""}
_rcfgPath = _os.path.join(_os.path.dirname(_os.path.abspath(__file__)),
                          "..", "config", "relay-token.txt")
try:
    for _raw in open(_rcfgPath, encoding="utf-8-sig"):
        _line = _raw.strip()
        if not _line or _line.startswith("#"):
            continue
        if "=" in _line:
            _k, _v = _line.split("=", 1)
            if _k.strip().lower() in _rcfg:
                _rcfg[_k.strip().lower()] = _v.strip()
        elif not _rcfg["token"]:
            _rcfg["token"] = _line   # 旧格式兼容：整行就是中继口令
except OSError:
    pass
RELAY = _os.environ.get("RC_RELAY") or _rcfg["relay"]   # 换服务器：set RC_RELAY=新IP:端口
T = _os.environ.get("RC_TOKEN") or _rcfg["token"]
A = _os.environ.get("RC_ADMIN") or _rcfg["admin"]
if not RELAY or not T or not A:
    raise SystemExit("缺少中继地址/口令：请在 config\\relay-token.txt 里填 relay=、token=、admin=（该文件不入 git），"
                     "或设 RC_RELAY / RC_TOKEN / RC_ADMIN 环境变量")


def b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def post(url, data, hdr=None):
    req = urllib.request.Request(url, data=data, headers=hdr or {}, method="POST")
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.status, r.read().decode("utf-8", "replace")


def der_to_p1363(der):
    # SEQUENCE{rINTEGER,sINTEGER} -> r||s 各 32 字节（与 C# IeeeP1363FixedFieldConcatenation 一致）
    def read_len(d, i):
        n = d[i]
        i += 1
        if n & 0x80:
            nn = n & 0x7F
            n = int.from_bytes(d[i:i + nn], "big")
            i += nn
        return n, i

    assert der[0] == 0x30
    _, i = read_len(der, 1)
    vals = []
    while i < len(der):
        assert der[i] == 0x02, hex(der[i])
        n, i = read_len(der, i + 1)
        vals.append(int.from_bytes(der[i:i + n], "big"))
        i += n
    r, s = vals
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def main():
    zip_path, ver = sys.argv[1], sys.argv[2]
    notes = sys.argv[3] if len(sys.argv) > 3 else "更新"
    rollout = int(sys.argv[4]) if len(sys.argv) > 4 else 100

    key = load_pem_private_key(open(r"license\private.pem", "rb").read(), password=None)
    pkg = open(zip_path, "rb").read()
    sha = hashlib.sha256(pkg).hexdigest()
    payload = {"v": 1, "kind": "agent", "ver": ver, "sha256": sha, "size": len(pkg),
               "notes": notes, "ts": int(time.time())}
    body = json.dumps(payload, separators=(",", ":")).encode()
    sig = der_to_p1363(key.sign(f"RCU1.{b64url(body)}".encode(), ec.ECDSA(hashes.SHA256())))
    code = f"RCU1.{b64url(body)}.{b64url(sig)}"
    manifest = {"agent": {"code": code, "packageUrl": f"/api/update/file/agent-{ver}.zip"},
                "rolloutPercent": rollout}

    boundary = uuid.uuid4().hex
    bio = io.BytesIO()
    bio.write(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="agent-{ver}.zip"\r\nContent-Type: application/zip\r\n\r\n'.encode())
    bio.write(pkg)
    bio.write(f"\r\n--{boundary}--\r\n".encode())
    s1, r1 = post(f"http://{RELAY}/admin/update/package?token={A}", bio.getvalue(),
                  {"Content-Type": f"multipart/form-data; boundary={boundary}"})
    print("package :", s1, r1)
    if s1 != 200 or json.loads(r1)["sha256"] != sha:
        print("!! 服务器侧哈希不一致，中止")
        return
    s2, r2 = post(f"http://{RELAY}/admin/update/manifest?token={A}", json.dumps(manifest).encode(),
                  {"Content-Type": "application/json"})
    print("manifest:", s2, r2)
    if s2 == 200:
        print(f"已发布 v{ver}（灰度 {rollout}%）")


if __name__ == "__main__":
    main()
