@echo off
setlocal enabledelayedexpansion

REM Akapen Windows MVP -- self-contained interactive launcher.
REM
REM Builds akapen.dll (Rust) + a self-contained win-x64 .NET host. The result
REM has no WinUI/Windows App Runtime or separately installed .NET dependency.
REM
REM Run from the repository root:
REM     apps\windows-probe\run.bat                 (blank canvas)
REM     apps\windows-probe\run.bat path\to\img.png (open an image)
REM     apps\windows-probe\run.bat --headless-export [out-dir]
REM     apps\windows-probe\run.bat --interactive-smoke [out-dir]
REM     apps\windows-probe\run.bat --presentation-smoke
REM

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

echo [run] dotnet publish self-contained win-x64
dotnet publish -c Release -r win-x64 --self-contained true apps\windows-probe\AkapenProbe\AkapenProbe.csproj
if errorlevel 1 (
    echo [run] FAIL: dotnet build failed
    popd
    exit /b 1
)

set "OUT_DIR=%REPO_ROOT%\apps\windows-probe\AkapenProbe\bin\Release\net8.0\win-x64\publish"
echo [run] copy target\release\akapen.dll -^> %OUT_DIR%\akapen_native.dll
copy /Y "%REPO_ROOT%\target\release\akapen.dll" "%OUT_DIR%\akapen_native.dll" >nul
if errorlevel 1 (
    echo [run] FAIL: could not copy akapen_native.dll (did cargo build actually produce it?)
    popd
    exit /b 1
)

echo [run] %OUT_DIR%\Akapen.exe %*
"%OUT_DIR%\Akapen.exe" %*
set "EXITCODE=%ERRORLEVEL%"
popd
exit /b %EXITCODE%
