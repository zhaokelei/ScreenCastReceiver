此目录用于存放随程序输出的原生 DLL（csproj 已配置把 runtimes\** 复制到输出目录）。

按 libs/README.md 获取/编译后，把以下文件放入本目录（或直接放入输出目录）：
- RPiPlay.dll      (AirPlay2)
- Miracast.dll     (Miracast)
- go2rtc.dll       (RTSP/WebRTC)
- mpv-1.dll 或 mpv-2.dll (libmpv，也可直接放输出目录)

缺失的 DLL 不影响程序编译与其它服务运行，对应服务会在日志中提示。
