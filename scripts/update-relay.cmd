@echo off
chcp 936 >nul
setlocal
REM 更新香港中继到最新构建（经 hk.py 走本机代理，不要求本机直连 22 端口）
cd /d "%~dp0.."
set PYTHONPATH=E:\tools\pylibs
echo [1/2] 上传新二进制...
python scripts\hk.py put "build\server\rcserver-linux-amd64" "/tmp/rcserver-new"
if errorlevel 1 ( echo 上传失败 & pause & exit /b 1 )
echo [2/2] 替换并重启服务...
python scripts\hk.py exec "systemctl stop rcserver; cp /tmp/rcserver-new /opt/rcserver/rcserver && chmod +x /opt/rcserver/rcserver; systemctl start rcserver; sleep 2; /opt/rcserver/rcctl status"
echo --- 从本机验证 ---
python -c "import urllib.request;print('healthz:',urllib.request.urlopen('http://202.60.232.209:8080/healthz',timeout=10).read().decode())"
python -c "import urllib.request;print('license:',urllib.request.urlopen('http://202.60.232.209:8080/api/license',timeout=10).read().decode())"
echo.
echo 更新完成。授权管理页（浏览器打开，把 ^<中继令牌^> 换成实际令牌）：
echo    http://202.60.232.209:8080/admin?token=^<中继令牌^>
pause
