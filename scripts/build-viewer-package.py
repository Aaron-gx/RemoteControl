"""打包"主控端安装包"：把服务器地址与连接令牌**预置进包**，客户拿到双击即用。

为什么要有它：主控端界面里没有令牌输入框，令牌只读 config\viewer.json ——
让客户自己去改配置文件是不合理的。这里在打包时写一份 viewer-defaults.txt 放在程序目录，
主控端启动时会把**配置里为空**的项用预置值补上（已有配置优先，不会覆盖厂商自己填的值）。

产物：
  build\viewer\            —— 程序目录（含 viewer-defaults.txt）
  build\主控端安装包.zip    —— 直接发给客户解压即用

用法：
  python scripts\build-viewer-package.py [--out <目录>]
"""
import io
import json
import os
import subprocess
import sys
import zipfile

BS = chr(92)
ROOT = r'E:\Learn\MeProject\2026.9.20-uu\RemoteControl'
DOTNET = r'E:\tools\dotnet\dotnet.exe'

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
    """预置值来源：先读厂商自己的主控端配置，退回被控端配置（同一个服务器与令牌）。"""
    for path in (r'E:\RemoteControl\viewer\config\viewer.json',
                 r'E:\RemoteControl\agent\config\agent.json'):
        try:
            d = json.load(io.open(path, encoding='utf-8-sig'))
            server, token = d.get('ServerUrl', ''), d.get('AgentToken', '')
            if server and token:
                print(f'  预置值来源：{path}')
                return server, token
        except Exception as e:
            print(f'  （读 {path} 失败：{e}）')
    return '', ''


def main():
    out = os.path.join(ROOT, 'build', 'viewer')
    args = sys.argv[1:]
    if '--out' in args:
        out = args[args.index('--out') + 1]

    server, token = read_defaults()
    if not server or not token:
        raise SystemExit('没能拿到服务器地址/令牌：请确认 E:\\RemoteControl\\viewer\\config\\viewer.json 里有 ServerUrl 与 AgentToken')
    print(f'预置服务器：{server}  令牌长度：{len(token)}')

    print(f'1) 发布主控端 → {out} …')
    sh(DOTNET, 'publish', os.path.join(ROOT, 'src', 'Viewer', 'Viewer.csproj'),
       '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', out,
       '--nologo', '-v', 'minimal')

    print('2) 写入 viewer-defaults.txt …')
    os.makedirs(out, exist_ok=True)
    with io.open(os.path.join(out, 'viewer-defaults.txt'), 'w', encoding='utf-8') as f:
        f.write('# 主控端预置值（打包时写入）。config\\viewer.json 里为空的项会用这里的值补齐。\n')
        f.write(f'ServerUrl={server}\n')
        f.write(f'Token={token}\n')

    print('3) 打 zip 便于分发 …')
    zip_out = os.path.join(ROOT, 'build', '主控端安装包.zip')
    if os.path.exists(zip_out):
        os.remove(zip_out)
    count = 0
    with zipfile.ZipFile(zip_out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for dirpath, _dirs, files in os.walk(out):
            for fn in files:
                full = os.path.join(dirpath, fn)
                # config\viewer.json 是**本机配置**，不要打进分发包（客户各自生成）
                rel = os.path.relpath(full, out)
                # config\viewer.json 是**本机配置**（服务器/令牌/上次连的被控端），logs 里是开发机的
                # 运行记录（含本机路径、机器名）。两者都只属于这台机器，客户各自生成，不能打进分发包。
                low = rel.lower()
                # 厂商侧文档不能发给客户：运维-授权与防绕过/部署/验收报告/P2P方案 讲的是授权、
                # 服务器与撤销体系（2026-09 复盘：这些文档曾随包发给客户）。客户只留使用说明。
                if low.startswith('docs' + BS) and low != 'docs' + BS + '使用说明.md':
                    continue
                if low.startswith('config' + BS) or low.startswith('logs' + BS):
                    continue
                z.write(full, rel)
                count += 1
    print(f'完成：{zip_out}（{round(os.path.getsize(zip_out) / 1048576, 1)} MB，{count} 个文件）')
    print('   客户拿到 zip：解压 → 双击 Viewer.exe 即用（地址与令牌已预置）')


if __name__ == '__main__':
    main()
