# Xiaolei DLAN

Windows 桌面 DLNA 投屏接收端。手机/平板通过 DLNA（UPnP AV）发现本机后即可投屏播放视频，播放内核为独立的 [mpv](https://mpv.io/) 进程（命名管道 IPC 控制）。

## 功能特性

- **DLNA 接收**：自动发现并注册为 DLNA 数字媒体接收器（DMR），手机端可直接投屏
- **软解 / 硬解一键切换**：通过 mpv `hwdec` 属性即时切换，切换过程不中断播放、不中断 DLNA 连接
- **画面比例**：自适应 / 原始 / 拉伸 / 等比缩放，全屏播放
- **自定义设备名与端口**：修改后自动保存（`settings.json`），下次启动自动读取；端口 `0` 表示自动分配
- **内存日志**：纯内存环形缓冲（最多 2000 条），日志面板仅保留最近 1000 行，不写磁盘、不占额外空间
- **自定义端口配置**：`ports.json`（DLNA 端口自定义）

## 环境要求

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）
- 运行时目录下需存在 `mpv\mpv.exe`（播放内核，约 113 MB，超出 GitHub 单文件 100 MB 限制，故不随仓库分发）

## 构建

```powershell
# 恢复并发布
dotnet restore ScreenCastReceiver.sln
dotnet publish src/ScreenCastReceiver/ScreenCastReceiver.csproj -c Release -o publish/out

# 将 mpv 播放器放入输出目录
#   publish/out/mpv/mpv.exe
```

### 制作安装包（Inno Setup 7）

```powershell
& "C:\Program Files\Inno Setup 7\ISCC.exe" installer\XiaoleiDLAN.iss
```

产物输出到 `publish\XiaoleiDLAN-Setup-<版本>.exe`。

### GitHub Actions 一键编译

仓库已配置 `.github/workflows/build.yml`：push / 手动触发（Actions 页面 → Run workflow）后，自动完成编译、下载最新 mpv-win64、并上传 `XiaoleiDLAN-win-x64` 构建产物。

## 目录结构

```
src/ScreenCastReceiver/   WPF 主程序源码
  ├─ Player/              mpv 会话管理（IPC、软硬解切换）
  ├─ Services/            DLNA DMR 服务（发现、HTTP、UPnP）
  ├─ Logging/             纯内存日志
  └─ Helpers/             设置持久化（settings.json / ports.json）
tests/                    DLNA 相关单元测试
installer/                Inno Setup 安装脚本
libs/                     运行时依赖说明（mpv-dll 需自行放置）
```

## License

MIT
