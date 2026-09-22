"""SSH 助手：用 paramiko 免交互登录香港服务器（密码认证 → 装公钥 → 后续免密）
用法:
  python hk.py exec "命令"
  python hk.py put <本地文件> <远端路径>
  python hk.py get <远端文件> <本地路径>
  python hk.py install-key            # 装本机公钥，之后可免密
  python hk.py status                 # 系统信息
密码从 E:\\tools\\hk-pass.txt 读取（不写进仓库）。
"""
import io
import os
import sys
import socket
import stat

CRLF = chr(13) + chr(10)

import paramiko

HOST = "202.60.232.209"
USER = "root"
PORT = 22
PASS_FILE = r"E:\tools\hk-pass.txt"
KEY_FILE = r"E:\tools\hk_ed25519"


def password():
    if os.path.exists(PASS_FILE):
        return io.open(PASS_FILE, encoding="utf-8").read().strip()
    return os.environ.get("HK_PASS", "")


def _proxy_sock():
    """经本机 Clash 的 HTTP 代理建隧道（本机直连该 IP 会被拦，走代理才通）。
    set HK_DIRECT=1 可强制直连。"""
    if os.environ.get("HK_DIRECT"):
        return None
    proxy = os.environ.get("HK_PROXY", "http://127.0.0.1:7897")
    if not proxy:
        return None
    from urllib.parse import urlparse
    u = urlparse(proxy)
    s = socket.create_connection((u.hostname, u.port), timeout=20)
    head = "CONNECT %s:%d HTTP/1.1" % (HOST, PORT) + CRLF + "Host: %s:%d" % (HOST, PORT) + CRLF + CRLF
    s.sendall(head.encode())
    buf = b""
    sep = (CRLF + CRLF).encode()
    while sep not in buf:
        chunk = s.recv(256)
        if not chunk:
            raise RuntimeError("proxy CONNECT no response")
        buf += chunk
    head_part, _, leftover = buf.partition(sep)
    first = head_part.split(CRLF.encode())[0].decode("latin-1")
    if " 200 " not in first:
        raise RuntimeError("proxy CONNECT failed: " + first)
    # 代理可能把 CONNECT 应答和服务端的第一段数据（SSH banner）放在同一个包里，
    # 多读出来的这些字节必须还给上层，否则 SSH 会报 "Error reading SSH protocol banner"。
    if leftover:
        return _PrefixedSock(s, leftover)
    return s


class _PrefixedSock:
    """把 CONNECT 应答里夹带的后续字节补给上层，其余操作原样透传给真 socket。"""

    def __init__(self, sock, leftover: bytes):
        self._sock = sock
        self._left = leftover

    def recv(self, n, *args, **kwargs):
        if self._left:
            data, self._left = self._left[:n], self._left[n:]
            return data
        return self._sock.recv(n, *args, **kwargs)

    def __getattr__(self, name):
        return getattr(self._sock, name)


def connect():
    """优先密钥；没有则用密码（并在首次成功后安装密钥）。自动经代理建隧道。"""
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    sock = _proxy_sock()
    if os.path.exists(KEY_FILE):
        try:
            c.connect(HOST, port=PORT, username=USER, key_filename=KEY_FILE, timeout=25, sock=sock)
            return c
        except Exception as e:
            print(f"[i] 密钥登录失败（{e}），改用密码")
            try:
                sock.close()
            except Exception:
                pass
            sock = _proxy_sock()
    c.connect(HOST, port=PORT, username=USER, password=password(), timeout=25, sock=sock)
    return c


def ensure_key(c):
    """把本机公钥装到服务器（幂等），之后免密"""
    if not os.path.exists(KEY_FILE):
        k = paramiko.Ed25519Key.generate() if hasattr(paramiko.Ed25519Key, "generate") else None
        if k is None:
            from cryptography.hazmat.primitives.asymmetric import ed25519
            from cryptography.hazmat.primitives import serialization
            priv = ed25519.Ed25519PrivateKey.generate()
            data = priv.private_bytes(
                encoding=serialization.Encoding.PEM,
                format=serialization.PrivateFormat.OpenSSH,
                encryption_algorithm=serialization.NoEncryption())
            io.open(KEY_FILE, "wb").write(data)
            pub = priv.public_key().public_bytes(
                encoding=serialization.Encoding.OpenSSH,
                format=serialization.PublicFormat.OpenSSH)
            io.open(KEY_FILE + ".pub", "wb").write(pub)
            try:
                os.chmod(KEY_FILE, stat.S_IRUSR | stat.S_IWUSR)
            except Exception:
                pass
        else:
            k.write_private_key_file(KEY_FILE)
            io.open(KEY_FILE + ".pub", "w").write(f"{k.get_name()} {k.get_base64()}")
    pub = io.open(KEY_FILE + ".pub", encoding="utf-8").read().strip()
    cmd = ("mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && "
           "chmod 600 ~/.ssh/authorized_keys && "
           f"grep -qF '{pub}' ~/.ssh/authorized_keys || echo '{pub}' >> ~/.ssh/authorized_keys; "
           "echo KEY_INSTALLED")
    out = run(c, cmd)
    print(out.strip().splitlines()[-1] if out.strip() else out)


def run(c, cmd, timeout=600):
    stdin, stdout, stderr = c.exec_command(cmd, timeout=timeout, get_pty=False)
    out = stdout.read().decode("utf-8", "replace")
    err = stderr.read().decode("utf-8", "replace")
    code = stdout.channel.recv_exit_status()
    if err.strip():
        out += ("\n[stderr] " + err) if out else err
    if code != 0:
        out += f"\n[exit={code}]"
    return out



def put_file(c, local, remote, timeout=900):
    """用 base64 经 exec 通道上传（不依赖 SFTP 子系统）"""
    data = io.open(local, "rb").read()
    import base64
    b64 = base64.b64encode(data).decode("ascii")
    cmd = "mkdir -p $(dirname %s) && base64 -d > %s && ls -la %s" % (sh(remote), sh(remote), sh(remote))
    stdin, stdout, stderr = c.exec_command(cmd, timeout=timeout)
    # 分块写入，避免一次性塞爆通道
    for i in range(0, len(b64), 65536):
        stdin.write(b64[i:i + 65536])
    stdin.channel.shutdown_write()
    out = stdout.read().decode("utf-8", "replace")
    err = stderr.read().decode("utf-8", "replace")
    code = stdout.channel.recv_exit_status()
    print(out.strip() or err.strip())
    if code != 0:
        raise RuntimeError("upload failed: " + err.strip())
    print("uploaded %s (%d bytes) -> %s" % (local, len(data), remote))


def get_file(c, remote, local, timeout=900):
    import base64
    out = run(c, "base64 -w0 " + sh(remote), timeout=timeout)
    data = base64.b64decode("".join(out.split()))
    io.open(local, "wb").write(data)
    print("downloaded %s (%d bytes) -> %s" % (remote, len(data), local))


def sh(s):
    return "'" + s.replace("'", "'\''") + "'"


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    cmd = sys.argv[1]
    c = connect()
    try:
        if cmd == "exec":
            print(run(c, sys.argv[2]))
        elif cmd == "cmd":
            # 从标准输入读命令，避免引号地狱
            print(run(c, io.open(sys.argv[2], encoding="utf-8").read()))
        elif cmd == "put":
            put_file(c, sys.argv[2], sys.argv[3])
        elif cmd == "get":
            get_file(c, sys.argv[2], sys.argv[3])
        elif cmd == "install-key":
            ensure_key(c)
        elif cmd == "status":
            print(run(c, "uname -a; nproc; free -m | head -2; df -h / | tail -1; "
                         "cat /etc/os-release | head -2; ss -lntp | head -10"))
        else:
            print("未知命令")
            return 1
    finally:
        c.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
