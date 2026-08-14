using System.Net;
using System.Text.Json;
using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Native;
using ScreenCastReceiver.Player;

namespace ScreenCastReceiver.Services;

/// <summary>
/// RTSP-WebRTC 备用安卓镜像服务（需求⑤）：
/// - 集成 go2rtc 编译的 Windows x64 DLL（go2rtc.dll），P/Invoke 调用
/// - go2rtc 作为 RTSP 服务端监听端口，接收安卓端（Android-ScreenPush APP）
///   推上来的 H264/H265+AAC 音视频流（rtsp://PC-IP:port/screen），输出流交给 MPV 渲染
/// - 完全不依赖 Wi-Fi Display 硬件，有线网卡也可以工作
/// - 附带 UDP 广播通告，供安卓端自动发现本机 RTSP 服务地址
/// - 该模式无法被安卓系统投屏按钮搜索到（需要配合专用 APP 推流）
/// </summary>
public sealed class RtspWebRtcMirrorService : ScreenCastServiceBase
{
    private UdpBeaconServer? _beacon;
    private bool _nativeStarted;
    private int _rtspPort;
    private int _webrtcPort;
    private string _deviceName = "ScreenCastReceiver";

    public RtspWebRtcMirrorService(AppLogger log, MpvSessionManager mpv)
        : base(ServiceKind.RtspWebRtc, log, mpv)
    {
    }

    public void SetDeviceName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _deviceName = name.Trim();
    }

    protected override Task StartCoreAsync(CancellationToken ct)
    {
        if (!Go2RtcNative.DllAvailable)
            throw new InvalidOperationException("未找到 go2rtc.dll，请将其放到程序目录（获取方式见 libs/README.md）");

        // 端口探测（需求⑦）：RTSP 8554 / WebRTC 8555 / API 1984，被占用自动顺延
        _rtspPort = PortProbe.FindFreeTcpPort(8554);
        _webrtcPort = PortProbe.FindFreeTcpPort(8555);
        var apiPort = PortProbe.FindFreeTcpPort(1984);

        var config = new
        {
            log = new { level = "warn" },
            rtsp = new { listen = $":{_rtspPort}" },
            webrtc = new { listen = $":{_webrtcPort}" },
            api = new { listen = $"127.0.0.1:{apiPort}" }
        };
        var configJson = JsonSerializer.Serialize(config);

        Go2RtcNative.Start(configJson);
        _nativeStarted = true;
        ListeningPort = _rtspPort;

        // 安卓端推流地址: rtsp://<本机IP>:<rtspPort>/screen
        var lanIp = BindConfig.GetFirstLanIpv4();
        Log.Info(Tag, $"go2rtc 已启动, RTSP端口={_rtspPort}, WebRTC端口={_webrtcPort}, " +
                      $"安卓推流地址: rtsp://{lanIp}:{_rtspPort}/screen");

        // UDP 广播通告（安卓端自动发现）
        _beacon = new UdpBeaconServer(Log);
        if (lanIp != null)
            _beacon.Start(lanIp.ToString(), _rtspPort, _deviceName);
        else
            Log.Warn(Tag, "未找到局域网 IP，UDP 广播通告未启动");

        // 输出流交给 MPV 渲染（本机回环 RTSP 地址）
        var localRtsp = $"rtsp://127.0.0.1:{_rtspPort}/screen";
        _ = Mpv.RequestPlayback(ServiceKind.RtspWebRtc, localRtsp);
        Log.Info(Tag, "等待安卓端推流接入...");

        return Task.CompletedTask;
    }

    protected override Task StopCoreAsync()
    {
        // 先停 go2rtc DLL（阻塞等待内部协程/端口退出），再停广播
        if (_nativeStarted)
        {
            try { Go2RtcNative.Stop(); }
            catch (Exception ex) { Log.Warn(Tag, $"DLL 停止异常: {ex.Message}"); }
            _nativeStarted = false;
        }

        try { _beacon?.Stop(); _beacon = null; }
        catch { }

        Mpv.Stop(ServiceKind.RtspWebRtc);

        if (_rtspPort > 0 && PortProbe.IsTcpPortInUse(_rtspPort))
            Log.Warn(Tag, $"RTSP 端口 {_rtspPort} 可能仍有残留占用，请稍后检查");
        return Task.CompletedTask;
    }

    protected override bool ValidateSockets()
        => _nativeStarted && Go2RtcNative.IsRunning();

    public override void Dispose()
    {
        try { Go2RtcNative.Stop(); } catch { }
        try { _beacon?.Dispose(); } catch { }
        base.Dispose();
    }
}
