# 原生 DLL 获取与编译说明（Windows 端）

本项目四个后台服务依赖以下 x64 原生 DLL，**均需用户自行获取/编译**（C# 侧已按下方接口契约写好 P/Invoke）。
DLL 就位后放入程序输出目录（`ScreenCastReceiver.exe` 同级）即可；缺失时对应服务会记录异常日志，
**不影响其它服务运行**（架构硬性要求①）。

| DLL 文件 | 用途 | 对应 C# 封装 | 契约头文件 |
|---|---|---|---|
| `RPiPlay.dll` | AirPlay2（iOS 投屏+镜像） | `Native/RPiPlayNative.cs` | `libs/RPiPlay-dll/export_interface.h` |
| `Miracast.dll` | Miracast（安卓原生系统镜像） | `Native/MiracastNative.cs` | `libs/Miracast-dll/export_interface.h` |
| `go2rtc.dll` | RTSP/WebRTC 备用镜像接收 | `Native/Go2RtcNative.cs` | `libs/go2rtc-bridge/bridge.go` |
| `mpv-1.dll` / `mpv-2.dll` | libmpv 渲染（Mpv.NET 封装使用） | `Player/MpvSessionManager.cs` | 官方 shinchiro 构建 |

---

## 1. mpv-1.dll / mpv-2.dll（必需，否则画面无法渲染）

1. 前往 mpv 官方 Windows 构建站：https://sourceforge.net/projects/mpv-player-windows/files/
   （或 https://mpv.srsf.com.cn/ 国内镜像），下载 **64bit** 构建。
2. 解压后把 `mpv-1.dll`（旧版）或 `mpv-2.dll`（新版）复制到程序输出目录。
3. 程序会自动依次尝试 `mpv-1.dll → mpv-2.dll → lib\mpv-1.dll → lib\mpv-2.dll`。

> 说明：Mpv.NET (1.1.1) 是 libmpv 的薄封装，本工程只使用其稳定公开 API：
> `MpvPlayer(IntPtr hwnd, string libMpvPath)`、`Load/Resume/Pause/Stop`、`Position/Duration/Volume`、
> `API.Command("set","video-rotate",…)`。

## 2. RPiPlay.dll（AirPlay2）

基于开源 [FD-/RPiPlay](https://github.com/FD-/RPiPlay)（AirPlay 镜像接收器）编译 Windows x64 DLL。

步骤：
1. 克隆 `https://github.com/FD-/RPiPlay.git`，用 Visual Studio 打开并配置为 **x64 Release**。
   原生依赖：libplist、pthread（Windows 可用 [pthreads-win32](https://github.com/GerHobbelt/pthread-win32)）。
2. 在 `lib/` 侧新增导出层（参考本目录 `export_interface.h`）：
   - 将 `raop.c` / `stream.c` / `playback.c` 编译为 DLL，并把解码后的 **H.264 Annex-B 字节流**
     经 `OnVideoSample` 回调透传给 C#（不要解码成 RGB，C# 直接送 MPV 渲染）；
   - 音频（ALAC/AAC）经 `OnAudioSample` 回调透传（v1 只记录日志，不混音）。
3. 实现 `rpiplay_start/stop` 时保证：`stop` **阻塞 Join 全部内部线程**后再返回（防止端口残留）。
4. 产出 `RPiPlay.dll` 复制到程序输出目录。

> 已知边界：iOS 视频 APP 的“URL 播放投屏（FairPlay DRM）”RPiPlay 不实现；
> 本模块提供 iOS **整机屏幕镜像**及视频会话镜像投屏。文档见工程根 `使用说明.md`。

## 3. Miracast.dll（Miracast 系统镜像）

基于开源 Miracast-Windows 接收端源码编译 x64 DLL。

候选源码（任选其一，按 `export_interface.h` 适配导出）：
- [HuntCode/Miracast_Sink_Windows](https://github.com/HuntCode/Miracast_Sink_Windows)
  （C++，libhv + TS 解复用，UDP RTP 接收，接口最接近）
- 其它实现 Wi-Fi Display 接收的 Windows 项目（注意区分：真 Miracast 走 Wi-Fi Direct 连接，
  依赖网卡 Wi-Fi Display Sink 硬件；UDP 7250 版本不依赖该硬件）

要求：
1. 输出 **MPEG-TS 封装流**（H264+AAC）经 `OnTsPacket` 回调透传（C# 直接送 MPV，自动识别 mpegts）。
2. `miracast_stop` 必须阻塞等待内部线程退出后再返回（防止端口残留）。
3. 编译为 x64 DLL，导出符号与 `Native/MiracastNative.cs` 的 `[DllImport]` 完全一致。

> 硬件检测：程序内置 `MiracastHardwareDetector`，通过
> `netsh wlan show drivers interface="xxx"` 的“无线显示: 支持/不支持”字段判断，
> 并用 NativeWifi API 二次校验。检测结果仅展示在 GUI 与日志，**不拦截**手动开启服务。

## 4. go2rtc.dll（RTSP-WebRTC 备用镜像）

基于开源 [AlexxIT/go2rtc](https://github.com/AlexxIT/go2rtc)，用 Go 的 c-shared 模式编译为 DLL。

编译方法（Go 1.20+，需要 mingw-w64 或安装过 MSVC 的环境）：

```powershell
git clone https://github.com/AlexxIT/go2rtc
cd go2rtc
# 把 libs/go2rtc-bridge/bridge.go 放到 cmd/bridge/ 目录，并编写 go.mod 引用 go2rtc 核心
cd cmd/bridge
CGO_ENABLED=1 GOOS=windows GOARCH=amd64 go build -buildmode=c-shared -o go2rtc.dll
```

产出 `go2rtc.dll` 复制到程序输出目录。

> 说明：`bridge.go` 中的 `core.Start(ctx, config)` 为 go2rtc 内部 API，不同版本函数签名可能微调，
> 请按所克隆版本调整（核心逻辑不变：解析 JSON 配置 → 启动 → 阻塞 → stop 时 cancel 等待退出）。

## 5. 端口契约速查

| 服务 | 默认端口 | 占用时行为 |
|---|---|---|
| DLNA SSDP | UDP 1900（固定） | 日志明确提示，HTTP 控制端口从 49152 起顺延 |
| DLNA HTTP/SOAP | TCP 49152+ 顺延 | 自动顺延 |
| AirPlay2 | TCP 5000 起顺延 | 自动顺延 |
| Miracast RTP | UDP 7250 起顺延 | 自动顺延 |
| RTSP | TCP 8554 起顺延 | 自动顺延 |
| WebRTC | TCP 8555 起顺延 | 自动顺延 |
| UDP 广播通告 | UDP 45678 起顺延 | 自动顺延 |
