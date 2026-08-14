using System.Net;
using System.Net.Sockets;
using System.Text;
using ScreenCastReceiver.Logging;

namespace ScreenCastReceiver.Helpers;

/// <summary>
/// UDP 广播通告服务（RTSP-WebRTC 备用镜像的安卓端发现机制）。
/// 周期向局域网广播：SCREENCAST-RTSP v1 ip=1.2.3.4 rtsp_port=8554 name=PC
/// 同时响应安卓端的单播 PING（当广播被路由器/防火墙拦截时仍可被发现）。
/// </summary>
public sealed class UdpBeaconServer : IDisposable
{
    public const string BeaconProtocol = "SCREENCAST-RTSP v1";
    private const int DefaultBeaconPort = 45678;

    private readonly AppLogger _log;
    private UdpClient? _udp;
    private CancellationTokenSource _cts = new();
    private Thread? _thread;
    private readonly object _lock = new();
    private string _payload = "";

    /// <summary>实际使用的广播端口（探测后顺延）。</summary>
    public int Port { get; private set; } = DefaultBeaconPort;

    public UdpBeaconServer(AppLogger log) => _log = log;

    public void Start(string ip, int rtspPort, string deviceName)
    {
        Stop();

        // 端口被占用时顺延（需求⑦）
        Port = PortProbe.FindFreeUdpPort(DefaultBeaconPort);

        var payload = $"{BeaconProtocol} ip={ip} rtsp_port={rtspPort} name={deviceName}";
        _payload = payload;
        lock (_lock)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.EnableBroadcast = true;
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        _cts = new CancellationTokenSource();
        _thread = new Thread(Loop) { IsBackground = true, Name = "UdpBeaconServer" };
        _thread.Start();
        _log.Info("[RTSP-WebRTC]", $"UDP 通告已启动, 广播端口={Port}, 内容={payload}");
    }

    private void Loop()
    {
        var token = _cts.Token;
        var broadcast = new IPEndPoint(IPAddress.Broadcast, Port);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytes = Encoding.UTF8.GetBytes(_payload);
                lock (_lock)
                {
                    _udp?.Send(bytes, bytes.Length, broadcast);
                }

                // 顺带接收单播 PING（发现模式 B：手机单播询问）
                _udp?.Client.Poll(200_000, SelectMode.SelectRead);
                lock (_lock)
                {
                    if (_udp?.Available > 0)
                    {
                        try
                        {
                            var remote = new IPEndPoint(IPAddress.Any, 0);
                            var data = _udp.Receive(ref remote);
                            var msg = Encoding.UTF8.GetString(data).Trim();
                            if (msg.Contains("PING"))
                            {
                                _udp.Send(bytes, bytes.Length, remote);
                                _log.Info("[RTSP-WebRTC]", $"响应发现 PING from {remote.Address}");
                            }
                        }
                        catch (SocketException) { /* 轮询间隙无数据属正常 */ }
                    }
                }

                for (var i = 0; i < 20 && !token.IsCancellationRequested; i++)
                    Thread.Sleep(100); // 每 2 秒广播一次
            }
        }
        catch (SocketException ex)
        {
            if (!token.IsCancellationRequested)
                _log.Warn("[RTSP-WebRTC]", $"UDP 通告异常（网络断开?）: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log.Error("[RTSP-WebRTC]", $"UDP 通告错误: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        lock (_lock)
        {
            try { _udp?.Close(); } catch { }
            _udp = null;
        }
        try { _thread?.Join(2000); } catch { }
        _thread = null;
    }

    public bool IsAlive => _udp != null && !_cts.IsCancellationRequested;

    public void Dispose() => Stop();
}
