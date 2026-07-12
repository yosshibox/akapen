@echo off
setlocal enabledelayedexpansion

REM Akapen Windows probe -- interactive launcher.
REM
REM Builds akapen.dll (Rust) + AkapenProbe.dll (.NET), copies the native DLL
REM next to the managed one so the P/Invoke resolver finds it, then runs the
REM probe. Any command-line arguments (image path, output dir) are forwarded
REM to the probe with %*.
REM
REM Run from the repository root:
REM     apps\windows-probe\run.bat                 (blank canvas)
REM     apps\windows-probe\run.bat path\to\img.png (open an image)
REM
REM This is the throwaway de-risk spike (see README.md), not the future
REM WinUI 3 shell -- kept as raw user32.dll + hand-written DllImport.

REM Locate the repo root (this script lives at <root>\apps\windows-probe\run.bat).
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%..\.."
set "REPO_ROOT=%CD%"

echo [run] repo root: %REPO_ROOT%
echo [run] cargo build -p akapen-ffi --release
cargo build -p akapen-ffi --release
if errorlevel 1 (
    echo [run] FAIL: cargo build failed
    popd
    exit /b 1
)

echo [run] dotnet build -c Release apps\windows-probe\AkapenProbe
dotnet build -c Release apps\windows-probe\AkapenProbe\AkapenProbe.csproj
if errorlevel 1 (
    echo [run] FAIL: dotnet build failed
    popd
    exit /b 1
)

set "OUT_DIR=%REPO_ROOT%\apps\windows-probe\AkapenProbe\bin\Release\net8.0"
echo [run] copy target\release\akapen.dll -^> %OUT_DIR%
copy /Y "%REPO_ROOT%\target\release\akapen.dll" "%OUT_DIR%\akapen.dll" >nul
if errorlevel 1 (
    echo [run] FAIL: could not copy akapen.dll (did cargo build actually produce it?)
    popd
    exit /b 1
)

echo [run] dotnet %OUT_DIR%\AkapenProbe.dll %*
dotnet "%OUT_DIR%\AkapenProbe.dll" %*
set "EXITCODE=%ERRORLEVEL%"
popd
exit /b %EXITCODE%
