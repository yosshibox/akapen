@echo off
setlocal

REM Windows MVP launcher. Run this from an interactive desktop session.
REM SSH Session 0 cannot create a WinUI SwapChainPanel.

pushd "%~dp0..\.."
set "ROOT=%CD%"
set "SHELL_DIR=%ROOT%\apps\windows\AkapenApp"
set "OUT=%SHELL_DIR%\publish-win-x64"

echo [mvp] Building Rust FFI...
cargo build -p akapen-ffi --release
if errorlevel 1 goto :fail

echo [mvp] Regenerating C# binding...
cargo run -p akapen-dotnet-bindgen
if errorlevel 1 goto :fail

echo [mvp] Publishing framework-dependent WinUI shell...
dotnet publish "%SHELL_DIR%\AkapenApp.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -o "%OUT%"
if errorlevel 1 goto :fail

echo [mvp] Copying native DLL...
copy /Y "%ROOT%\target\release\akapen.dll" "%OUT%\akapen.dll" >nul
if errorlevel 1 goto :fail

echo [mvp] Starting AkapenApp.exe in the interactive desktop session...
"%OUT%\AkapenApp.exe"
set "EXITCODE=%ERRORLEVEL%"
popd
exit /b %EXITCODE%

:fail
echo [mvp] FAILED with errorlevel %ERRORLEVEL%.
popd
exit /b 1
