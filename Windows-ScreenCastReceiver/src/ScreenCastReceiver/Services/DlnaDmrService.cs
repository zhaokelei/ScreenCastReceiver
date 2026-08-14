using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Player;

namespace ScreenCastReceiver.Services;

/// <summary>
/// DLNA-DMR 服务（需求⑥）：独立服务类，使用 DmrUpnpServer 实现 UPnP/DLNA 渲染器。
/// - 支持 GUI 自定义 DLNA 设备显示名称
/// - 收到视频播放 URL → 交给 MPV 播放
/// - 支持手机端 pause / stop / seek 播放控制
/// - 可单独开启/关闭，与镜像服务完全解耦
/// </summary>
public sealed class DlnaDmrService : ScreenCastServiceBase
{
    private DmrUpnpServer? _server;
    private string _deviceName = "Xiaolei DLAN";

    /// <summary>用户指定的 DLNA HTTP 端口（0 表示自动探测）。</summary>
    public int Port { get; set; }

    public DlnaDmrService(AppLogger log, MpvSessionManager mpv)
        : base(ServiceKind.Dlna, log, mpv)
    {
    }

    /// <summary>设置 DLNA 设备显示名称（GUI 输入框，启动前调用）。</summary>
    public void SetDeviceName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _deviceName = name.Trim();
    }

    protected override Task StartCoreAsync(CancellationToken ct)
    {
        // 关键端口探测（需求⑦）：HTTP 控制端口默认从 49152 起顺延；用户指定端口时优先使用
        var httpPort = Port > 0 && Port < 65536 ? Port : PortProbe.FindFreeTcpPort(49152);
        var bindIps = BindConfig.GetBindAddresses();

        _server = new DmrUpnpServer(Log)
        {
            PositionProvider = () =>
            {
                var (pos, dur) = Mpv.GetPlaybackPosition(ServiceKind.Dlna);
                return (pos, dur);
            },
            TransportStateProvider = () => Mpv.IsPlaying(ServiceKind.Dlna) ? 1 : 0
        };

        // DLNA 控制指令 → MPV（需求①：暂停/继续/停止/快进快退）
        _server.SetUri += (uri, meta) =>
        {
            Log.Info(Tag, $"收到视频播放链接: {uri}");
            _ = Mpv.RequestPlayback(ServiceKind.Dlna, uri);
        };
        _server.Play += () => Mpv.Resume(ServiceKind.Dlna);
        _server.Pause += () => Mpv.Pause(ServiceKind.Dlna);
        _server.Stop += () => Mpv.Stop(ServiceKind.Dlna);
        _server.Seek += seconds => Mpv.Seek(ServiceKind.Dlna, seconds);
        _server.Next += () => Mpv.Seek(ServiceKind.Dlna, Mpv.GetPlaybackPosition(ServiceKind.Dlna).Position + 30);
        _server.Previous += () => Mpv.Seek(ServiceKind.Dlna, Math.Max(0, Mpv.GetPlaybackPosition(ServiceKind.Dlna).Position - 30));

        _server.Start(_deviceName, httpPort, 1900, bindIps.ToArray());
        ListeningPort = _server.HttpPort;
        return Task.CompletedTask;
    }

    protected override Task StopCoreAsync()
    {
        try { _server?.StopServer(); } catch (Exception ex) { Log.Warn(Tag, $"停止异常: {ex.Message}"); }
        _server = null;
        return Task.CompletedTask;
    }

    protected override bool ValidateSockets() => _server?.IsAlive ?? false;

    public override void Dispose()
    {
        _server?.StopServer();
        base.Dispose();
    }
}
