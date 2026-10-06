# -*- coding: utf-8 -*-
"""
主控端侧集成测试的桩（仅测试用，不进产品）：
- 一个极简"中继"：/healthz /api/agents /api/license /api/download + /ws 配对转发（协议帧 [1B type][4B BE len][payload]）
- 一个"假被控端"：响应软件列表请求（含两条假软件）、回显心跳；收到驱动指令后推送 ClipboardFile(IsFolder)
约定：绝不在本机跑真被控端（用户硬规矩），被控端行为由该桩代替。
用法：python scripts/_test_stub_relay.py [--push-folder-delay 12]
"""
import argparse
import asyncio
import json
import os
import struct
import sys

import aiohttp
from aiohttp import web

TOKEN = "testtoken"
AGENT_ID = "TESTPC"
PORT = 18080

HERE = os.path.dirname(os.path.abspath(__file__))
WORKDIR = os.path.join(os.environ.get("TEMP", "/tmp"), "rc-viewertest")
REMOTE_FOLDER = os.path.join(WORKDIR, "remote", "测试文件夹")
ZIP_PATH = os.path.join(WORKDIR, "测试文件夹.zip")

agent_srv = None     # 假被控端在服务器侧的连接（viewer→agent 要发到这里）
viewer_ws = None     # 真主控端连接
stats = {"viewer_msgs": 0, "list_requests": 0, "folder_pushed": False}


def frame(msg_type: int, payload: bytes) -> bytes:
    return bytes([msg_type]) + struct.pack(">I", len(payload)) + payload


async def fake_agent_loop(session):
    """假被控端：连上自己（中继），按需应答。它只负责**收**服务器转来的消息并**回**响应。"""
    url = f"ws://127.0.0.1:{PORT}/ws?role=agent&id={AGENT_ID}&token={TOKEN}"
    while True:
        try:
            async with session.ws_connect(url) as ws:
                print("[fake-agent] registered", flush=True)
                async for msg in ws:
                    if msg.type != aiohttp.WSMsgType.BINARY:
                        continue
                    data = msg.data
                    if len(data) < 5:
                        continue
                    mtype = data[0]
                    plen = struct.unpack(">I", data[1:5])[0]
                    payload = data[5:5 + plen]
                    print(f"[fake-agent] RECV type=0x{mtype:02X} len={len(payload)}", flush=True)
                    if mtype == 0x15:  # RequestSoftwareList
                        stats["list_requests"] += 1
                        lst = [
                            {"Name": "记事本", "ExePath": "C:\\Windows\\notepad.exe"},
                            {"Name": "计算器", "ExePath": "C:\\Windows\\System32\\calc.exe"},
                        ]
                        await ws.send_bytes(frame(0x10, json.dumps(lst).encode("utf-8")))
                        print(f"[fake-agent] sent software list x{stats['list_requests']}", flush=True)
                    elif mtype == 0x30:  # Heartbeat：回显（主控端测 RTT）
                        await ws.send_bytes(frame(0x30, payload))
                    elif mtype == 0x11:  # OpenSoftware：记一行（真实打开只在真被控机验）
                        print(f"[fake-agent] got OPEN: {payload.decode('utf-8', 'replace')}", flush=True)
                    # 其余消息忽略
        except Exception as e:
            print(f"[fake-agent] reconnect: {e}", flush=True)
            await asyncio.sleep(1)


async def push_folder_notification():
    """给主控端推送"远程复制了文件夹"通知（触发主控端下载→解压→进剪贴板）。"""
    global viewer_ws
    # 准备远端文件夹与 zip（作为"中继暂存"内容）
    os.makedirs(os.path.join(REMOTE_FOLDER, "子目录"), exist_ok=True)
    with open(os.path.join(REMOTE_FOLDER, "说明.txt"), "w", encoding="utf-8") as f:
        f.write("这是来自假被控端的文件夹内容")
    with open(os.path.join(REMOTE_FOLDER, "子目录", "数据.bin"), "wb") as f:
        f.write(b"\xAB" * 4096)
    import zipfile
    if os.path.exists(ZIP_PATH):
        os.remove(ZIP_PATH)
    with zipfile.ZipFile(ZIP_PATH, "w") as z:
        for root, _, files in os.walk(REMOTE_FOLDER):
            for name in files:
                full = os.path.join(root, name)
                z.write(full, os.path.relpath(full, REMOTE_FOLDER))

    info = {
        "FileId": "zip0001",
        "FileName": "测试文件夹.zip",
        "FileSize": os.path.getsize(ZIP_PATH),
        "IsFolder": True,
        "FolderName": "测试文件夹",
    }
    for _ in range(60):
        if viewer_ws is not None:
            await viewer_ws.send_bytes(frame(0x21, json.dumps(info).encode("utf-8")))
            stats["folder_pushed"] = True
            print("[fake-agent] pushed ClipboardFile(IsFolder) to viewer", flush=True)
            return
        await asyncio.sleep(0.5)
    print("[fake-agent] WARN: no viewer connected, folder not pushed", flush=True)


def make_app():
    async def healthz(req):
        return web.Response(text="ok")

    async def agents(req):
        return web.json_response({"agents": [{"id": AGENT_ID, "connectedAt": "2026-10-04T00:00:00Z"}]})

    async def license(req):
        return web.json_response({"state": "ok", "lic": "TEST", "expires": "2099-01-01T00:00:00Z"})

    async def download(req):
        fid = req.match_info["fid"]
        if fid != "zip0001" or not os.path.exists(ZIP_PATH):
            return web.Response(status=404, text="no such file")
        return web.Response(body=open(ZIP_PATH, "rb").read(),
                            content_type="application/octet-stream")

    async def upload(req):
        return web.json_response(status=200, data={"fileId": "upl1", "fileName": "x", "fileSize": 1})

    async def ws_handler(req):
        global viewer_ws, agent_srv
        role = req.query.get("role", "")
        ws = web.WebSocketResponse()
        await ws.prepare(req)
        if role == "agent":
            # 假被控端（fake_agent_loop 以客户端身份连进来）：
            #  · 它**发来**的帧（列表/心跳回显）→ 转发给主控端（下面的循环）
            #  · 主控端发来的帧 → 由 viewer 分支发到 agent_srv（本连接的服务器侧）上
            agent_srv = ws
            print("[relay] agent-side connected", flush=True)
            try:
                async for msg in ws:
                    if msg.type != aiohttp.WSMsgType.BINARY:
                        continue
                    print(f"[relay] agent->viewer frame type=0x{msg.data[0]:02X}", flush=True)
                    if viewer_ws is not None:
                        await viewer_ws.send_bytes(msg.data)
            finally:
                print("[relay] agent-side disconnected", flush=True)
            return ws
        viewer_ws = ws
        print("[relay] viewer connected", flush=True)
        try:
            async for msg in ws:
                if msg.type != aiohttp.WSMsgType.BINARY:
                    continue
                stats["viewer_msgs"] += 1
                print(f"[relay] viewer->agent frame type=0x{msg.data[0]:02X} agent_srv={'Y' if agent_srv else 'N'}", flush=True)
                if agent_srv is not None:
                    await agent_srv.send_bytes(msg.data)   # 透传给假被控端（必须发到它连接的服务器侧）
        finally:
            print("[relay] viewer disconnected", flush=True)
            if viewer_ws is ws:
                viewer_ws = None
        return ws

    app = web.Application()
    app.router.add_get("/healthz", healthz)
    app.router.add_get("/api/agents", agents)
    app.router.add_get("/api/license", license)
    app.router.add_get("/api/download/{fid}", download)
    app.router.add_post("/api/upload", upload)
    app.router.add_get("/ws", ws_handler)
    return app


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--push-folder-delay", type=float, default=12)
    args = parser.parse_args()

    os.makedirs(WORKDIR, exist_ok=True)
    app = make_app()
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, "127.0.0.1", PORT)
    await site.start()
    print(f"[relay] listening 127.0.0.1:{PORT}", flush=True)

    async with aiohttp.ClientSession() as session:
        agent_task = asyncio.create_task(fake_agent_loop(session))
        await asyncio.sleep(args.push_folder_delay)
        await push_folder_notification()
        # 再活 90 秒供断言，然后退出
        await asyncio.sleep(90)
        agent_task.cancel()

    await runner.cleanup()
    print("[relay] stats:", json.dumps(stats), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
