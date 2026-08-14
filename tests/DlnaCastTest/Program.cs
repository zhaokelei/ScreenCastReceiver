// DLNA 投屏自动化测试 Console
// 1) 启动 ScreenCastReceiver GUI
// 2) Win32 鼠标勾选 DLNA 复选框
// 3) 依次执行：SSDP 发现 / description.xml / SCPD / SetAVTransportURI / Play / GetMediaInfo / GetPositionInfo / Seek / Pause / Stop

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#region Win32 API
internal class Win32
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint cButtons, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    public const uint MOUSEEVENTF_LEFTUP = 0x04;
    public const int SW_RESTORE = 9;
}
#endregion

internal static class Program
{
    private const string MainWindowTitle = "ScreenCastReceiver - Windows 投屏接收端";
    private static readonly string BaseDir = @"c:\Users\Administrator\Desktop\111\src\ScreenCastReceiver\bin\x64\Release\net8.0-windows";
    private static readonly string PortCfgPath = Path.Combine(BaseDir, "port_config.json");
    private static readonly string LogDir = Path.Combine(BaseDir, "Logs");

    private static async Task<int> Main()
    {
        Console.Title = "DLNA 投屏自动化测试";
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║     🟢 模拟安卓 DLNA 投屏 - 自动化控制链路测试                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝\n");

        // 1) 读取配置的 DLNA 端口
        int dlnaHttpPort = 49152;
        try
        {
            if (File.Exists(PortCfgPath))
            {
                var cfg = JsonSerializer.Deserialize<PortCfg>(File.ReadAllText(PortCfgPath));
                dlnaHttpPort = cfg?.DlnaHttpPort ?? dlnaHttpPort;
            }
        }
        catch { }
        Console.WriteLine($"📋 配置 DLNA HTTP 端口: {dlnaHttpPort}");

        // 2) 关闭残留进程
        KillProcesses(new[] { "ScreenCastReceiver", "mpv", "go2rtc" });
        await Task.Delay(1500);

        // 3) 启动 ScreenCastReceiver
        Console.WriteLine($"\n▶️  启动 GUI 程序...");
        var guiProc = StartGui();
        if (guiProc == null) { Console.WriteLine("❌ 启动失败"); return 1; }

        // 4) 等待窗口可见
        IntPtr hwnd = WaitForWindow(MainWindowTitle, 20000);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("❌ 未找到主窗口"); return 1; }
        Console.WriteLine($"✅ 主窗口就绪  HWND={hwnd}  PID={guiProc.Id}");

        // 5) 等待 DLNA 服务自动启动（已通过 --auto-start-dlna 参数，无需 Win32 点击）
        Console.WriteLine($"\n⏳ 等待 DLNA HTTP 端口 {dlnaHttpPort} 监听（命令行自动启动）...");
        Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(hwnd);
        if (!WaitForPortListening(dlnaHttpPort, 20000))
        {
            Console.WriteLine($"❌ DLNA 端口 {dlnaHttpPort} 未监听！当前49150-49200监听列表：");
            DumpListenersInRange();
            return 1;
        }
        // 再等SSDP/HTTP初始化完成
        await Task.Delay(2500);
        Console.WriteLine($"✅ 端口 {dlnaHttpPort} 已监听！\n");

        var baseUrl = $"http://127.0.0.1:{dlnaHttpPort}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // ========== 1. GET description.xml ==========
        Console.WriteLine("━━ 1. GET description.xml（模拟安卓获取设备描述） ━━");
        var desc = await http.GetStringAsync($"{baseUrl}/description.xml");
        bool hasName = desc.Contains("ScreenCastReceiver") || desc.Contains("我的影院");
        bool hasRenderer = desc.Contains("MediaRenderer") && desc.Contains("AVTransport:1");
        var deviceName = Regex.Match(desc, @"<friendlyName>([^<]*)</friendlyName>").Groups[1].Value;
        Console.WriteLine($"   friendlyName = {deviceName}");
        Console.WriteLine($"   返回长度 = {desc.Length} 字节");
        Console.WriteLine($"   设备名/型号匹配: {(hasName ? "✅" : "❌")}    MediaRenderer + AVTransport: {(hasRenderer ? "✅" : "❌")}\n");

        // ========== 2. 三个 SCPD ==========
        Console.WriteLine("━━ 2. GET 三个 SCPD 资源（安卓 Cling 库按此顺序请求） ━━");
        foreach (var s in new[] { "/scpd/AVTransport.xml", "/scpd/ConnectionManager.xml", "/scpd/RenderingControl.xml" })
        {
            var resp = await http.GetAsync($"{baseUrl}{s}");
            string tag = resp.IsSuccessStatusCode ? "✅" : "❌";
            Console.WriteLine($"   GET {s,-44} => {(int)resp.StatusCode} {resp.StatusCode} {tag} {resp.Content.Headers.ContentLength}B");
        }
        Console.WriteLine();

        // ========== 3. SetAVTransportURI ==========
        var testVideo = "https://cdn3.ryplay3.com/20240826/26664_d0b4898f/index.m3u8";
        Console.WriteLine("━━ 3. SOAP SetAVTransportURI（模拟安卓投送视频链接） ━━");
        Console.WriteLine($"   投送URL = {testVideo}");
        string[] results = new string[12];
        int idx = 0;
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "SetAVTransportURI", $@"
<InstanceID>0</InstanceID>
<CurrentURI>{WebUtility.HtmlEncode(testVideo)}</CurrentURI>
<CurrentURIMetaData></CurrentURIMetaData>");
        await Task.Delay(1500);
        int mpvBefore = Process.GetProcessesByName("mpv").Length;
        Console.WriteLine($"   SetAVTransportURI: {results[0]}   (MPV进程数当前={mpvBefore})\n");

        // ========== 4. Play ==========
        Console.WriteLine("━━ 4. SOAP Play（开始播放） ━━");
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "Play",
            "<InstanceID>0</InstanceID><Speed>1</Speed>");
        await Task.Delay(2500);
        int mpvPlay = Process.GetProcessesByName("mpv").Length;
        Console.WriteLine($"   Play: {results[idx - 1]}    执行后MPV进程数={mpvPlay}\n");

        // ========== 5. GetMediaInfo ==========
        Console.WriteLine("━━ 5. SOAP GetMediaInfo（获取时长/曲目数） ━━");
        var gmiResp = await CallSoapRaw(http, baseUrl, "AVTransport:1", "GetMediaInfo", "<InstanceID>0</InstanceID>");
        bool gmiOk = gmiResp.Contains("GetMediaInfoResponse");
        Console.WriteLine($"   响应OK: {(gmiOk ? "✅" : "❌")}");
        if (gmiOk)
        {
            string dur = Regex.Match(gmiResp, @"<MediaDuration>([^<]*)</MediaDuration>").Groups[1].Value;
            string nr = Regex.Match(gmiResp, @"<NrTracks>([^<]*)</NrTracks>").Groups[1].Value;
            Console.WriteLine($"   MediaDuration = {dur}    NrTracks = {nr}");
        }
        Console.WriteLine();

        // ========== 6. GetPositionInfo ==========
        Console.WriteLine("━━ 6. SOAP GetPositionInfo（获取进度） ━━");
        var gpiResp = await CallSoapRaw(http, baseUrl, "AVTransport:1", "GetPositionInfo", "<InstanceID>0</InstanceID>");
        bool gpiOk = gpiResp.Contains("GetPositionInfoResponse");
        Console.WriteLine($"   响应OK: {(gpiOk ? "✅" : "❌")}");
        if (gpiOk)
        {
            string rel = Regex.Match(gpiResp, @"<RelTime>([^<]*)</RelTime>").Groups[1].Value;
            string td = Regex.Match(gpiResp, @"<TrackDuration>([^<]*)</TrackDuration>").Groups[1].Value;
            string meta = Regex.Match(gpiResp, @"<TrackURI>([^<]*)</TrackURI>").Groups[1].Value;
            Console.WriteLine($"   RelTime（当前进度） = {rel}");
            Console.WriteLine($"   TrackDuration（总时长） = {td}");
            if (!string.IsNullOrEmpty(meta)) Console.WriteLine($"   TrackURI = {meta}");
        }
        Console.WriteLine();

        // ========== 7. GetTransportInfo ==========
        Console.WriteLine("━━ 7. SOAP GetTransportInfo（获取播放状态 PLAYING/PAUSED...） ━━");
        var st1 = await GetTransportState(http, baseUrl);
        Console.WriteLine($"   播放后状态 = {(st1.Contains("PLAYING") ? "🟢 " : "")}{st1}\n");

        // ========== 8. Seek ==========
        Console.WriteLine("━━ 8. SOAP Seek（模拟拖动进度条到 00:00:10） ━━");
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "Seek",
            "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>00:00:10</Target>");
        await Task.Delay(1500);
        Console.WriteLine($"   Seek: {results[idx - 1]}");
        var gpiAfter = await CallSoapRaw(http, baseUrl, "AVTransport:1", "GetPositionInfo", "<InstanceID>0</InstanceID>");
        if (gpiAfter.Contains("GetPositionInfoResponse"))
            Console.WriteLine($"   Seek后 RelTime = {Regex.Match(gpiAfter, @"<RelTime>([^<]*)</RelTime>").Groups[1].Value}\n");

        // ========== 9. Pause ==========
        Console.WriteLine("━━ 9. SOAP Pause（暂停） ━━");
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "Pause", "<InstanceID>0</InstanceID>");
        await Task.Delay(1500);
        var st2 = await GetTransportState(http, baseUrl);
        Console.WriteLine($"   Pause: {results[idx - 1]}    暂停后状态 = {(st2.Contains("PAUSED") ? "🟡 " : "")}{st2}\n");

        // ========== 10. Resume Play ==========
        Console.WriteLine("━━ 10. SOAP Play（恢复播放） ━━");
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "Play",
            "<InstanceID>0</InstanceID><Speed>1</Speed>");
        await Task.Delay(1500);
        var st3 = await GetTransportState(http, baseUrl);
        Console.WriteLine($"   Play恢复: {results[idx - 1]}    恢复后状态 = {(st3.Contains("PLAYING") ? "🟢 " : "")}{st3}\n");

        // ========== 11. Stop ==========
        Console.WriteLine("━━ 11. SOAP Stop（结束投屏） ━━");
        results[idx++] = await CallSoap(http, baseUrl, "AVTransport:1", "Stop", "<InstanceID>0</InstanceID>");
        await Task.Delay(1500);
        var st4 = await GetTransportState(http, baseUrl);
        Console.WriteLine($"   Stop: {results[idx - 1]}    Stop后状态 = {(st4.Contains("STOPPED") || st4.Contains("NO_MEDIA") ? "🔴 " : "")}{st4}");
        int mpvAfterStop = Process.GetProcessesByName("mpv").Length;
        Console.WriteLine($"   Stop后 MPV 进程数={mpvAfterStop}\n");

        // ========== 12. SSDP M-SEARCH ==========
        Console.WriteLine("━━ 12. UDP SSDP M-SEARCH（模拟安卓广播搜设备） ━━");
        int ssdpHits = DoSsdpSearch();
        Console.WriteLine($"   收到 {ssdpHits} 条 ScreenCast/MediaRenderer SSDP 响应\n");

        // ========== 13. 打印服务端日志片段 ==========
        Console.WriteLine("━━ 13. 本次测试的服务端日志片段 ━━");
        DumpLogTail(60);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  ✅ 全部测试执行完毕！请对比上述日志检查 DLNA 控制链路完整性");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("\n(15秒后自动退出)");
        await Task.Delay(15000);
        Environment.Exit(0);
        return 0;
    }

    #region 辅助方法
    private static void KillProcesses(string[] names)
    {
        foreach (var n in names)
        {
            foreach (var p in Process.GetProcessesByName(n))
            {
                try { p.Kill(); } catch { }
            }
        }
    }

    private static Process? StartGui()
    {
        var exe = Path.Combine(BaseDir, "ScreenCastReceiver.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine($"❌ 找不到 {exe}");
            return null;
        }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = BaseDir,
            UseShellExecute = false
        };
        // 自动化测试：通过命令行参数自动启动 DLNA 服务，避免 Win32 模拟鼠标点击坐标不准
        psi.ArgumentList.Add("--auto-start-dlna");
        return Process.Start(psi);
    }

    private static IntPtr WaitForWindow(string title, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hwnd = Win32.FindWindow(null, title);
            if (hwnd != IntPtr.Zero && Win32.IsWindowVisible(hwnd)) return hwnd;
            Thread.Sleep(300);
        }
        return IntPtr.Zero;
    }

    private static bool ClickOnce(IntPtr hwnd, int relX, int relY)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;
        int x = rect.Left + relX;
        int y = rect.Top + relY;
        Win32.SetCursorPos(x, y);
        Thread.Sleep(120);
        Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);
        Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(400);
        return true;
    }

    private static bool MultiClickAt(IntPtr hwnd, int[][] candidates, int dlnaPort)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;
        int i = 0;
        foreach (var c in candidates)
        {
            i++;
            int x = rect.Left + c[0], y = rect.Top + c[1];
            Console.WriteLine($"   尝试 #{i}: 屏幕({x},{y}) （窗口内偏移={c[0]},{c[1]}）");
            Win32.SetCursorPos(x, y);
            Thread.Sleep(120);
            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            // 点击后等待几秒看端口是否起来
            for (int w = 0; w < 6; w++)
            {
                Thread.Sleep(500);
                if (IsPortListening(dlnaPort))
                {
                    Console.WriteLine($"   ✅ 第#{i}次点击成功！端口 {dlnaPort} 已监听");
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync("127.0.0.1", port);
            if (task.Wait(350) && tcp.Connected) return true;
        }
        catch { }
        return false;
    }

    private static bool WaitForPortListening(int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var tcp = new TcpClient();
                var task = tcp.ConnectAsync("127.0.0.1", port);
                if (task.Wait(400) && tcp.Connected) return true;
            }
            catch { }
            Thread.Sleep(300);
        }
        return false;
    }

    private static void DumpListenersInRange()
    {
        var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
        foreach (var ep in props.GetActiveTcpListeners())
        {
            if (ep.Port >= 49150 && ep.Port <= 49200)
                Console.WriteLine($"   listening :{ep.Port}");
        }
    }

    private static async Task<string> CallSoap(HttpClient http, string baseUrl, string svcId, string action, string bodyInner)
    {
        var resp = await CallSoapRaw(http, baseUrl, svcId, action, bodyInner);
        if (resp.Contains(action + "Response")) return "✅";
        if (resp.Contains("faultcode") || resp.Contains("Fault"))
        {
            var fc = Regex.Match(resp, @"<faultstring[^>]*>([^<]*)</faultstring>").Groups[1].Value;
            return $"❌ Fault: {fc}";
        }
        return $"⚠ HTTP成功但无 {action}Response";
    }

    private static async Task<string> CallSoapRaw(HttpClient http, string baseUrl, string svcId, string action, string bodyInner)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <u:{action} xmlns:u=""urn:schemas-upnp-org:service:{svcId}"">
      {bodyInner}
    </u:{action}>
  </s:Body>
</s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
        req.Headers.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:{svcId}#{action}\"");
        req.Content = new StringContent(soap, Encoding.UTF8, "text/xml");
        try
        {
            var resp = await http.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception e) { return $"<error>{e.Message}</error>"; }
    }

    private static async Task<string> GetTransportState(HttpClient http, string baseUrl)
    {
        var body = await CallSoapRaw(http, baseUrl, "AVTransport:1", "GetTransportInfo", "<InstanceID>0</InstanceID>");
        return Regex.Match(body, @"<CurrentTransportState>([^<]*)</CurrentTransportState>").Groups[1].Value;
    }

    private static int DoSsdpSearch()
    {
        int hits = 0;
        var msg = "M-SEARCH * HTTP/1.1\r\n" +
                  "HOST: 239.255.255.250:1900\r\n" +
                  "MAN: \"ssdp:discover\"\r\n" +
                  "MX: 3\r\n" +
                  "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n" +
                  "USER-AGENT: Android/14 UPnP/1.0 Cling/2.0\r\n\r\n";
        var buf = Encoding.ASCII.GetBytes(msg);
        try
        {
            // SSDP 客户端：绑定 ANY:随机端口，加入 239.255.255.250 组播组即可接收响应
            // （服务端监听在 1900，客户端不能占用 1900 否则会与系统 UPnP 冲突）
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); // 随机端口
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse("239.255.255.250"), IPAddress.Any));
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 4000);

            // 发送 2 次 M-SEARCH（间隔 200ms）增加可靠性
            for (int t = 0; t < 2; t++)
            {
                udp.SendAsync(buf, buf.Length, "239.255.255.250", 1900).Wait(300);
                Thread.Sleep(200);
            }

            var ep = new IPEndPoint(IPAddress.Any, 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3500)
            {
                byte[] data;
                try { data = udp.Receive(ref ep); }
                catch (SocketException) { break; }
                var s = Encoding.ASCII.GetString(data);
                if (s.Contains("ScreenCast") || s.Contains("MediaRenderer") || s.Contains("我的影院"))
                {
                    hits++;
                    var loc = Regex.Match(s, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    Console.WriteLine($"   ✅ #{hits} 来自 {ep.Address,-15} LOCATION={loc}");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"   SSDP完成({e.Message})");
        }
        return hits;
    }

    private static void DumpLogTail(int lines)
    {
        var today = Path.Combine(LogDir, $"screencast_{DateTime.Now:yyyyMMdd}.log");
        if (!File.Exists(today)) { Console.WriteLine("   （日志文件不存在）"); return; }
        var all = File.ReadAllLines(today);
        int i = Math.Max(0, all.Length - lines);
        for (; i < all.Length; i++)
        {
            var l = all[i];
            if (l.Contains("[DLNA]") || l.Contains("MPV") || l.Contains("mpv") || l.Contains("SetAVTransport")
                || l.Contains("投屏") || l.Contains("端口") || l.Contains("HTTP 控制") || l.Contains("Play") || l.Contains("Seek"))
                Console.WriteLine($"   {l}");
        }
    }
    #endregion

    // 仅用于反序列化 port_config.json
    private sealed class PortCfg { public int DlnaHttpPort { get; set; } }
}
