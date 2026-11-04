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
import os
import struct
import sys

import aiohttp

# 中继地址与口令从 config\relay-token.txt 读（gitignore 已排除，绝不入版本库；2026-10-14 推 GitHub 前清密）
# 格式：relay=IP:端口 / token=口令 / admin=管理口令；环境变量 RC_RELAY/RC_TOKEN 可覆盖
_rcfg = {"relay": "", "token": ""}
_rcfgPath = os.path.join(os.path.dirname(os.path.abspath(__file__)),
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
RELAY = os.environ.get("RC_RELAY") or _rcfg["relay"]   # 换服务器：set RC_RELAY=新IP:端口
TOKEN = os.environ.get("RC_TOKEN") or _rcfg["token"]
if not RELAY or not TOKEN:
    sys.exit("缺少中继地址/口令：请在 config\\relay-token.txt 里填 relay= 与 token=（该文件不入 git），"
             "或设 RC_RELAY / RC_TOKEN 环境变量")

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


def safe_out_path(p) -> str:
    """输出路径守卫（2026-10-14 安全扫描整改）：拒绝 .. 穿越——
    诊断产物只写显式给定的绝对路径；路径里任何一段是 .. 直接拒绝。"""
    raw = str(p).strip()
    parts = raw.replace("/", os.sep).split(os.sep)
    if ".." in parts:
        raise SystemExit(f"拒绝穿越路径: {raw}")
    return os.path.abspath(raw)

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
                        import os as _os
                        print("[MONITOR] " + payload.decode("utf-8", "replace")[:4000 if _os.environ.get("RC_FULLMON") else 200], flush=True)
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
    elif mode == "typetest":
        # 端到端键盘验证：开记事本(外屏) → 键入 HELLO → Ctrl+A Ctrl+C → 等 0x20 剪贴板回传
        # → 清空 → Ctrl+V 粘贴回 → 再 Ctrl+A Ctrl+C 验证粘贴结果。
        # 剪贴板回传 = "hello1/2" 时说明 Ctrl+C 与 Ctrl+V 在被控端真实生效。
        async def _type():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    clips = []

                    async def recv_for(sec):
                        end = asyncio.get_event_loop().time() + sec
                        while True:
                            left = end - asyncio.get_event_loop().time()
                            if left <= 0:
                                return
                            try:
                                msg = await ws.receive(timeout=left)
                            except asyncio.TimeoutError:
                                return
                            if msg.type != aiohttp.WSMsgType.BINARY:
                                continue
                            data = msg.data
                            t, n = data[0], struct.unpack(">I", data[1:5])[0]
                            payload = data[5:5 + n]
                            if t == 0x30:
                                await ws.send_bytes(frame(0x30, payload))
                            elif t == 0x20:
                                txt = payload.decode("utf-8", "replace").strip()
                                clips.append(txt)
                                print(f"[CLIP] #{len(clips)}: {txt!r}", flush=True)

                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))

                    async def tap(vk, hold=0.08):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)

                    async def combo(mod, vk):
                        await key(mod); await asyncio.sleep(0.08)
                        await key(vk); await asyncio.sleep(0.08)
                        await key(vk, True); await asyncio.sleep(0.02)
                        await key(mod, True)

                    # 1) 开记事本（走 OPEN 通道 → StartOnVirtualScreen → 外屏）
                    await ws.send_bytes(frame(0x15, b""))   # 上线信号
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    print("[sent OPEN notepad]", flush=True)
                    await recv_for(3.0)

                    # 2) 键入 HELLO（VK: H=0x48 E=0x45 L=0x4C O=0x4F）
                    for vk in (0x48, 0x45, 0x4C, 0x4C, 0x4F):
                        await tap(vk)
                        await asyncio.sleep(0.12)
                    await recv_for(0.6)

                    # 3) Ctrl+A 全选 → Ctrl+C 复制（Ctrl=0x11 A=0x41 C=0x43）
                    await combo(0x11, 0x41); await asyncio.sleep(0.25)
                    await combo(0x11, 0x43)
                    await recv_for(2.5)          # 等剪贴板同步回传
                    c1 = list(clips)

                    # 4) 清空（Ctrl+A → Del=0x2E）→ Ctrl+V 粘贴回（V=0x56）
                    await combo(0x11, 0x41); await asyncio.sleep(0.25)
                    await tap(0x2E); await asyncio.sleep(0.25)
                    await combo(0x11, 0x56)
                    await recv_for(1.0)

                    # 5) 再 Ctrl+A Ctrl+C：剪贴板应重新出现 HELLO（= 粘贴真的发生了）
                    clips.clear()
                    await combo(0x11, 0x41); await asyncio.sleep(0.25)
                    await combo(0x11, 0x43)
                    await recv_for(2.5)

                    print("=" * 60, flush=True)
                    ok1 = any("HELLO" in c for c in c1)
                    ok2 = any("HELLO" in c for c in clips)
                    print(f"Ctrl+C 复制: {'✓ 生效' if ok1 else '✗ 未生效'}（第一次剪贴板={c1}）", flush=True)
                    print(f"Ctrl+V 粘贴: {'✓ 生效' if ok2 else '✗ 未生效'}（粘贴后剪贴板={clips}）", flush=True)
                    print("=" * 60, flush=True)
        asyncio.run(_type())
    elif mode == "kbtest2":
        # 可视化键盘测试：开记事本 → 键入 → Ctrl+A → Ctrl+C → 截图虚拟屏(base64 回传)。
        # 全程保持 viewer 在线（孤儿回收不触发），剪贴板 0x20 与截图都从同一会话收。
        async def _kb2():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    clips = []

                    async def recv_for(sec):
                        end = asyncio.get_event_loop().time() + sec
                        while True:
                            left = end - asyncio.get_event_loop().time()
                            if left <= 0:
                                return
                            try:
                                msg = await ws.receive(timeout=left)
                            except asyncio.TimeoutError:
                                return
                            if msg.type != aiohttp.WSMsgType.BINARY:
                                continue
                            data = msg.data
                            t, n = data[0], struct.unpack(">I", data[1:5])[0]
                            payload = data[5:5 + n]
                            if t == 0x30:
                                await ws.send_bytes(frame(0x30, payload))
                            elif t == 0x20:
                                txt = payload.decode("utf-8", "replace").strip()
                                clips.append(txt)
                                tag = "B64-SHOT" if len(txt) > 50000 else ("HELLO?" if "HELLO" in txt else "OTHER")
                                print(f"[CLIP] {tag} len={len(txt)} head={txt[:60]!r}", flush=True)

                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))

                    async def tap(vk, hold=0.04):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)

                    async def combo(mod, vk, pre=0.05, post=0.03):
                        await key(mod); await asyncio.sleep(pre)
                        await key(vk); await asyncio.sleep(post)
                        await key(vk, True); await asyncio.sleep(0.03)
                        await key(mod, True)

                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    print("[OPEN notepad]", flush=True)
                    await recv_for(3.5)

                    for vk in (0x48, 0x45, 0x4C, 0x4C, 0x4F):   # HELLO
                        await tap(vk)
                        await asyncio.sleep(0.15)
                    await recv_for(0.8)
                    print("[typed HELLO]", flush=True)

                    await combo(0x11, 0x41)                        # Ctrl+A
                    await recv_for(0.8)
                    print("[Ctrl+A done]", flush=True)
                    await combo(0x11, 0x43)                        # Ctrl+C
                    await recv_for(2.5)
                    print("[Ctrl+C done]", flush=True)

                    # 同会话截图：PS 抓虚拟屏 → 缩放 → PNG base64 → Set-Clipboard → 0x20 回来
                    shot = ("-NoProfile -Command \""
                            "Add-Type -AssemblyName System.Windows.Forms;"
                            "Add-Type -AssemblyName System.Drawing;"
                            "$b=New-Object System.Drawing.Bitmap 1920,1080;"
                            "$g=[System.Drawing.Graphics]::FromImage($b);"
                            "$g.CopyFromScreen(2560,0,0,0,$b.Size);"
                            "$s=New-Object System.Drawing.Bitmap 640,360;"
                            "$g2=[System.Drawing.Graphics]::FromImage($s);"
                            "$g2.DrawImage($b,0,0,640,360);"
                            "$s.Save($env:TEMP+'\\vsk.png',[System.Drawing.Imaging.ImageFormat]::Png);"
                            "Set-Clipboard ([Convert]::ToBase64String([IO.File]::ReadAllBytes($env:TEMP+'\\vsk.png')))"
                            "\"")
                    ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": ps_exe, "args": shot}).encode()))
                    await recv_for(9.0)

                    import base64 as _b
                    b64s = [c for c in clips if c.startswith("iVBOR")]   # PNG base64 魔数
                    hellos = [c for c in clips if "HELLO" in c and len(c) < 5000]
                    print("=" * 60, flush=True)
                    print(f"剪贴板出现 HELLO（Ctrl+C 生效）: {'✓' if hellos else '✗'}", flush=True)
                    if b64s:
                        try:
                            open(safe_out_path(r"D:\tools\vsk.png"), "wb").write(_b.b64decode(b64s[-1]))
                            print("截图已存 D:\\tools\\vsk.png", flush=True)
                        except Exception as e:
                            print("截图解码失败:", e, flush=True)
                    else:
                        print("截图未收到", flush=True)
                    print("=" * 60, flush=True)
        asyncio.run(_kb2())
    elif mode == "kbtest3":
        # 确定性键盘测试：开记事本 → 键入 → Ctrl+A → 读编辑框文本(WM_GETTEXT)/选区(EM_GETSEL)/
        # 前台焦点(GetGUIThreadInfo) → 输出到文件 → clip 回传。不依赖截图与视觉模型。
        async def _kb3():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    clips = []

                    async def recv_for(sec):
                        end = asyncio.get_event_loop().time() + sec
                        while True:
                            left = end - asyncio.get_event_loop().time()
                            if left <= 0:
                                    return
                            try:
                                msg = await ws.receive(timeout=left)
                            except asyncio.TimeoutError:
                                return
                            if msg.type != aiohttp.WSMsgType.BINARY:
                                continue
                            data = msg.data
                            t, n = data[0], struct.unpack(">I", data[1:5])[0]
                            payload = data[5:5 + n]
                            if t == 0x30:
                                await ws.send_bytes(frame(0x30, payload))
                            elif t == 0x20:
                                txt = payload.decode("utf-8", "replace").strip()
                                clips.append(txt)
                                print(f"[CLIP] len={len(txt)} head={txt[:70]!r}", flush=True)

                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))

                    async def tap(vk, hold=0.04):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)

                    async def combo(mod, vk, pre=0.06, post=0.04):
                        await key(mod); await asyncio.sleep(pre)
                        await key(vk); await asyncio.sleep(post)
                        await key(vk, True); await asyncio.sleep(0.04)
                        await key(mod, True)

                    import base64 as _b64m
                    probe_ps = r'''
Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public class NP {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, StringBuilder lp);
 [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, out int wp, out int lp);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GTI gti);
 [StructLayout(LayoutKind.Sequential)] public struct RECTc{public int L,T,R,B;}
 [StructLayout(LayoutKind.Sequential)] public struct GTI{
  public int cbSize; public uint flags; public IntPtr hwndActive; public IntPtr hwndFocus;
  public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret;
  public RECTc rcCaret;
 }
}
"@
$h=[NP]::FindWindowW('Notepad',$null)
$out=@()
$out+='hwnd='+$h
$e=[NP]::FindWindowExW($h,[IntPtr]::Zero,'Edit',$null)
$out+='edit='+$e
if($e -ne [IntPtr]::Zero){
 $sb=New-Object System.Text.StringBuilder 4096
 [void][NP]::SendMessageW($e,0x000D,[IntPtr]4096,$sb)
 $out+='TEXT=['+$sb.ToString()+']'
 $s=0;$t=0
 [void][NP]::SendMessageW($e,0x00B0,[ref]$s,[ref]$t)
 $out+=('SEL={0},{1}' -f $s,$t)
}
$g=New-Object NP+GTI
$g.cbSize=[System.Runtime.InteropServices.Marshal]::SizeOf($g)
$fg=[NP]::GetForegroundWindow()
[void][NP]::GetGUIThreadInfo(0,[ref]$g)
$out+='FG='+$fg+' fgFocus='+$g.hwndFocus+' fgCaret='+$g.hwndCaret+' caretXY='+$g.rcCaret.L+','+$g.rcCaret.T
$out -join [Environment]::NewLine | Out-File ($env:TEMP+'\kb3.txt')
Get-Content ($env:TEMP+'\kb3.txt') | clip
'''
                    probe = "-NoProfile -EncodedCommand " + _b64m.b64encode(
                        probe_ps.encode("utf-16-le")).decode()
                    ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"

                    async def probe_dump(tag):
                        await ws.send_bytes(frame(0x11, json.dumps(
                            {"exePath": ps_exe, "args": probe}).encode()))
                        await recv_for(7.0)
                        print(f"--- {tag} probed ---", flush=True)

                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    await recv_for(3.5)

                    await probe_dump("fresh-open")

                    for vk in (0x48, 0x45, 0x4C, 0x4C, 0x4F):
                        await tap(vk)
                        await asyncio.sleep(0.15)
                    await recv_for(0.8)
                    await probe_dump("after-typing")

                    await combo(0x11, 0x41)
                    await recv_for(0.8)
                    await probe_dump("after-ctrlA")
        asyncio.run(_kb3())
    elif mode == "capture":
        # 抓视频流到本地文件：连接期间收到的全部 0x01 视频帧按序落盘（裸 H.264 annex-B），
        # 之后本地 ffmpeg 解码任意帧 = 主控端用户看到的画面。用法：capture <out.bin> [秒]
        out_path = sys.argv[2] if len(sys.argv) > 2 else r"D:\tools\cap.h264"
        secs = float(sys.argv[3]) if len(sys.argv) > 3 else 15.0
        async def _cap():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    buf = bytearray()
                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))
                    async def tap(vk, hold=0.04):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)
                    async def combo(mod, vk, pre=0.06, post=0.04):
                        await key(mod); await asyncio.sleep(pre)
                        await key(vk); await asyncio.sleep(post)
                        await key(vk, True); await asyncio.sleep(0.04)
                        await key(mod, True)
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.5)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    print("[OPEN notepad]", flush=True)
                    end = asyncio.get_event_loop().time() + secs
                    typed = False

                    async def typing_task():
                        await asyncio.sleep(3.5)
                        for vk in (0x48, 0x45, 0x4C, 0x4C, 0x4F):
                            await tap(vk)
                            await asyncio.sleep(0.15)
                        await asyncio.sleep(0.5)
                        await combo(0x11, 0x41)   # Ctrl+A
                        print("[typed HELLO + Ctrl+A]", flush=True)
                    asyncio.get_event_loop().create_task(typing_task())
                    while True:
                        left = end - asyncio.get_event_loop().time()
                        if left <= 0:
                            break
                        try:
                            msg = await ws.receive(timeout=min(left, 1.0))
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        data = msg.data
                        t, n = data[0], struct.unpack(">I", data[1:5])[0]
                        payload = data[5:5 + n]
                        if t == 0x30:
                            await ws.send_bytes(frame(0x30, payload))
                        elif t == 0x01:
                            buf.extend(payload)
                    open(safe_out_path(out_path), "wb").write(bytes(buf))
                    print(f"[saved {len(buf)} bytes -> {out_path}]", flush=True)
        asyncio.run(_cap())
    elif mode == "kbtest4":
        # 修饰键区分实验（视频流取证）：同一记事本里依次测
        #   轮1 Shift+End   轮2 左Ctrl+A   轮3 右Ctrl+A
        # 每轮之间 1.2s 停顿（按 2fps 解码正好各占 2-3 帧），选区=蓝色高亮肉眼可辨。
        out_path = sys.argv[2] if len(sys.argv) > 2 else r"D:\tools\cap4.h264"
        async def _kb4():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    buf = bytearray()
                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))
                    async def tap(vk, hold=0.04):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)
                    async def combo(mod, vk, pre=0.06, post=0.04):
                        await key(mod); await asyncio.sleep(pre)
                        await key(vk); await asyncio.sleep(post)
                        await key(vk, True); await asyncio.sleep(0.04)
                        await key(mod, True)
                    async def type_str(s):
                        for ch in s:
                            vk = ord(ch.upper())
                            await tap(vk)
                            await asyncio.sleep(0.09)
                    async def rounds():
                        await asyncio.sleep(3.5)      # 等记事本开好
                        await type_str("AB")
                        await combo(0x10, 0x23)        # Shift+End → 应选中 AB
                        await asyncio.sleep(1.6)
                        await tap(0x24)                # Home 取消选区
                        await tap(0x0D)                # 回车到第2行
                        await type_str("CD")
                        await combo(0x11, 0x41)        # 左Ctrl+A → 应全选
                        await asyncio.sleep(1.6)
                        await tap(0x24)
                        await tap(0x0D)
                        await type_str("EF")
                        await combo(0xA3, 0x41)        # 右Ctrl+A（0xA3=VK_RCONTROL）
                        await asyncio.sleep(1.8)
                        print("[3 rounds done: Shift+End / LCtrl+A / RCtrl+A]", flush=True)
                    asyncio.get_event_loop().create_task(rounds())
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode())
                    )
                    end = asyncio.get_event_loop().time() + 15.0
                    while True:
                        left = end - asyncio.get_event_loop().time()
                        if left <= 0:
                            break
                        try:
                            msg = await ws.receive(timeout=min(left, 1.0))
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        data = msg.data
                        t, n = data[0], struct.unpack(">I", data[1:5])[0]
                        payload = data[5:5 + n]
                        if t == 0x30:
                            await ws.send_bytes(frame(0x30, payload))
                        elif t == 0x01:
                            buf.extend(payload)
                    open(safe_out_path(out_path), "wb").write(bytes(buf))
                    print(f"[saved {len(buf)} bytes -> {out_path}]", flush=True)
        asyncio.run(_kb4())
    elif mode == "kbtest5":
        # 最终闭环：打字→Ctrl+A→Ctrl+C(等剪贴板回传)→清空→Ctrl+V→再全选复制。
        # 视频流取证(选区高亮) + 剪贴板 0x20 回传(复制生效) 双证据。全程无被控端弹窗。
        out_path = sys.argv[2] if len(sys.argv) > 2 else r"D:\tools\cap9.h264"
        async def _kb5():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    buf = bytearray()
                    clips = []
                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))
                    async def tap(vk, hold=0.05):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)
                    async def combo(mod, vk, pre=0.08, post=0.05):
                        await key(mod); await asyncio.sleep(pre)
                        await key(vk); await asyncio.sleep(post)
                        await key(vk, True); await asyncio.sleep(0.05)
                        await key(mod, True)
                    async def type_str(s2):
                        for ch in s2:
                            await tap(ord(ch.upper()))
                            await asyncio.sleep(0.12)
                    async def script():
                        await asyncio.sleep(3.5)                       # t≈3.5 记事本就绪
                        if os.environ.get("KB_SHIFT_TOGGLE"):
                            await tap(0x10)                            # Shift 单击：微软拼音 中⇄英
                            await asyncio.sleep(0.6)
                        await type_str("HELLO")                        # 轮1: 打字
                        await asyncio.sleep(2.0)                       # t≈6.5
                        await combo(0x11, 0x41)                        # 轮2: Ctrl+A
                        await asyncio.sleep(2.0)                       # t≈8.5
                        await combo(0x11, 0x43)                        # 轮3: Ctrl+C
                        await asyncio.sleep(2.5)                       # 等剪贴板回传
                        await combo(0x11, 0x41)                        # 轮4: 全选+Del 清空
                        await asyncio.sleep(0.4)
                        await tap(0x2E)
                        await asyncio.sleep(1.2)
                        await combo(0x11, 0x56)                        # 轮5: Ctrl+V 粘贴回
                        await asyncio.sleep(2.0)
                        await combo(0x11, 0x41)                        # 轮6: 再全选+复制验证
                        await asyncio.sleep(0.4)
                        await combo(0x11, 0x43)
                        await asyncio.sleep(2.5)
                        print("[script done]", flush=True)
                    asyncio.get_event_loop().create_task(script())
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    end = asyncio.get_event_loop().time() + 22.0
                    while True:
                        left = end - asyncio.get_event_loop().time()
                        if left <= 0:
                            break
                        try:
                            msg = await ws.receive(timeout=min(left, 1.0))
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        data = msg.data
                        t, n = data[0], struct.unpack(">I", data[1:5])[0]
                        payload = data[5:5 + n]
                        if t == 0x30:
                            await ws.send_bytes(frame(0x30, payload))
                        elif t == 0x01:
                            buf.extend(payload)
                        elif t == 0x20:
                            txt = payload.decode("utf-8", "replace").strip()
                            clips.append(txt)
                            print(f"[CLIP#{len(clips)}] len={len(txt)} {txt[:60]!r}", flush=True)
                    open(safe_out_path(out_path), "wb").write(bytes(buf))
                    hellos = [c for c in clips if "HELLO" in c and len(c) < 100]
                    print("=" * 56, flush=True)
                    print(f"剪贴板出现 HELLO: {'✓ Ctrl+C/复制生效' if hellos else '✗ 未生效'}  clips={[c[:20] for c in clips]}", flush=True)
                    print(f"视频流 {len(buf)}B -> {out_path}（本地解码看选区/粘贴）", flush=True)
                    print("=" * 56, flush=True)
        asyncio.run(_kb5())
    elif mode == "kbtest6":
        # 复现用户场景的验收：记事本开好后把它的输入法**强制设回中文**（模拟用户点进
        # 已有窗口时的状态），然后模拟快速连打，验收 EnsureImeEnglishForForeground：
        # 第一个键落下前输入法应被自动切英，字直落不进组字框。
        out_path = sys.argv[2] if len(sys.argv) > 2 else r"D:\tools\cap6.h264"
        async def _kb6():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    buf = bytearray()
                    async def key(vk, up=False, flags=0):
                        await ws.send_bytes(frame(0x05, struct.pack(">HBB", vk, 1 if up else 0, flags)))
                    async def tap(vk, hold=0.08):
                        await key(vk); await asyncio.sleep(hold); await key(vk, True)
                    import base64 as _b
                    set_ime_cn = r'''
Add-Type @"
using System;using System.Runtime.InteropServices;
public class IME6 {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string t);
 [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
$h=[IME6]::FindWindowW("Notepad",$null)
$ime=[IME6]::ImmGetDefaultIMEWnd($h)
[void][IME6]::SendMessageW($ime,0x283,[IntPtr]2,[IntPtr]1)
"IME set NATIVE(中文) for $h" | Out-File $env:TEMP\kb6.txt -Encoding utf8
'''
                    ps_args = "-NoProfile -WindowStyle Hidden -EncodedCommand " + _b.b64encode(
                        set_ime_cn.encode("utf-16-le")).decode()
                    ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"

                    async def script():
                        await asyncio.sleep(3.5)      # 记事本就绪（已被 ForceActivate+IME 英文化）
                        # 强制把记事本输入法设回中文（复现"点进已有窗口"的状态）
                        await ws.send_bytes(frame(0x11, json.dumps(
                            {"exePath": ps_exe, "args": ps_args}).encode()))
                        await asyncio.sleep(2.0)       # 等 PS 跑完、前台回落
                        # 模拟快速连打：HELLO 间隔 30ms（用户"连续按了很多次"的场景）
                        for ch in "HELLOWORLD123":
                            await tap(ord(ch))
                            await asyncio.sleep(0.03)
                        print("[burst typed: HELLOWORLD123]", flush=True)
                        await asyncio.sleep(2.5)
                    asyncio.get_event_loop().create_task(script())
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x11, json.dumps(
                        {"exePath": "C:\\Windows\\System32\\notepad.exe", "args": ""}).encode()))
                    end = asyncio.get_event_loop().time() + 14.0
                    while True:
                        left = end - asyncio.get_event_loop().time()
                        if left <= 0:
                            break
                        try:
                            msg = await ws.receive(timeout=min(left, 1.0))
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        data = msg.data
                        t, n = data[0], struct.unpack(">I", data[1:5])[0]
                        payload = data[5:5 + n]
                        if t == 0x30:
                            await ws.send_bytes(frame(0x30, payload))
                        elif t == 0x01:
                            buf.extend(payload)
                    open(safe_out_path(out_path), "wb").write(bytes(buf))
                    print(f"[saved {len(buf)}B -> {out_path}]", flush=True)
        asyncio.run(_kb6())
    elif mode == "thispc":
        asyncio.run(session({"open": ("::this-pc", "")}, 15))
    elif mode == "diag":
        # 诊断（2026-10-11 healthd 式）：协调器直接应答（不经过引擎），引擎卡死也能用。
        # 用法：python scripts\_rcp.py diag [tail行数]
        tail_lines = int(sys.argv[2]) if len(sys.argv) > 2 else 40
        async def _diag():
            async with aiohttp.ClientSession() as s:
                url = f"ws://{RELAY}/ws?role=viewer&target={AGENT}&token={TOKEN}"
                async with s.ws_connect(url, max_msg_size=64 * 1024 * 1024) as ws:
                    print(f"[connected -> {AGENT}]", flush=True)
                    await ws.send_bytes(frame(0x15, b""))
                    await asyncio.sleep(0.3)
                    await ws.send_bytes(frame(0x56, b""))   # DiagnosticsRequest
                    print("[sent DiagnosticsRequest]", flush=True)
                    end = asyncio.get_event_loop().time() + 10
                    while asyncio.get_event_loop().time() < end:
                        try:
                            msg = await ws.receive(timeout=max(0.1, end - asyncio.get_event_loop().time()))
                        except asyncio.TimeoutError:
                            break
                        if msg.type != aiohttp.WSMsgType.BINARY:
                            continue
                        data = msg.data
                        t, n = data[0], struct.unpack(">I", data[1:5])[0]
                        payload = data[5:5 + n]
                        if t == 0x30:
                            await ws.send_bytes(frame(0x30, payload))
                        elif t == 0x57:   # DiagnosticsReport
                            d = json.loads(payload.decode("utf-8"))
                            print("=" * 62, flush=True)
                            for k in ("Timestamp", "AgentVersion", "EngineConnected", "EngineThreadAlive",
                                      "StatusAgeMs", "ViewerCount", "RelayConnected", "RelayQueued", "RelayDropped",
                                      "UdpActive", "UdpEndpoint", "UdpRtt", "UdpLoss", "UdpPingLoss", "UdpSuspended",
                                      "ConfigSummary"):
                                print(f"{k:20} = {d.get(k)}", flush=True)
                            print("-" * 62, flush=True)
                            wt = (d.get("WorkerLogTail") or "").splitlines()
                            ct = (d.get("CoordinatorLogTail") or "").splitlines()
                            print(f"--- WorkerLogTail（最后 {tail_lines} 行 / 共 {len(wt)} 行）---", flush=True)
                            for line in wt[-tail_lines:]:
                                print("  " + line, flush=True)
                            print(f"--- CoordinatorLogTail（最后 {tail_lines} 行 / 共 {len(ct)} 行）---", flush=True)
                            for line in ct[-tail_lines:]:
                                print("  " + line, flush=True)
                            print("=" * 62, flush=True)
                            return
                    print("!! 10 秒内没等到诊断报告（协调器不响应？）", flush=True)
        asyncio.run(_diag())
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
              "Get-Content $w.FullName -Tail " + str(int(__import__("os").environ.get("RC_TAIL","150"))) + " | Out-String | Set-Clipboard;"
              "Start-Sleep 2;"
              "Get-Content $c.FullName -Tail " + str(int(__import__("os").environ.get("RC_TAIL","150"))//2) + " | Out-String | Set-Clipboard"
              "\"")
        ps_exe = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
        asyncio.run(session({"open": (ps_exe, ps)}, 30))
    elif mode == "deploy":
        # 远程热更新：下载补丁 zip → 停被控端 → 解压覆盖 → 重启（一条 PowerShell 完成，杀掉父进程后继续跑）
        # 【2026-10-08 踩坑】iwr 失败时旧 rcp.zip 残留 → Expand-Archive 把旧版解回去 →
        # "部署成功"其实还是旧版本。必须：先删旧包 → -UseBasicParsing → 校验下载字节数 → 对不上就中止。
        file_id = sys.argv[2]
        expect = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        ps = ("-NoProfile -Command \""
              "$ErrorActionPreference='Stop';"
              "Remove-Item $env:TEMP\\rcp.zip -Force -ErrorAction SilentlyContinue;"
              f"iwr 'http://{RELAY}/api/download/{file_id}?token={TOKEN}' -UseBasicParsing -OutFile $env:TEMP\\rcp.zip;"
              "$z=Get-Item $env:TEMP\\rcp.zip;"
              + (f"if($z.Length -ne {expect}){{Set-Clipboard ('DL-SIZE-MISMATCH '+$z.Length);exit 1}};" if expect else "")
              + "$p=(Get-Process Agent.Coordinator -ErrorAction Stop).Path;"
              "$d=Split-Path $p;"
              "Set-Clipboard ('install-dir: '+$d);"
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
                    open(safe_out_path(out_h264), "wb").write(bytes(buf))
                    print(f"[saved {out_h264} {len(buf)} bytes]", flush=True)
        asyncio.run(_rec())
    else:
        print(__doc__)

if __name__ == "__main__":
    main()
