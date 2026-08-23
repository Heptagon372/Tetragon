@echo off
chcp 65001 >nul
title Tetragon - 위탁판매 자동화
cd /d "%~dp0"

echo.
echo  ==========================================
echo   Tetragon  위탁판매 대량등록 자동화
echo  ==========================================
echo.

REM ── 1. 필수 도구 확인 ────────────────────────────────────────────────
where dotnet >nul 2>&1
if errorlevel 1 (
    echo  [오류] .NET SDK 가 설치되어 있지 않습니다.
    echo         https://dotnet.microsoft.com/download 에서 .NET 8 SDK 를 설치하세요.
    goto :fail
)

where npm >nul 2>&1
if errorlevel 1 (
    echo  [오류] Node.js 가 설치되어 있지 않습니다.
    echo         https://nodejs.org 에서 설치하세요.
    goto :fail
)

REM ── 2. 이미 실행 중인 서버 정리 ──────────────────────────────────────
echo  [1/4] 기존 서버 확인 중...
powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 5080,5173 -State Listen -ErrorAction SilentlyContinue; if ($p) { $p | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }; Write-Host '        실행 중이던 서버를 종료했습니다.' } else { Write-Host '        실행 중인 서버가 없습니다.' }"
call :sleep 2

REM ── 3. 프론트엔드 의존성 ─────────────────────────────────────────────
if exist "frontend\node_modules" (
    echo  [2/4] 프론트엔드 패키지 확인 완료
) else (
    echo  [2/4] 프론트엔드 패키지 설치 중... ^(처음 한 번만, 1~2분 소요^)
    pushd frontend
    call npm install
    if errorlevel 1 (
        popd
        echo  [오류] npm install 에 실패했습니다.
        goto :fail
    )
    popd
)

REM ── 4. 서버 기동 ─────────────────────────────────────────────────────
echo  [3/4] 서버를 시작합니다...
start "Tetragon API (5080)" cmd /c "chcp 65001 >nul && dotnet run --project src\Tetragon.Api --urls http://localhost:5080"
start "Tetragon Web (5173)" cmd /c "chcp 65001 >nul && cd frontend && npm run dev"

REM ── 5. 준비될 때까지 대기 (최대 2분) ─────────────────────────────────
echo  [4/4] 서버 준비를 기다리는 중...
set /a TRY=0

:wait
set /a TRY+=1
powershell -NoProfile -Command "try { $a=(Invoke-WebRequest 'http://localhost:5080/api/v1/plugins' -TimeoutSec 2 -UseBasicParsing).StatusCode; $w=(Invoke-WebRequest 'http://localhost:5173' -TimeoutSec 2 -UseBasicParsing).StatusCode; if ($a -eq 200 -and $w -eq 200) { exit 0 }; exit 1 } catch { exit 1 }" >nul 2>&1
if not errorlevel 1 goto :ready
if %TRY% geq 40 goto :timeout
call :sleep 3
goto :wait

:ready
echo.
echo  ==========================================
echo   준비 완료
echo.
echo    화면   http://localhost:5173
echo    API    http://localhost:5080/swagger
echo  ==========================================
echo.
start "" http://localhost:5173
echo  브라우저를 열었습니다.
goto :done

:timeout
echo.
echo  [경고] 서버가 2분 안에 준비되지 않았습니다.
echo         'Tetragon API' / 'Tetragon Web' 창의 오류 메시지를 확인하세요.
echo         준비되면 http://localhost:5173 으로 접속할 수 있습니다.
goto :done

:done
echo.
echo  * 서버를 끄려면 stop.bat 을 실행하거나 두 검은 창을 닫으세요.
echo.
pause
exit /b 0

:fail
echo.
pause
exit /b 1

REM timeout 은 입력이 리디렉션된 환경에서 실패하므로 ping 으로 대기한다
:sleep
ping -n %~1 127.0.0.1 >nul 2>&1
exit /b 0
