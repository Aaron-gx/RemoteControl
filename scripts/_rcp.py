# -*- coding: utf-8 -*-
"""
真实链路测试客户端（仅主控端侧，遵守"被控端不在本地跑"的规矩）：
直连生产中继，以 viewer 身份与在线被控端交互：查版本/开软件/抓剪贴板回传的日志。
用法：
  python scripts/_rcp.py status          # 连上后打印 WorkerStatus（含 AgentVersion）
  python scripts/_rcp.py run "<命令>"     # 在被控端执行 cmd 命令（通过 OpenSoftware）
  python scripts/_rcp.py thispc          # 打开此电脑
  python scripts/_rcp.py grablog         # 拉取 worker/coordinator 日志尾部（走剪贴板通道）
"""
import asyncio
import json
import struct
import sys

import aiohttp

RELAY = "__RELAY_HOST__:18080"
TOKEN = "__RELAY_TOKEN__"

import urllib.request
agents = json.loads(urllib.request.urlopen(
    urllib.request.Request(f"http://{RELAY}/api/agents", headers={"X-RC-Token": TOKEN}), timeout=8).read())
_want = __import__("os").environ.get("RC_AGENT", "")
_matched = [a["id"] for a in agents["agents"] if _want and _want in a["id"]]
AGENT = (_matched[0] if _matched else (agents["agents"][0]["id"] if agents["agents"] else None))
if not AGENT:
    print("no agent online"); sys.exit(1)

def frame(t: int, payload: bytes) -> bytes:
    return bytes([t]) + struct.pack(">I", len(payload)) + payload

async def session(cmd_args, seconds: float = 12.0):
    """连上被控端，保持 seconds 秒：期间执行 cmd_args 给出的动作，打印收到的所有关键消息"""
    async with aiohttp.ClientSession() as s:
        url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
        async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
            print(f"[connected -> {AGENT}]", flush=True)
            # 开场：请求软件列表（= 主控端上线信号）
            await ws.send_bytes(frame(0x15, b""))
            if cmd_args.get("open"):
                await asyncio.sleep(0.5)
                exe, args = cmd_args["open"]
                payload = json.dumps({"exePath": exe, "args": args}).encode("utf-8")
                await ws.send_bytes(frame(0x11, payload))
                print(f"[sent OPEN] {exe} {args}", flush=True)
            got_clip = None
            status = None
            try:
                end = asyncio.get_event_loop().time() + seconds
                while True:
                    left = end - asyncio.get_event_loop().time()
                    if left <= 0:
                        break
                    msg = await ws.receive(timeout=left)
                    if msg.type != aiohttp.WSMsgType.BINARY:
                        continue
                    data = msg.data
                    t, n = data[0], struct.unpack(">I", data[1:5])[0]
                    payload = data[5:5 + n]
                    if t == 0x30:   # 心跳回显
                        await ws.send_bytes(frame(0x30, payload))
                    elif t == 0x41:  # WorkerStatus
                        try:
                            status = json.loads(payload.decode("utf-8"))
                        except Exception:
                            pass
                    elif t == 0x20:  # ClipboardText（抓日志靠它）
                        got_clip = payload.decode("utf-8", "replace")
                        print("[CLIPBOARD-TEXT] " + "-" * 60, flush=True)
                        print(got_clip, flush=True)
                        print("-" * 70, flush=True)
                    elif t == 0x21:
                        print("[CLIPBOARD-FILE] " + payload.decode("utf-8", "replace"), flush=True)
                    elif t == 0x14:
                        print("[MONITOR] " + payload.decode("utf-8", "replace")[:200], flush=True)
                    elif t == 0x1F:
                        print("[ERROR] " + payload.decode("utf-8", "replace"), flush=True)
                    # 0x01 视频帧计数（不打印）
            except asyncio.TimeoutError:
                pass
            if status:
                keys = ["AgentVersion", "CaptureFps", "Backend", "BitrateKbps", "FrameRate", "InputPath",
                        "CursorContested", "CursorBlockerHint", "CursorArbiterArmed", "LocalInputIdleMs", "SecureDesktop", "UpdateState", "UpdateStaged", "HostName", "UserName"]
                print("[STATUS] " + json.dumps({k: status.get(k) for k in keys if k in status},
                                               ensure_ascii=False), flush=True)
            return status, got_clip

def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "status"
    if mode == "status":
        asyncio.run(session({}, 8))
    elif mode == "thispc":
        asyncio.run(session({"open": ("::this-pc", "")}, 15))
    elif mode == "run":
        cmd = sys.argv[2]
        asyncio.run(session({"open": ("C:\\Windows\\System32\\cmd.exe", f"/c {cmd}")}, 15))
    elif mode == "grablog":
        # 日志经剪贴板通道同步回来：先 worker 尾 150 行，2 秒后 coordinator 尾 80 行（两次 0x20 都能收到）
        ps = ("-NoProfile -Command \""
              "$p=(Get-Process Agent.Coordinator -ErrorAction Stop).Path;"
              "$d=Split-Path $p;"
              "$w=Get-ChildItem (Join-Path $d 'logs') -Filter 'worker-*.log' | Sort LastWriteTime | Select -Last 1;"
              "$c=Get-ChildItem (Join-Path $d 'logs') -Filter 'coordinator-*.log' | Sort LastWriteTime | Select -Last 1;"
              "Get-Content $w.FullName -Tail 150 | Out-String | Set-Clipboard;"
              "Start-Sleep 2;"
              "Get-Content $c.FullName -Tail 80 | Out-String | Set-Clipboard"
              "\"")
        ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
        asyncio.run(session({"open": (ps_exe, ps)}, 30))
    elif mode == "deploy":
        # 远程热更新：下载补丁 zip → 停被控端 → 解压覆盖 → 重启（一条 PowerShell 完成，杀掉父进程后继续跑）
        file_id = sys.argv[2]
        ps = ("-NoProfile -Command \""
              f"iwr 'http://{RELAY}/api/download/{file_id}?token={TOKEN}' -OutFile $env:TEMP\\rcp.zip;"
              "$p=(Get-Process Agent.Coordinator -ErrorAction Stop).Path;"
              "$d=Split-Path $p;"
              "Write-Host ('install-dir: '+$d);"
              "Stop-Process -Name Agent.Coordinator -Force;"
              "Start-Sleep 2;"
              "Expand-Archive $env:TEMP\\rcp.zip -DestinationPath $d -Force;"
              "Start-Sleep 1;"
              "Set-Clipboard ('deployed to '+$d);"
              "Start-Process (Join-Path $d 'Agent.Coordinator.exe') -WorkingDirectory $d"
              "\"")
        ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
        asyncio.run(session({"open": (ps_exe, ps)}, 25))
    elif mode == "winpos":
        # 量远端可见窗口的屏幕坐标（确认"此电脑"落在副屏 2560..4480 还是主屏 0..2560）
        addtype = ("Add-Type @'\n"
                   "using System;using System.Text;using System.Runtime.InteropServices;\n"
                   "public class W{\n"
                   " [DllImport(\"user32.dll\")] public static extern bool EnumWindows(Func<IntPtr,IntPtr,bool> cb, IntPtr p);\n"
                   " [DllImport(\"user32.dll\")] public static extern bool IsWindowVisible(IntPtr h);\n"
                   " [DllImport(\"user32.dll\")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);\n"
                   " [DllImport(\"user32.dll\")] public static extern bool GetWindowRect(IntPtr h, out R r);\n"
                   " public struct R{public int L,T,Rt,B;}\n"
                   "}\n"
                   "'@")
        body = ("$sb=New-Object System.Text.StringBuilder;"
                "[W]::EnumWindows({param($h,$p)"
                " if([W]::IsWindowVisible($h)){"
                "$t=New-Object System.Text.StringBuilder 256;"
                "[void][W]::GetWindowText($h,$t,256);"
                "$ttl=$t.ToString();"
                "$r=New-Object W+R;"
                "[void][W]::GetWindowRect($h,[ref]$r);"
                "if($ttl.Length -gt 0 -and ($r.Rt - $r.L) -gt 300){"
                "[void]$sb.AppendLine($ttl + ' :: ' + $r.L + ',' + $r.T + ' - ' + $r.Rt + ',' + $r.B)}};"
                "return $true},[IntPtr]::Zero)|Out-Null;"
                "Set-Clipboard $sb.ToString()")
        ps = "-NoProfile -Command \"" + addtype + ";" + body + "\""
        ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
        asyncio.run(session({"open": (ps_exe, ps)}, 25))
    elif mode == "deploy64":
        # 走 WebSocket 剪贴板通道传补丁（HTTP 上传拥塞时的备用路）：base64 → 远端剪贴板 → PS 解码落盘 → 停进程覆盖重启
        import base64 as _b64
        zip_path = sys.argv[2]
        blob = _b64.b64encode(open(zip_path, "rb").read()).decode("ascii")
        print(f"[deploy64] zip={len(open(zip_path,'rb').read())}B base64={len(blob)}B", flush=True)
        ps = ("-NoProfile -Command \""
              "$b=Get-Clipboard -Raw;"
              "$tmp=[IO.Path]::GetTempPath()+'rcp.zip';"
              "[IO.File]::WriteAllBytes($tmp,[Convert]::FromBase64String($b));"
              "$p=(Get-Process Agent.Coordinator -ErrorAction Stop).Path;"
              "$d=Split-Path $p;"
              "Set-Clipboard 'ok';"
              "Stop-Process -Name Agent.Coordinator -Force;"
              "Start-Sleep 2;"
              "Expand-Archive $tmp -DestinationPath $d -Force;"
              "Start-Sleep 1;"
              "Start-Process (Join-Path $d 'Agent.Coordinator.exe') -WorkingDirectory $d"
              "\"")

        async def _run():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print("[connected]", flush=True)
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(1.0)
                    # 先把 base64 送进被控端剪贴板（0x20 文本同步）
                    await ws.send_bytes(frame(0x20, blob.encode("ascii")))
                    print("[sent base64 via clipboard channel]", flush=True)
                    await asyncio.sleep(4.0)   # 等被控端写入剪贴板
                    # 再触发部署脚本（它会先把剪贴板读出来落盘）
                    payload = json.dumps({"exePath": "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
                                           "args": ps}).encode("utf-8")
                    await ws.send_bytes(frame(0x11, payload))
                    print("[sent deploy script]", flush=True)
                    await asyncio.sleep(20)

        asyncio.run(_run())
    elif mode == "frames":
        # 闪烁测量：切到主屏采集 -> 录 N 秒视频 -> 中途打开此电脑 -> 存 .h264（本地另跑 ffmpeg 抽帧逐帧检查）
        import sys as _s
        seconds = float(_s.argv[2]) if len(_s.argv) > 2 else 8.0
        open_at = float(_s.argv[3]) if len(_s.argv) > 3 else 3.0
        out_h264 = _s.argv[4] if len(_s.argv) > 4 else "D:/tools/rc-flash.h264"

        async def _rec():
            buf = bytearray()
            opened = False
            async with aiohttp.ClientSession() as s:
                async with s.ws_connect(f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}",
                                        max_msg_size=64 * 1024 * 1024) as ws:
                    print("[connected]", flush=True)
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.6)
                    await ws.send_bytes(frame(0x61, bytes([2])))     # CaptureModeHint=PrimaryOnly
                    print("[switched to primary capture]", flush=True)
                    t0 = asyncio.get_event_loop().time()
                    end = t0 + seconds
                    while True:
                        now2 = asyncio.get_event_loop().time()
                        left = end - now2
                        if left <= 0:
                            break
                        if not opened and now2 - t0 >= open_at:
                            opened = True
                            await ws.send_bytes(frame(0x11, json.dumps({"exePath": "::this-pc", "args": ""}).encode()))
                            print(f"[opened this-pc @+{open_at}s]", flush=True)
                        try:
                            msg = await ws.receive(timeout=min(left, 1.0))
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        d = msg.data
                        t, n = d[0], struct.unpack(">I", d[1:5])[0]
                        pl = d[5:5 + n]
                        if t == 0x01 and n > 8:
                            buf += pl[8:]                             # 去 8B 时间戳 -> 裸 H.264 码流
                        elif t == 0x30:
                            await ws.send_bytes(frame(0x30, pl))
                    open(out_h264, "wb").write(bytes(buf))
                    print(f"[saved {out_h264} {len(buf)} bytes]", flush=True)
        asyncio.run(_rec())
    else:
        print(__doc__)

if __name__ == "__main__":
    main()
