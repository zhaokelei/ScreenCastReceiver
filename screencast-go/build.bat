@echo off
REM ================================================================
REM   ScreenCastReceiver Go 版一键构建脚本
REM   特点：零第三方依赖（纯 syscall 调 Win32 API），
REM   输出：单个 ScreenCastReceiver.exe（无控制台窗口、去符号、体积极小）
REM ================================================================
chcp 65001 >nul
setlocal

REM 国内Go镜像（访问不到proxy.golang.org时自动回退）
set GOPROXY=https://goproxy.cn,direct
set CGO_ENABLED=0
set GOOS=windows
set GOARCH=amd64

echo [1/4] 清理旧产物...
if exist ScreenCastReceiver.exe  del /F /Q ScreenCastReceiver.exe
if exist go.sum          del /F /Q go.sum 2>nul

echo [2/4] 整理模块依赖...
go mod tidy 2>nul

echo [3/4] 编译单 EXE（无控制台+去符号+小体积）...
REM 说明：
REM   -ldflags="-s -w" : 去掉DWARF调试/符号表，减小~50%体积
REM   -H windowsgui    : Win32 GUI子系统，启动时不闪控制台黑框
REM   -trimpath        : 去除源码绝对路径，信息脱敏
go build -o ScreenCastReceiver.exe ^
  -trimpath ^
  -ldflags="-s -w -H windowsgui" .

if %ERRORLEVEL% NEQ 0 (
  echo.
  echo [X] 编译失败！错误码=%ERRORLEVEL%
  pause
  exit /b 1
)

echo [4/4] 输出大小统计：
for %%I in (ScreenCastReceiver.exe) do echo     ScreenCastReceiver.exe = %%~zI 字节（约 %%~zI/1024/1024 MB）

echo.
echo ✅ 编译完成！将 ScreenCastReceiver.exe 与 mpv\ 目录（含 mpv.exe）放同一目录即可直接运行。
echo    （若仅验证功能：可以不带mpv先启动GUI，勾选DLNA后会友好提示缺失mpv）
pause
endlocal
