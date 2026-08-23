@echo off
chcp 65001 >nul
title Tetragon 종료
cd /d "%~dp0"

echo.
echo  Tetragon 서버를 종료합니다...
echo.

powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 5080,5173 -State Listen -ErrorAction SilentlyContinue; if ($p) { $p | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { $n = (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName; Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue; Write-Host \"   종료: $n (PID $_)\" } } else { Write-Host '   실행 중인 서버가 없습니다.' }"

echo.
echo  완료했습니다.
ping -n 3 127.0.0.1 >nul 2>&1
