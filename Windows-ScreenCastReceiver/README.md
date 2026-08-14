# ScreenCastReceiver — Windows 投屏接收端

C# WPF .NET 8 (x64)，适配 Win10/Win11。四大独立后台服务：DLNA-DMR / AirPlay2 / Miracast / RTSP-WebRTC 备用镜像。

## 目录结构

```
Windows-ScreenCastReceiver/
├── ScreenCastReceiver.sln
├── src/ScreenCastReceiver/
│   ├── ScreenCastReceiver.csproj        # .NET8 WPF，x64，NuGet: Mpv.NET 1.1.1
│   ├── app.manifest
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)             # GUI 逻辑
│   ├── Models/ServiceModels.cs          # ServiceKind/ServiceStatus/事件参数
│   ├── Logging/AppLogger.cs             # 线程安全日志（带 [DLNA] 等来源标签 + 落盘）
│   ├── Native/                          # 三个原生 DLL 的 P/Invoke 封装
│   │   ├── RPiPlayNative.cs
│   │   ├── MiracastNative.cs
│   │   └── Go2RtcNative.cs
│   ├── Services/
│   │   ├── ScreenCastServiceBase.cs     # 取消令牌/休眠唤醒/网络变化自动停止
│   │   ├── DlnaDmrService.cs            # DLNA 服务类（独立）
│   │   ├── DmrUpnpServer.cs             # 纯 C# UPnP/DLNA DMR 协议栈（SSDP+SOAP）
│   │   ├── AirPlayBridgeService.cs      # AirPlay2 桥接（RPiPlay.dll）
│   │   ├── MiracastBridgeService.cs     # Miracast 桥接（Miracast.dll）
│   │   └── RtspWebRtcMirrorService.cs   # RTSP/WebRTC 备用镜像（go2rtc.dll）
│   ├── Detection/MiracastHardwareDetector.cs  # netsh + NativeWifi 硬件检测
│   ├── Helpers/
│   │   ├── NetworkHelper.cs             # 网卡枚举（物理/虚拟区分）+ BindConfig
│   │   ├── PortProbe.cs                 # 端口自动探测顺延
│   │   ├── FirewallHelper.cs            # netsh 防火墙规则
│   │   └── UdpBeaconServer.cs           # RTSP 服务的局域网广播通告
│   ├── Player/
│   │   ├── MpvSessionManager.cs         # MPV 会话隔离 + 抢占确认 + 旋转
│   │   ├── LocalStreamForwarder.cs      # DLL 回调字节流 → 本地TCP → MPV
│   │   └── Win32HwndHost.cs             # WPF 原生窗口宿主
│   └── runtimes/                        # 放置 RPiPlay.dll/Miracast.dll/go2rtc.dll/mpv-1.dll
├── libs/                                # DLL 获取/编译说明 + C 契约头文件 + go 桥接
│   ├── README.md
│   ├── RPiPlay-dll/export_interface.h
│   ├── Miracast-dll/export_interface.h
│   └── go2rtc-bridge/bridge.go
└── README.md
```

## NuGet 依赖清单

| 包 | 版本 | 用途 |
|---|---|---|
| Mpv.NET | 1.1.1 | libmpv 播放器封装（Load/Resume/Pause/Stop/Seek/旋转） |

> DLNA-DMR 未使用 Open.UPnP（该包在 NuGet 已不可用，404），改用纯 C# 实现的等价 UPnP 协议栈
> （SSDP 发现 + 设备描述 + SOAP 控制），功能完整且保证可编译。

## 编译

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 x64 目标）。
2. `dotnet restore` → `dotnet build -c Release -p:Platform=x64`，或用 Visual Studio 2022 打开 sln。
3. 按 `libs/README.md` 获取四个原生 DLL 放入输出目录（缺哪个服务就停哪个，其余照常运行）。

## 运行

- 启动后勾选所需服务；DLNA 设备名称可在输入框自定义。
- 日志区可见各服务实际监听端口、设备接入/断开、Miracast 硬件检测结果。
- 新投屏接入且已有画面在播放时，会弹窗询问是否抢占。
- 软件退出时自动：停止四个服务 → 等待 DLL 内部线程退出 → 释放全部端口 → 退出。
