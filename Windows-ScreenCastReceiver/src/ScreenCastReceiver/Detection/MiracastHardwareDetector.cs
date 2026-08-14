using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCastReceiver.Detection;

/// <summary>单张网卡的 Miracast 硬件检测结果。</summary>
public sealed class MiracastAdapterResult
{
    public required string AdapterName { get; init; }
    public bool? Supported { get; init; }
    public string Raw { get; init; } = "";
}

/// <summary>
/// Miracast 硬件检测（需求④）：
/// - 遍历全部无线网卡，调用 netsh wlan show drivers interface="xxx" 获取每张卡的
///   "Wireless Display（无线显示）" 支持状态
/// - 附 NativeWifi (WlanEnumInterfaces) 二次校验：确认无线网卡确实存在且可枚举
/// - 检测结果仅用于 GUI 文字显示与日志，不拦截用户手动开启 Miracast 服务
/// </summary>
public static class MiracastHardwareDetector
{
    /// <summary>汇总检测结果：true=支持 / false=不支持 / null=多张网卡结果不一致或未知。</summary>
    public static (bool? Supported, List<MiracastAdapterResult> Details) Detect()
    {
        var results = new List<MiracastAdapterResult>();
        var wireless = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToList();

        if (wireless.Count == 0)
        {
            // 没有无线网卡 → 通过 NativeWifi 二次校验，确认是否只是类型判断差异
            var nativeNames = NativeWifiProbe.ListWirelessInterfaces();
            if (nativeNames.Count > 0)
            {
                foreach (var name in nativeNames)
                {
                    results.Add(ProbeAdapter(name));
                }
            }
        }
        else
        {
            foreach (var ni in wireless)
            {
                results.Add(ProbeAdapter(ni.Name));
            }
        }

        // 汇总
        bool? summary = null;
        if (results.Count == 1) summary = results[0].Supported;
        else if (results.Count > 1)
        {
            var distinct = results.Where(r => r.Supported.HasValue).Select(r => r.Supported!.Value).Distinct().ToList();
            if (distinct.Count == 1) summary = distinct[0];
            // 多张卡结果不一致 → null（界面显示“多张网卡结果见日志”）
        }

        return (summary, results);
    }

    /// <summary>对单张无线网卡执行 netsh 检测。</summary>
    private static MiracastAdapterResult ProbeAdapter(string interfaceName)
    {
        var raw = RunNetshDrivers(interfaceName);
        var supported = ParseWirelessDisplay(raw);
        return new MiracastAdapterResult
        {
            AdapterName = interfaceName,
            Supported = supported,
            Raw = raw
        };
    }

    /// <summary>运行 netsh wlan show drivers 获取驱动信息。</summary>
    private static string RunNetshDrivers(string interfaceName)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                Arguments = $"wlan show drivers interface=\"{interfaceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(8000);
            return output;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 解析 netsh 输出中的无线显示支持字段。
    /// 中文系统：无线显示：支持/不支持；英文系统：Wireless Display Supported: Yes/No
    /// </summary>
    private static bool? ParseWirelessDisplay(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line, @"无线显示[：:]\s*(支持|不支持|是|否)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = m.Groups[1].Value;
                return v is "支持" or "是" || v.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            }
            m = Regex.Match(line, @"Wireless Display\s*(Supported)?\s*[：:]\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = m.Groups[2].Value;
                return v.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                       v.Equals("Supported", StringComparison.OrdinalIgnoreCase);
            }
        }
        return null;
    }
}

/// <summary>NativeWifi 二次校验（确认无线网卡存在性，防止 netsh 因权限/命名差异漏检）。</summary>
internal static class NativeWifiProbe
{
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern int WlanFreeMemory(IntPtr pMemory);

    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    /// <summary>通过 NativeWifi API 列出系统可见的无线接口名。</summary>
    public static List<string> ListWirelessInterfaces()
    {
        var names = new List<string>();
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return names;
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var listPtr) != 0) return names;
                try
                {
                    // WLAN_INTERFACE_INFO_LIST: DWORD dwNumberOfItems; WLAN_INTERFACE_INFO InterfaceInfo[1];
                    var count = System.Runtime.InteropServices.Marshal.ReadInt32(listPtr);
                    const int infoSize = 548; // sizeof(WLAN_INTERFACE_INFO) = 540 + 4 + 4
                    var basePtr = listPtr + 4;
                    for (var i = 0; i < count; i++)
                    {
                        var info = basePtr + i * infoSize;
                        // 结构体偏移 0..256: GUID(16) + description(256)
                        var desc = System.Runtime.InteropServices.Marshal.PtrToStringUni(info + 16, 128);
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            var name = desc.Split('\0')[0];
                            names.Add(name);
                        }
                    }
                }
                finally { WlanFreeMemory(listPtr); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch { /* NativeWifi 探测失败不阻断主流程 */ }
        return names;
    }
}
