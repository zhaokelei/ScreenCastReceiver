// 模拟安卓 DLNA 投屏请求测试脚本
// 直接引用 ScreenCastReceiver 的核心 DLL，实例化 DlnaDmrService 并启动，然后用 HttpClient 发 SOAP 请求

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// 加载程序目录（和Release DLL在同一位置）
var baseDir = @"c:\Users\Administrator\Desktop\111\Windows-ScreenCastReceiver\src\ScreenCastReceiver\bin\x64\Release\net8.0-windows";
Environment.CurrentDirectory = baseDir;
Assembly.LoadFrom(Path.Combine(baseDir, "ScreenCastReceiver.dll"));

// 获取类型
var logType = Type.GetType("ScreenCastReceiver.Logging.AppLogger, ScreenCastReceiver");
var mpvType = Type.GetType("ScreenCastReceiver.Player.MpvSessionManager, ScreenCastReceiver");
var dlnaType = Type.GetType("ScreenCastReceiver.Services.DlnaDmrService, ScreenCastReceiver");
var portConfigType = Type.GetType("ScreenCastReceiver.Helpers.PortConfig, ScreenCastReceiver");

if (logType == null || mpvType == null || dlnaType == null || portConfigType == null)
{
    Console.WriteLine("❌ 类型加载失败，请检查DLL是否完整");
    return;
}

// 单例获取 Logger
var log = logType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
// 实例化播放器 + DLNA服务
var mpv = Activator.CreateInstance(mpvType, log);
var dlna = Activator.CreateInstance(dlnaType, log, mpv);

// 取端口配置
var cfg = portConfigType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
var dlnaHttpPort = (int)portConfigType.GetProperty("DlnaHttpPort")!.GetValue(cfg)!;
var dlnaDeviceNameProp = Type.GetType("ScreenCastReceiver.Helpers.AppSettings, ScreenCastReceiver")!
    .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
var appCfg = dlnaDeviceNameProp.GetValue(null);
var deviceName = (string)Type.GetType("ScreenCastReceiver.Helpers.AppSettings, ScreenCastReceiver")!
    .GetProperty("DlnaDeviceName")!.GetValue(appCfg)!;

Console.WriteLine($"=== 🟢 DLNA 投屏自动化测试 ===");
Console.WriteLine($"  配置 HTTP端口 = {dlnaHttpPort}");
Console.WriteLine($"  配置 DLNA设备名 = {deviceName}");
Console.WriteLine();

// 启动DLNA服务
Console.Write(">>> 1. 启动DLNA服务...");
var startMethod = dlnaType.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Instance)!;
var startTask = (Task)startMethod.Invoke(dlna, null)!;
await startTask;
Thread.Sleep(2500);

// 检查监听端口
var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
    .GetActiveTcpListeners().Where(p => p.Port == dlnaHttpPort).Count();
Console.WriteLine(listeners > 0 ? $" ✅ 端口 {dlnaHttpPort} 已监听（固定端口验证通过，没有+1！）" : $" ❌ 端口未监听");
if (listeners == 0) return;

var baseUrl = $"http://127.0.0.1:{dlnaHttpPort}";
var client = new HttpClient();

// ========== 1) GET description.xml ==========
Console.WriteLine();
Console.WriteLine($">>> 2) GET {baseUrl}/description.xml （模拟安卓APP获取设备描述）");
var desc = await client.GetStringAsync($"{baseUrl}/description.xml");
var hasName = desc.Contains(deviceName) || desc.Contains("ScreenCastReceiver");
var hasDlnaType = desc.Contains("MediaRenderer") && desc.Contains("AVTransport:1");
Console.WriteLine($"  返回长度 = {desc.Length} 字节");
Console.WriteLine($"  含设备名 ScreenCastReceiver：{(hasName ? "✅" : "❌")}   含 MediaRenderer/AVTransport 类型：{(hasDlnaType ? "✅" : "❌")}");

// ========== 2) GET SCPD 文件 ==========
Console.WriteLine();
Console.WriteLine($">>> 3) 校验3个SCPD资源可访问（AVTransport/ConnectionManager/RenderingControl）");
string[] scpds = { "/scpd/AVTransport.xml", "/scpd/ConnectionManager.xml", "/scpd/RenderingControl.xml" };
foreach (var s in scpds)
{
    var r = await client.GetAsync($"{baseUrl}{s}");
    Console.WriteLine($"  GET {s,-45} => {(int)r.StatusCode} {r.StatusCode} {r.Content.Headers.ContentLength}字节");
    r.EnsureSuccessStatusCode();
}

// ========== 3) SOAP SetAVTransportURI ==========
Console.WriteLine();
var testVideo = "https://cdn3.ryplay3.com/20240826/26664_d0b4898f/index.m3u8";
Console.WriteLine($">>> 4) SOAP SetAVTransportURI （模拟安卓端投送视频链接）");
Console.WriteLine($"  URL = {testVideo}");
var setUriSoap = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <u:SetAVTransportURI xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
      <InstanceID>0</InstanceID>
      <CurrentURI>{WebUtility.HtmlEncode(testVideo)}</CurrentURI>
      <CurrentURIMetaData></CurrentURIMetaData>
    </u:SetAVTransportURI>
  </s:Body>
</s:Envelope>";
var req1 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req1.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");
req1.Content = new StringContent(setUriSoap, Encoding.UTF8, "text/xml");
var resp1 = await client.SendAsync(req1);
var body1 = await resp1.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp1.StatusCode} {resp1.StatusCode}，长度 {body1.Length} 字节");
Console.WriteLine($"  含 SetAVTransportURIResponse = {(body1.Contains("SetAVTransportURIResponse") ? "✅" : "❌")}");

Thread.Sleep(1200);

// ========== 4) SOAP Play ==========
Console.WriteLine();
Console.WriteLine($">>> 5) SOAP Play （开始播放）");
var playSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <u:Play xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
      <InstanceID>0</InstanceID>
      <Speed>1</Speed>
    </u:Play>
  </s:Body>
</s:Envelope>";
var req2 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req2.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#Play\"");
req2.Content = new StringContent(playSoap, Encoding.UTF8, "text/xml");
var resp2 = await client.SendAsync(req2);
var body2 = await resp2.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp2.StatusCode} {resp2.StatusCode}，含 PlayResponse = {(body2.Contains("PlayResponse") ? "✅" : "❌")}");

Thread.Sleep(2000);

// 检查MPV进程是否已创建
var mpvCount = System.Diagnostics.Process.GetProcessesByName("mpv").Length;
Console.WriteLine($"  当前 MPV 进程数 = {mpvCount}（投屏后应启动播放器）");

// ========== 5) GetMediaInfo ==========
Console.WriteLine();
Console.WriteLine($">>> 6) SOAP GetMediaInfo （获取媒体信息）");
var gmiSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:GetMediaInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1""><InstanceID>0</InstanceID></u:GetMediaInfo></s:Body>
</s:Envelope>";
var req3 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req3.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetMediaInfo\"");
req3.Content = new StringContent(gmiSoap, Encoding.UTF8, "text/xml");
var resp3 = await client.SendAsync(req3);
var body3 = await resp3.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp3.StatusCode}，含 GetMediaInfoResponse = {(body3.Contains("GetMediaInfoResponse") ? "✅" : "❌")}");
if (body3.Contains("MediaDuration")) Console.WriteLine($"  MediaDuration = {System.Text.RegularExpressions.Regex.Match(body3, @"<MediaDuration>([^<]*)</MediaDuration>").Groups[1].Value}");
if (body3.Contains("NrTracks")) Console.WriteLine($"  NrTracks = {System.Text.RegularExpressions.Regex.Match(body3, @"<NrTracks>([^<]*)</NrTracks>").Groups[1].Value}");

// ========== 6) GetPositionInfo ==========
Console.WriteLine();
Console.WriteLine($">>> 7) SOAP GetPositionInfo （获取播放进度，安卓APP实时刷新进度条）");
var gpiSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:GetPositionInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1""><InstanceID>0</InstanceID></u:GetPositionInfo></s:Body>
</s:Envelope>";
var req4 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req4.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"");
req4.Content = new StringContent(gpiSoap, Encoding.UTF8, "text/xml");
var resp4 = await client.SendAsync(req4);
var body4 = await resp4.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp4.StatusCode}，含 GetPositionInfoResponse = {(body4.Contains("GetPositionInfoResponse") ? "✅" : "❌")}");
if (body4.Contains("RelTime"))
{
    Console.WriteLine($"  RelTime = {System.Text.RegularExpressions.Regex.Match(body4, @"<RelTime>([^<]*)</RelTime>").Groups[1].Value}");
    Console.WriteLine($"  TrackDuration = {System.Text.RegularExpressions.Regex.Match(body4, @"<TrackDuration>([^<]*)</TrackDuration>").Groups[1].Value}");
}

// ========== 7) GetTransportInfo ==========
Console.WriteLine();
Console.WriteLine($">>> 8) SOAP GetTransportInfo （获取传输/播放状态）");
var gtiSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:GetTransportInfo xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1""><InstanceID>0</InstanceID></u:GetTransportInfo></s:Body>
</s:Envelope>";
var req5 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req5.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo\"");
req5.Content = new StringContent(gtiSoap, Encoding.UTF8, "text/xml");
var resp5 = await client.SendAsync(req5);
var body5 = await resp5.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp5.StatusCode}，状态 = {System.Text.RegularExpressions.Regex.Match(body5, @"<CurrentTransportState>([^<]*)</CurrentTransportState>").Groups[1].Value}");

// ========== 8) Seek ==========
Console.WriteLine();
Console.WriteLine($">>> 9) SOAP Seek （拖动进度到 00:00:10，模拟用户拖动进度条）");
var seekSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:Seek xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1"">
    <InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>00:00:10</Target>
  </u:Seek></s:Body>
</s:Envelope>";
var req6 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req6.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#Seek\"");
req6.Content = new StringContent(seekSoap, Encoding.UTF8, "text/xml");
var resp6 = await client.SendAsync(req6);
var body6 = await resp6.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp6.StatusCode}，含 SeekResponse = {(body6.Contains("SeekResponse") ? "✅" : "❌")}");

Thread.Sleep(1500);

// ========== 9) Pause ==========
Console.WriteLine();
Console.WriteLine($">>> 10) SOAP Pause （暂停）");
var pauseSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:Pause xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1""><InstanceID>0</InstanceID></u:Pause></s:Body>
</s:Envelope>";
var req7 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req7.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#Pause\"");
req7.Content = new StringContent(pauseSoap, Encoding.UTF8, "text/xml");
var resp7 = await client.SendAsync(req7);
var body7 = await resp7.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp7.StatusCode}，含 PauseResponse = {(body7.Contains("PauseResponse") ? "✅" : "❌")}");

Thread.Sleep(1000);

// 再次查询状态
var req5b = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req5b.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo\"");
req5b.Content = new StringContent(gtiSoap, Encoding.UTF8, "text/xml");
var resp5b = await client.SendAsync(req5b);
var body5b = await resp5b.Content.ReadAsStringAsync();
Console.WriteLine($"  暂停后状态 = {System.Text.RegularExpressions.Regex.Match(body5b, @"<CurrentTransportState>([^<]*)</CurrentTransportState>").Groups[1].Value}");

// ========== 10) Stop ==========
Console.WriteLine();
Console.WriteLine($">>> 11) SOAP Stop （停止投屏）");
var stopSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:Stop xmlns:u=""urn:schemas-upnp-org:service:AVTransport:1""><InstanceID>0</InstanceID></u:Stop></s:Body>
</s:Envelope>";
var req8 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req8.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#Stop\"");
req8.Content = new StringContent(stopSoap, Encoding.UTF8, "text/xml");
var resp8 = await client.SendAsync(req8);
var body8 = await resp8.Content.ReadAsStringAsync();
Console.WriteLine($"  响应 {(int)resp8.StatusCode}，含 StopResponse = {(body8.Contains("StopResponse") ? "✅" : "❌")}");

Thread.Sleep(1000);
var req5c = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/AVTransport/control");
req5c.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo\"");
req5c.Content = new StringContent(gtiSoap, Encoding.UTF8, "text/xml");
var resp5c = await client.SendAsync(req5c);
var body5c = await resp5c.Content.ReadAsStringAsync();
Console.WriteLine($"  Stop后状态 = {System.Text.RegularExpressions.Regex.Match(body5c, @"<CurrentTransportState>([^<]*)</CurrentTransportState>").Groups[1].Value}");

// ========== 11) SSDP M-SEARCH ==========
Console.WriteLine();
Console.WriteLine($">>> 12) UDP SSDP M-SEARCH （模拟安卓端搜索设备，广播到239.255.255.250:1900）");
var ssdp = "M-SEARCH * HTTP/1.1\r\n" +
           "HOST: 239.255.255.250:1900\r\n" +
           "MAN: \"ssdp:discover\"\r\n" +
           "MX: 2\r\n" +
           "ST: ssdp:all\r\n" +
           "USER-AGENT: Android/14 UPnP/1.0 Cling/2.0\r\n\r\n";
var udp = new System.Net.Sockets.UdpClient();
udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReceiveTimeout, 3000);
var reqBytes = Encoding.ASCII.GetBytes(ssdp);
await udp.SendAsync(reqBytes, reqBytes.Length, "239.255.255.250", 1900);
int ssdpOk = 0;
try
{
    var ep = new IPEndPoint(IPAddress.Any, 0);
    for (int i = 0; i < 5; i++)
    {
        var buf = udp.Receive(ref ep);
        var res = Encoding.ASCII.GetString(buf);
        if (res.Contains("MediaRenderer") || res.Contains("ScreenCastReceiver"))
        {
            ssdpOk++;
            var loc = System.Text.RegularExpressions.Regex.Match(res, @"LOCATION:\s*(\S+)").Groups[1].Value;
            Console.WriteLine($"  ✅ 收到SSDP响应 #{ssdpOk}，来自 {ep.Address}, Location={loc}");
        }
    }
}
catch (Exception e) { Console.WriteLine($"  结束：{e.Message}"); }
finally { udp.Close(); }
Console.WriteLine($"  共收到 {ssdpOk} 条 ScreenCast/MediaRenderer SSDP 响应");

// 结束：停止服务
Console.WriteLine();
Console.WriteLine(">>> 13) 停止DLNA服务，关闭MPV...");
dlnaType.GetMethod("StopAsync", BindingFlags.Public | BindingFlags.Instance)!.Invoke(dlna, null);
Thread.Sleep(2000);

// 显示程序日志
var logFile = Path.Combine(baseDir, "Logs", $"screencast_{DateTime.Now:yyyyMMdd}.log");
if (File.Exists(logFile))
{
    var allLines = File.ReadAllLines(logFile);
    var lastSession = new List<string>();
    for (int i = allLines.Length - 1; i >= 0; i--)
    {
        lastSession.Insert(0, allLines[i]);
        if (allLines[i].Contains("绑定网卡更新") && lastSession.Count > 30) break;
    }
    Console.WriteLine();
    Console.WriteLine("===== 📜 本次DLNA测试的日志片段 =====");
    foreach (var l in lastSession.Where(l =>
        l.Contains("[DLNA]") || l.Contains("MPV") || l.Contains("SetAVTransportURI") ||
        l.Contains("收到投屏") || l.Contains("播放") || l.Contains("端口") || l.Contains("HTTP 控制")).Take(40))
        Console.WriteLine($"  {l}");
}

Console.WriteLine();
Console.WriteLine("=== 全部测试完成 ===");
