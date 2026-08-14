using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenCastReceiver.Helpers;

/// <summary>
/// 端口探测工具（需求⑦：禁止硬编码绑定死端口，被占用自动顺延并记录实际监听端口）。
/// </summary>
public static class PortProbe
{
    /// <summary>从 preferredStart 开始探测一个空闲 TCP 端口，最多尝试 count 个。</summary>
    public static int FindFreeTcpPort(int preferredStart = 0, int count = 200)
    {
        var start = preferredStart <= 0 ? 1024 : preferredStart;
        for (var p = start; p < start + count; p++)
        {
            if (!IsTcpPortInUse(p)) return p;
        }
        // 全部被占用则交给系统分配临时端口
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>从 preferredStart 开始探测一个空闲 UDP 端口。</summary>
    public static int FindFreeUdpPort(int preferredStart = 0, int count = 200)
    {
        var start = preferredStart <= 0 ? 1024 : preferredStart;
        for (var p = start; p < start + count; p++)
        {
            if (!IsUdpPortInUse(p)) return p;
        }
        return start;
    }

    /// <summary>探测 TCP 端口是否被占用。</summary>
    public static bool IsTcpPortInUse(int port)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            if (props.GetActiveTcpListeners().Any(e => e.Port == port)) return true;
            if (props.GetActiveTcpConnections().Any(e => e.LocalEndPoint.Port == port)) return true;
        }
        catch { /* 权限不足等场景降级处理 */ }
        return false;
    }

    /// <summary>探测 UDP 端口是否被占用（本机任意地址绑定尝试）。</summary>
    public static bool IsUdpPortInUse(int port)
    {
        try
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            client.Close();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
