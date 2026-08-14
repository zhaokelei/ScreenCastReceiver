using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenCastReceiver.Helpers;

/// <summary>网卡信息。</summary>
public sealed class NetAdapterInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsPhysical { get; init; }
    public required List<IPAddress> Ipv4Addresses { get; init; }

    public override string ToString() => $"{Name} ({Description})";
}

/// <summary>
/// 网卡辅助类：
/// - 枚举网卡并区分【物理网卡】/【虚拟/VPN/WSL网卡】
/// - 根据勾选结果计算要绑定的 IP 列表
/// - 提供本机局域网 IPv4（用于显示/广播）
/// </summary>
public static class NetworkHelper
{
    // 虚拟/VPN/WSL 网卡识别关键词（名称或描述命中即视为非物理网卡）
    private static readonly string[] VirtualKeywords =
    {
        "virtual", "vpn", "wsl", "hyper-v", "vethernet", "tap-", "tun", "loopback",
        "vmware", "virtualbox", "hamachi", "zerotier", "tailscale", "wireguard", "nordvpn",
        "windscribe", "surfshark", "pia", "expressvpn", "pan", "bluetooth", "wintun",
        "虚拟", "以太网适配器", "隧道", "本地连接"
    };

    /// <summary>获取全部 IPv4 网卡（按物理/虚拟分组）。</summary>
    public static (List<NetAdapterInfo> Physical, List<NetAdapterInfo> Virtual) GetAllAdapters()
    {
        var physical = new List<NetAdapterInfo>();
        var virtuals = new List<NetAdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipv4 = ni.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(u.Address))
                .Select(u => u.Address)
                .ToList();
            if (ipv4.Count == 0) continue;

            var desc = $"{ni.Name} | {ni.Description}";
            var isPhysical = IsPhysicalAdapter(ni.Name, ni.Description, ni.NetworkInterfaceType);
            var info = new NetAdapterInfo
            {
                Name = ni.Name,
                Description = desc,
                IsPhysical = isPhysical,
                Ipv4Addresses = ipv4
            };
            if (isPhysical) physical.Add(info); else virtuals.Add(info);
        }
        return (physical, virtuals);
    }

    /// <summary>判断是否为物理网卡（排除虚拟/VPN/WSL 关键词）。</summary>
    public static bool IsPhysicalAdapter(string name, string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        var haystack = $"{name} {description}".ToLowerInvariant();
        return !VirtualKeywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>根据用户勾选的网卡集合计算要绑定的 IPv4 列表。</summary>
    public static List<IPAddress> GetBindAddresses(
        IEnumerable<NetAdapterInfo> selectedPhysical,
        IEnumerable<NetAdapterInfo> selectedVirtual)
    {
        var result = new List<IPAddress>();
        foreach (var a in selectedPhysical.Concat(selectedVirtual))
            result.AddRange(a.Ipv4Addresses);
        return result.Distinct().ToList();
    }

    /// <summary>获取本机第一个物理网卡的局域网 IPv4（用于显示与 UDP 广播通告）。</summary>
    public static IPAddress? GetFirstLanIpv4()
    {
        var (physical, _) = GetAllAdapters();
        foreach (var a in physical)
            foreach (var ip in a.Ipv4Addresses)
            {
                // 排除 APIPA (169.254.*) 与保留网段
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                return ip;
            }
        return null;
    }
}

/// <summary>
/// 全局网卡绑定配置（需求⑨）：
/// GUI 勾选后写入 SelectedPhysical / SelectedVirtual，各服务启动时读取。
/// 默认绑定全部物理网卡，虚拟网卡需用户手动勾选。
/// </summary>
public static class BindConfig
{
    public static List<NetAdapterInfo> SelectedPhysical { get; set; } = new();
    public static List<NetAdapterInfo> SelectedVirtual { get; set; } = new();

    /// <summary>根据当前勾选计算绑定 IP 列表。</summary>
    public static List<IPAddress> GetBindAddresses()
        => NetworkHelper.GetBindAddresses(SelectedPhysical, SelectedVirtual);

    /// <summary>当前选中的第一个物理网卡 IPv4（用于 UDP 广播通告）。</summary>
    public static IPAddress? GetFirstLanIpv4() => NetworkHelper.GetFirstLanIpv4();
}
