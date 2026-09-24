@echo off
rem Relay round-trip speed check. Uploads one 64KB file, downloads it back, prints both rates.
rem Token comes from env RC_TOKEN (or set it inline below); server URL is baked in below.
rem Run it on the AGENT machine (or on the viewer machine - running it on both is the most informative).
setlocal
set "T=%RC_TOKEN%"
if "%T%"=="" set "T=REPLACE_ME_RELAY_TOKEN"
set "U=http://202.60.232.209:8080"
set "F=%TEMP%\rc_64k.bin"

fsutil file createnew "%F%" 65536 >nul 2>&1
if not exist "%F%" copy /y C:\Windows\System32\notepad.exe "%F%" >nul

echo [1/3] uploading 64KB to relay ...
curl -s -m 60 -F "file=@%F%" "%U%/api/upload?token=%T%" -o "%TEMP%\rc_up.txt"
set /p R=<"%TEMP%\rc_up.txt"
set "ID=%R:*fileId":"=%"
set "ID=%ID:~0,16%"
if "%ID%"=="" goto :fail
echo      fileId=%ID%

echo [2/3] downloading it back (this is the direction that matters) ...
curl -o NUL -m 90 -s -w "      DOWN  %%{speed_download} B/s   time %%{time_total}s   http %%{http_code}\n" "%U%/api/download/%ID%?token=%T%"

echo [3/3] upload rate for reference ...
curl -o NUL -m 60 -s -w "      UP    %%{speed_upload} B/s   time %%{time_total}s\n" -F "file=@%F%" "%U%/api/upload?token=%T%"

echo.
echo DOWN in the hundreds of KB/s  -^> relay-to-China is fine; only the relay-to-viewer
echo                                  path is bad. Fix with P2P direct (both ends in China).
echo DOWN only a few KB/s          -^> the relay's outbound to China is broken generally;
echo                                  move the relay to another host/line.
goto :done

:fail
echo      upload failed, server said: %R%
:done
endlocal
