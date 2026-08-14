using System.Runtime.InteropServices;
using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Native;
using ScreenCastReceiver.Player;

namespace ScreenCastReceiver.Services;

/// <summary>
/// Miracast 桥接服务（需求③）：
/// - 复用开源 Miracast-Windows 编译的 x64 DLL（Miracast.dll），P/Invoke 调用，不手写 Wi-Fi Display 协议
/// - 接收安卓原生系统屏幕镜像（依赖网卡 Wi-Fi Display Sink 硬件，硬件状态由
///   MiracastHardwareDetector 检测并展示，硬件不支持时仍允许用户手动开启）
/// - TS 流经回调 → 本地回环 TCP → MPV 渲染
/// - 严格管理 DLL 生命周期：停止时 miracast_stop 阻塞等待 DLL 内部线程退出
/// </summary>
public sealed class MiracastBridgeService : ScreenCastServiceBase
{
    private LocalStreamForwarder? _forwarder;
    private bool _nativeStarted;

    public MiracastBridgeService(AppLogger log, MpvSessionManager mpv)
        : base(ServiceKind.Miracast, log, mpv)
    {
    }

    protected override Task StartCoreAsync(CancellationToken ct)
    {
        if (!MiracastNative.DllAvailable)
            throw new InvalidOperationException("未找到 Miracast.dll，请将其放到程序目录（获取方式见 libs/README.md）");

        // Wi-Fi Display 规范：RTSP TCP 7236 / RTP UDP 7250，被占用自动顺延（需求⑦）
        var udpPort = PortProbe.FindFreeUdpPort(7250);

        _forwarder = new LocalStreamForwarder();
        _forwarder.Start();

        var callbacks = new MiracastNative.MiracastCallbacks
        {
            OnTsPacket = (data, length) =>
            {
                var buf = new byte[length];
                Marshal.Copy(data, buf, 0, length);
                _forwarder?.Write(buf, 0, length);
            },
            OnState = state => Log.Info(Tag, state switch
            {
                0 => "投屏设备已断开",
                1 => "投屏设备已连接",
                _ => "投屏媒体流已启动"
            }),
            OnText = textPtr =>
            {
                try
                {
                    var text = Marshal.PtrToStringUTF8(textPtr);
                    if (!string.IsNullOrEmpty(text)) Log.Info(Tag, text);
                }
                catch { }
            },
            UserData = IntPtr.Zero
        };

        MiracastNative.Init(callbacks);
        MiracastNative.Start(udpPort);
        _nativeStarted = true;

        ListeningPort = udpPort;

        // Miracast DLL 输出 MPEG-TS 封装流 → MPV 自动识别（lavf mpegts）
        var tcpUrl = $"tcp://127.0.0.1:{_forwarder.Port}";
        Log.Info(Tag, $"Miracast 服务已启动, RTP 接收端口={udpPort}, 投屏流将转发到 MPV");
        _ = Mpv.RequestPlaybackLiveTs(ServiceKind.Miracast, tcpUrl);

        return Task.CompletedTask;
    }

    protected override Task StopCoreAsync()
    {
        // 先停 DLL（阻塞等待内部线程退出，防止端口残留），再释放托管资源
        if (_nativeStarted)
        {
            try { MiracastNative.Stop(); }
            catch (Exception ex) { Log.Warn(Tag, $"DLL 停止异常: {ex.Message}"); }
            _nativeStarted = false;
        }

        try { _forwarder?.Dispose(); _forwarder = null; }
        catch { }

        if (ListeningPort > 0 && PortProbe.IsUdpPortInUse(ListeningPort))
            Log.Warn(Tag, $"UDP 端口 {ListeningPort} 可能仍有残留占用，请稍后检查");
        return Task.CompletedTask;
    }

    protected override bool ValidateSockets()
        => _nativeStarted && _forwarder != null && _forwarder.Port > 0;

    public override void Dispose()
    {
        try { MiracastNative.Stop(); } catch { }
        MiracastNative.Free();
        try { _forwarder?.Dispose(); } catch { }
        base.Dispose();
    }
}
