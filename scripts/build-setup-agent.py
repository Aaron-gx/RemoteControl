"""打包"被控端安装程序.exe"（单文件，内嵌整个被控端）。

产物：build\setup-agent\被控端安装程序.exe —— 就一个文件，拷到目标机双击即可，
无需携带 payload 目录。同时生成 build\被控端安装包.zip。
"""
import io
import os
import shutil
import subprocess
import sys
import zipfile

BS = chr(92)
ROOT = r'E:\Learn\MeProject\2026.9.20-uu\RemoteControl'
DOTNET = r'E:\tools\dotnet\dotnet.exe'
OUT = os.path.join(ROOT, 'build', 'setup-agent')
PAYLOAD_ZIP = os.path.join(ROOT, 'build', 'payload.zip')

env = dict(os.environ)
env['DOTNET_ROOT'] = r'E:\tools\dotnet'
env['PATH'] = r'E:\tools\dotnet;' + env.get('PATH', '')
env['NUGET_PACKAGES'] = r'E:\tools\nuget-packages'
env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
env['DOTNET_NOLOGO'] = '1'


def sh(*args, **kw):
    print('  $', ' '.join(args))
    r = subprocess.run(args, env=env, **kw)
    if r.returncode != 0:
        raise SystemExit(f'命令失败: {args}')


def read_defaults():
    """从被控端现有配置里取服务器地址与口令，作为安装包的预置值（被控端零填写）。"""
    cfg = os.path.join(r'E:\RemoteControl\agent\config\agent.json')
    try:
        import json
        d = json.load(io.open(cfg, encoding='utf-8-sig'))
        return d.get('ServerUrl', ''), d.get('AgentToken', '')
    except Exception as e:
        print('  读取默认配置失败：', e)
        return '', ''


def main():
    os.makedirs(OUT, exist_ok=True)
    server, token = read_defaults()
    print(f'预置服务器：{server}  口令长度：{len(token)}')

    agent_src = os.path.join(ROOT, 'build', 'agent')
    if not os.path.isfile(os.path.join(agent_src, 'Agent.Coordinator.exe')):
        raise SystemExit('缺少 build\\agent\\Agent.Coordinator.exe，先跑 build-all.ps1')

    # 1) 组装 payload.zip（结构与仓库一致：build\agent / drivers / scripts / setup-defaults.txt）
    print('1) 组装 payload.zip …')
    if os.path.exists(PAYLOAD_ZIP):
        os.remove(PAYLOAD_ZIP)
    with zipfile.ZipFile(PAYLOAD_ZIP, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for base in ('build' + BS + 'agent', 'drivers'):
            src = os.path.join(ROOT, base)
            for dirpath, _dirs, files in os.walk(src):
                for f in files:
                    full = os.path.join(dirpath, f)
                    rel = os.path.relpath(full, ROOT)
                    # config\agent.json 带着开发机的 AgentId，logs 是开发机的运行记录：
                    # 一旦混进安装包，装到客户机上就是"用别人的身份"（实测踩过同类坑）。
                    low = os.path.relpath(full, src).lower()
                    if low.startswith('config' + BS) or low.startswith('logs' + BS):
                        continue
                    # 厂商侧文档不进安装包（同主控端包：客户只留使用说明）
                    if low.startswith('docs' + BS) and low != 'docs' + BS + '使用说明.md':
                        continue
                    z.write(full, rel)
        for f in ('install-agent.ps1', 'create-remote-user.ps1', '连接自检.ps1', 'fix-agent-autostart.cmd'):
            p = os.path.join(ROOT, 'scripts', f)
            if os.path.isfile(p):
                z.write(p, 'scripts' + BS + f)
        z.writestr('setup-defaults.txt',
                   f'ServerUrl={server}\nToken={token}\n')
    print('   payload.zip =', round(os.path.getsize(PAYLOAD_ZIP) / 1048576, 1), 'MB')

    # 2) 发布安装程序（单个 exe，内嵌 payload.zip）
    print('2) 发布安装程序（单文件）…')
    sh(DOTNET, 'publish', os.path.join(ROOT, 'src', 'Tools', 'AgentSetup', 'AgentSetup.csproj'),
       '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', OUT,
       '-p:PayloadZip=' + PAYLOAD_ZIP, '--nologo', '-v', 'minimal')

    exe = os.path.join(OUT, '被控端安装程序.exe')
    if not os.path.isfile(exe):
        raise SystemExit('没有生成 被控端安装程序.exe')
    size = os.path.getsize(exe) / 1048576
    print(f'3) 产物：{exe}（{size:.1f} MB，单文件）')

    # 3) 打 zip 便于分发
    zip_out = os.path.join(ROOT, 'build', '被控端安装包.zip')
    if os.path.exists(zip_out):
        os.remove(zip_out)
    with zipfile.ZipFile(zip_out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.write(exe, '被控端安装程序.exe')
    print('   分发压缩包：', zip_out, round(os.path.getsize(zip_out) / 1048576, 1), 'MB')
    print('完成：把「被控端安装程序.exe」拷到目标电脑，双击 → 点「开始安装」即可。')


if __name__ == '__main__':
    main()
