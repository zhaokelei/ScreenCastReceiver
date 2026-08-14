using System.Net;
using System.Runtime.InteropServices;
using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Native;
using ScreenCastReceiver.Player;

namespace ScreenCastReceiver.Services;

/// <summary>
/// AirPlay2 桥接服务（需求②）：
/// - 复用开源 RPiPlay 编译的 Windows x64 DLL（RPiPlay.dll），P/Invoke 调用，不手写 AirPlay 协议
/// - 支持 iOS 视频投屏会话与整机屏幕镜像（同一套 AirPlay2 服务）
/// - 视频 H.264 帧经回调 → 本地回环 TCP → MPV 渲染
/// - 严格管理 DLL 生命周期：停止时 rpiplay_stop 阻塞等待 DLL 内部线程退出，再释放资源
/// </summary>
public sealed class AirPlayBridgeService : ScreenCastServiceBase
{
    private LocalStreamForwarder? _forwarder;
    private bool _nativeStarted;

    public AirPlayBridgeService(AppLogger log, MpvSessionManager mpv)
        : base(ServiceKind.AirPlay2, log, mpv)
    {
    }

    protected override Task StartCoreAsync(CancellationToken ct)
    {
        if (!RPiPlayNative.DllAvailable)
            throw new InvalidOperationException("未找到 RPiPlay.dll，请将其放到程序目录（获取方式见 libs/README.md）");

        // 端口探测（需求⑦）：AirPlay 默认 5000，被占用自动顺延
        var port = PortProbe.FindFreeTcpPort(5000);

        // 本地回环流转发：H264 帧 → MPV
        _forwarder = new LocalStreamForwarder();
        _forwarder.Start();

        var callbacks = new RPiPlayNative.RPiPlayCallbacks
        {
            OnVideoSample = (data, length, pts, keyFrame) =>
            {
                var buf = new byte[length];
                Marshal.Copy(data, buf, 0, length);
                _forwarder?.Write(buf, 0, length);
            },
            OnAudioSample = (data, length, pts, sampleRate, channels) =>
            {
                // v1 暂不混音，仅记录（避免中断视频流）
                // 后续如需伴音，可扩展为第二条本地流送 MPV 第二音频轨
            },
            OnMirrorState = state => Log.Info(Tag, state == 1 ? "iOS 屏幕镜像已开始" : "iOS 屏幕镜像已结束"),
            OnPlaybackState = state => Log.Info(Tag, $"iOS 播放控制: {state switch { 1 => "播放", 2 => "暂停", _ => "停止" }}"),
            OnSetVolume = vol => Log.Info(Tag, $"iOS 音量指令: {vol}"),
            OnText = textPtr =>
            {
                try
                {
                    var text = Marshal.PtrToStringUTF8(textPtr);
                    if (!string.IsNullOrEmpty(text)) Log.Info(Tag, text);
                }
                catch { /* 忽略文本回调异常 */ }
            },
            UserData = IntPtr.Zero
        };

        // 严格生命周期（需求②）：先初始化 → 再启动
        RPiPlayNative.Init(callbacks);
        RPiPlayNative.SetDeviceName(_deviceName);
        RPiPlayNative.Start(port);
        _nativeStarted = true;

        ListeningPort = port;

        // 把 DLL 回调的 H264 流接入 MPV（本地 tcp + lavf h264 解复用）
        var tcpUrl = $"tcp://127.0.0.1:{_forwarder.Port}";
        Log.Info(Tag, $"AirPlay2 服务已启动, 监听端口={port}, 镜像流将转发到 MPV ({tcpUrl})");
        _ = Mpv.RequestPlaybackLiveH264(ServiceKind.AirPlay2, tcpUrl);

        return Task.CompletedTask;
    }

    protected override Task StopCoreAsync()
    {
        // 先停 DLL（阻塞等待其内部线程退出，防止端口残留），再释放托管资源
        if (_nativeStarted)
        {
            try { RPiPlayNative.Stop(); }
            catch (Exception ex) { Log.Warn(Tag, $"DLL 停止异常: {ex.Message}"); }
            _nativeStarted = false;
        }

        try { _forwarder?.Dispose(); _forwarder = null; }
        catch { }

        // 确认端口已释放（最长等待 5 秒）
        if (ListeningPort > 0 && PortProbe.IsTcpPortInUse(ListeningPort))
            Log.Warn(Tag, $"端口 {ListeningPort} 可能仍有残留占用，请稍后检查");
        return Task.CompletedTask;
    }

    protected override bool ValidateSockets()
        => _nativeStarted && _forwarder != null && _forwarder.Port > 0;

    private string _deviceName = "ScreenCastReceiver-AirPlay";

    /// <summary>程序退出前调用：强制释放 DLL。</summary>
    public void FreeNative()
    {
        try { RPiPlayNative.Stop(); } catch { }
        RPiPlayNative.Free();
    }

    public override void Dispose()
    {
        try { RPiPlayNative.Stop(); } catch { }
        RPiPlayNative.Free();
        try { _forwarder?.Dispose(); } catch { }
        base.Dispose();
    }
}
