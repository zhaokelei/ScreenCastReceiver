using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ScreenCastReceiver.Logging;

namespace ScreenCastReceiver.Services;

/// <summary>
/// 纯 C# 实现的 UPnP/DLNA MediaRenderer (DMR) 协议栈。
///
/// 说明：Open.UPnP 包已在 NuGet 上不可用（404），为保证"完整可编译"，
/// 这里以 BCL 原生实现等价的完整 DMR：
///   1) SSDP：组播 NOTIFY alive/byebye + 响应 M-SEARCH（UDP 1900）
///   2) HTTP：设备描述 description.xml、SCPD、SOAP 控制（AVTransport/
///      ConnectionManager/RenderingControl）、GENA SUBSCRIBE 应答
///   3) 控制指令通过事件回传给 DlnaDmrService → MPV
///
/// 支持安卓端 B站/腾讯视频等 APP 的"投屏"（推视频播放链接，非屏幕镜像）。
/// </summary>
public sealed class DmrUpnpServer : IDisposable
{
    public const string DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";
    public const string AvtServiceType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string CmServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";
    public const string RcServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";

    // ---------- 控制事件（由 DlnaDmrService 订阅并驱动 MPV） ----------
    public event Action<string, string>? SetUri;        // (uri, metadata)
    public event Action? Play;
    public event Action? Pause;
    public event Action? Stop;
    public event Action<double>? Seek;
    public event Action? Next;
    public event Action? Previous;

    /// <summary>提供给 GetPositionInfo 的当前位置/时长（秒）。</summary>
    public Func<(double Pos, double Dur)>? PositionProvider { get; set; }

    /// <summary>传输状态：0=停止 1=播放 2=暂停。</summary>
    public Func<int>? TransportStateProvider { get; set; }

    private readonly AppLogger _log;
    private readonly string _udn;
    private string _deviceName = "Xiaolei DLAN";

    private UdpClient? _ssdp;
    private CancellationTokenSource? _ssdpCts;
    private Thread? _ssdpThread;

    private TcpListener? _http;
    private CancellationTokenSource? _httpCts;
    private Thread? _httpThread;

    private IPAddress[] _bindAddresses = Array.Empty<IPAddress>();
    private int _httpPort;

    public DmrUpnpServer(AppLogger log)
    {
        _log = log;
        _udn = "uuid:ScreenCastReceiver-" + Guid.NewGuid().ToString("N");
    }

    public string Udn => _udn;

    /// <summary>HTTP 监听端口（探测后可用）。</summary>
    public int HttpPort => _httpPort;

    public bool IsAlive => _httpThread != null && _httpThread.IsAlive;

    /// <summary>
    /// 启动 DMR。udp1900Port 固定为 1900（DLNA 约定），被占用时给出明确日志并降级继续。
    /// </summary>
    public void Start(string deviceName, int httpPort, int udp1900Port, IPAddress[] bindAddresses)
    {
        _deviceName = deviceName;
        _bindAddresses = bindAddresses.Length == 0 ? new[] { IPAddress.Any } : bindAddresses;
        _httpPort = httpPort;

        // ---------- 1. HTTP 控制服务器 ----------
        _http = new TcpListener(IPAddress.Any, httpPort);
        _http.Start();
        _httpPort = ((IPEndPoint)_http.LocalEndpoint).Port;
        _httpCts = new CancellationTokenSource();
        _httpThread = new Thread(HttpLoop) { IsBackground = true, Name = "DmrHttp" };
        _httpThread.Start();
        _log.Info("[DLNA]", $"HTTP 控制服务器已启动, 端口={_httpPort}");

        // ---------- 2. SSDP 发现 ----------
        try
        {
            _ssdp = new UdpClient(AddressFamily.InterNetwork);
            _ssdp.ExclusiveAddressUse = false;
            _ssdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _ssdp.Client.Bind(new IPEndPoint(IPAddress.Any, udp1900Port));
            foreach (var addr in _bindAddresses)
            {
                try { _ssdp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), addr); }
                catch (Exception ex)
                {
                    _log.Warn("[DLNA]", $"加入组播失败 {addr}: {ex.Message}");
                }
            }
            _ssdpCts = new CancellationTokenSource();
            _ssdpThread = new Thread(SsdpLoop) { IsBackground = true, Name = "DmrSsdp" };
            _ssdpThread.Start();
            SendNotify("ssdp:alive");
            _log.Info("[DLNA]", $"SSDP 已就绪 (UDP {udp1900Port})，安卓投屏 APP 可搜索到设备 \"{deviceName}\"");
        }
        catch (SocketException ex)
        {
            // UDP 1900 被占用（需求⑦）：给出明确日志提示，HTTP 服务继续运行
            _log.Error("[DLNA]", $"UDP 1900 被占用，SSDP 广播无法启用（可能已有其它 UPnP 服务）：{ex.Message}");
            try { _ssdp?.Close(); } catch { }
            _ssdp = null;
        }
    }

    // ==================== SSDP ====================

    private void SsdpLoop()
    {
        var token = _ssdpCts!.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try
                {
                    data = _ssdp!.Receive(ref remote);
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested) break;
                    _log.Warn("[DLNA]", $"SSDP 接收异常: {ex.Message}");
                    Thread.Sleep(500);
                    continue;
                }

                var msg = Encoding.UTF8.GetString(data);
                var lines = msg.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var first = lines.Length > 0 ? lines[0] : "";
                var headers = ParseHeaders(lines.Skip(1));
                headers["request-line"] = first;

                if (first.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
                {
                    HandleMsSearch(headers, remote);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log.Error("[DLNA]", $"SSDP 循环异常退出: {ex.Message}");
        }
    }

    private void HandleMsSearch(Dictionary<string, string> headers, IPEndPoint remote)
    {
        try
        {
            var st = headers.GetValueOrDefault("ST") ?? "";
            if (string.IsNullOrEmpty(st)) return;
            if (!string.Equals(st, "ssdp:all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(st, DeviceType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(st, _udn, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(st, "upnp:rootdevice", StringComparison.OrdinalIgnoreCase))
                return;

            var localIp = GetLocalIpFor(remote.Address);
            var usn = st switch
            {
                "upnp:rootdevice" => $"{_udn}::upnp:rootdevice",
                _ when st.Equals(_udn, StringComparison.OrdinalIgnoreCase) => _udn,
                _ => $"{_udn}::{DeviceType}"
            };
            var resp =
                "HTTP/1.1 200 OK\r\n" +
                "CACHE-CONTROL: max-age=1800\r\n" +
                $"DATE: {DateTime.UtcNow:r}\r\n" +
                "EXT:\r\n" +
                $"LOCATION: http://{localIp}:{_httpPort}/description.xml\r\n" +
                "SERVER: Windows/10.0 UPnP/1.0 ScreenCastReceiver/1.0\r\n" +
                $"ST: {st}\r\n" +
                $"USN: {usn}\r\n" +
                "\r\n";
            _ssdp!.Send(Encoding.UTF8.GetBytes(resp), resp.Length, remote);
            _log.Info("[DLNA]", $"响应 M-SEARCH (ST={st}) from {remote.Address}");
        }
        catch (Exception ex)
        {
            _log.Warn("[DLNA]", $"响应 M-SEARCH 失败: {ex.Message}");
        }
    }

    /// <summary>发送 NOTIFY 广播（alive/byebye）。</summary>
    private void SendNotify(string nts)
    {
        if (_ssdp == null) return;
        var entries = new (string Nt, string Usn)[]
        {
            ("upnp:rootdevice", $"{_udn}::upnp:rootdevice"),
            (DeviceType, $"{_udn}::{DeviceType}"),
            (_udn, _udn)
        };
        foreach (var addr in _bindAddresses)
        {
            foreach (var (nt, usn) in entries)
            {
                try
                {
                    var msg =
                        "NOTIFY * HTTP/1.1\r\n" +
                        "HOST: 239.255.255.250:1900\r\n" +
                        "CACHE-CONTROL: max-age=1800\r\n" +
                        $"LOCATION: http://{addr}:{_httpPort}/description.xml\r\n" +
                        "NT: " + nt + "\r\n" +
                        "NTS: " + nts + "\r\n" +
                        "SERVER: Windows/10.0 UPnP/1.0 ScreenCastReceiver/1.0\r\n" +
                        "USN: " + usn + "\r\n" +
                        "\r\n";
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    _ssdp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                }
                catch (Exception ex)
                {
                    _log.Warn("[DLNA]", $"NOTIFY 发送失败 ({nts}): {ex.Message}");
                }
            }
        }
    }

    // ==================== HTTP / SOAP ====================

    private void HttpLoop()
    {
        var token = _httpCts!.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _http!.AcceptTcpClient();
            }
            catch (Exception)
            {
                break;
            }

            // 每个连接单独线程处理，避免慢客户端阻塞发现
            var t = new Thread(() => HandleHttpClient(client))
            {
                IsBackground = true,
                Name = "DmrHttpConn"
            };
            t.Start();
        }
    }

    private void HandleHttpClient(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 15000;
            var stream = client.GetStream();
            var headerBuffer = new List<byte>();
            var oneByte = new byte[1];
            var headerEnd = -1;

            // 读取请求头（按字节直到 \r\n\r\n）
            while (headerEnd < 0)
            {
                var n = stream.Read(oneByte, 0, 1);
                if (n == 0) return;
                headerBuffer.Add(oneByte[0]);
                if (headerBuffer.Count > 64 * 1024) return;
                if (headerBuffer.Count >= 4 &&
                    headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                    headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n')
                    headerEnd = headerBuffer.Count;
            }

            var headerText = Encoding.UTF8.GetString(headerBuffer.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines.Length > 0 ? lines[0] : "";
            var headers = ParseHeaders(lines.Skip(1));
            var method = (requestLine.Split(' ').FirstOrDefault() ?? "").ToUpperInvariant();
            var path = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "/";

            // 读取请求体：优先 Content-Length，兼容 Transfer-Encoding: chunked（安卓投屏客户端常用）
            var body = "";
            if (headers.TryGetValue("Transfer-Encoding", out var te) &&
                te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = ReadChunkedBody(stream);
            }
            else if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0 && len < 8 * 1024 * 1024)
            {
                var bodyBytes = new byte[len];
                var read = 0;
                while (read < len)
                {
                    var n = stream.Read(bodyBytes, read, len - read);
                    if (n == 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(bodyBytes, 0, read);
            }

            string response;
            var contentType = "text/xml; charset=\"utf-8\"";

            if (path == "/description.xml" && method == "GET")
            {
                _log.Info("[DLNA]", $"GET /description.xml from {((IPEndPoint)client.Client.RemoteEndPoint).Address} (UA={(headers.TryGetValue("USER-AGENT", out var ua) ? ua : "?")})");
                response = BuildDeviceDescription();
            }
            else if (path.StartsWith("/scpd/") && method == "GET")
            {
                _log.Info("[DLNA]", $"GET {path} from {((IPEndPoint)client.Client.RemoteEndPoint).Address}");
                response = BuildScpd(path);
            }
            else if (method == "POST" && headers.TryGetValue("SOAPACTION", out var soapAction))
            {
                _log.Info("[DLNA]", $"SOAP {soapAction} from {((IPEndPoint)client.Client.RemoteEndPoint).Address}, body={body.Length}字节 (TE={(headers.TryGetValue("Transfer-Encoding", out var teH) ? teH : "-")}, CL={(headers.TryGetValue("Content-Length", out var clH) ? clH : "-")})");
                response = HandleSoap(soapAction, body);
            }
            else if (method == "SUBSCRIBE" || method == "UNSUBSCRIBE")
            {
                _log.Info("[DLNA]", $"{(method == "SUBSCRIBE" ? "GENA 订阅" : "取消订阅")} from {((IPEndPoint)client.Client.RemoteEndPoint).Address}, 回调={(headers.TryGetValue("CALLBACK", out var cb) ? cb : "?")}");
                // GENA 事件订阅：接受订阅但不主动推送（多数投屏客户端可正常工作）
                response =
                    "HTTP/1.1 200 OK\r\n" +
                    $"SID: uuid:ScreenCastReceiver-{Guid.NewGuid():N}\r\n" +
                    "TIMEOUT: Second-1800\r\n" +
                    "CONTENT-LENGTH: 0\r\n\r\n";
                WriteResponse(stream, response, isRaw: true);
                return;
            }
            else
            {
                response = "HTTP/1.1 404 Not Found\r\n" +
                           "CONTENT-LENGTH: 0\r\n\r\n";
                WriteResponse(stream, response, isRaw: true);
                return;
            }

            var payload = response;
            var resp =
                "HTTP/1.1 200 OK\r\n" +
                $"CONTENT-TYPE: {contentType}\r\n" +
                "SERVER: Windows/10.0 UPnP/1.0 ScreenCastReceiver/1.0\r\n" +
                "EXT:\r\n" +
                $"CONTENT-LENGTH: {Encoding.UTF8.GetByteCount(payload)}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            WriteResponse(stream, resp, isRaw: true);
            WriteResponse(stream, payload, isRaw: false);
        }
        catch (Exception ex)
        {
            _log.Warn("[DLNA]", $"HTTP 连接处理异常: {ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static void WriteResponse(NetworkStream stream, string text, bool isRaw)
    {
        var bytes = isRaw ? Encoding.ASCII.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>SOAP 动作分发。返回 XML 响应体。</summary>
    private string HandleSoap(string soapAction, string body)
    {
        try
        {
            // SOAPACTION: "urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI"
            var m = Regex.Match(soapAction, @"""([^#]+)#([A-Za-z0-9_]+)""");
            if (!m.Success) return SoapError(401);
            var service = m.Groups[1].Value;
            var action = m.Groups[2].Value;

            var args = ExtractSoapArgs(body);
            if (action == "SetAVTransportURI" && !args.ContainsKey("CurrentURI"))
            {
                _log.Warn("[DLNA]", $"SetAVTransportURI 未解析到 CurrentURI。bodyLen={body.Length}, 含闭合</CurrentURI>={body.Contains("</CurrentURI>", StringComparison.Ordinal)}, 含前缀标记:CurrentURI>={body.Contains(":CurrentURI>", StringComparison.Ordinal)}, body全文: {body.Replace("\r", "\\r").Replace("\n", "\\n")}");
            }
            var ns = $"xmlns:u=\"{service}\"";

            return service switch
            {
                AvtServiceType => HandleAvt(action, args, ns),
                CmServiceType => HandleCm(action, ns),
                RcServiceType => HandleRc(action, args, ns),
                _ => SoapError(401)
            };
        }
        catch (Exception ex)
        {
            _log.Error("[DLNA]", $"SOAP 处理异常 {soapAction}: {ex.Message}");
            return SoapError(501);
        }
    }

    private string HandleAvt(string action, Dictionary<string, string> args, string ns)
    {
        switch (action)
        {
            case "SetAVTransportURI":
            {
                var uri = args.GetValueOrDefault("CurrentURI") ?? "";
                var meta = args.GetValueOrDefault("CurrentURIMetaData") ?? "";
                _log.Info("[DLNA]", $"收到投屏视频链接: {uri}");
                if (!string.IsNullOrEmpty(uri))
                    SetUri?.Invoke(uri, meta);
                return SoapSuccess("SetAVTransportURIResponse", ns);
            }
            case "Play":
                Play?.Invoke();
                return SoapSuccess("PlayResponse", ns);
            case "Pause":
                Pause?.Invoke();
                return SoapSuccess("PauseResponse", ns);
            case "Stop":
                Stop?.Invoke();
                return SoapSuccess("StopResponse", ns);
            case "Seek":
            {
                var unit = args.GetValueOrDefault("Unit") ?? "";
                var target = args.GetValueOrDefault("Target") ?? "0";
                var seconds = ParseTime(target);
                _log.Info("[DLNA]", $"收到 Seek 指令 unit={unit} target={target} -> {seconds}s");
                Seek?.Invoke(seconds);
                return SoapSuccess("SeekResponse", ns);
            }
            case "Next":
                Next?.Invoke();
                return SoapSuccess("NextResponse", ns);
            case "Previous":
                Previous?.Invoke();
                return SoapSuccess("PreviousResponse", ns);
            case "GetTransportInfo":
            {
                var state = (TransportStateProvider?.Invoke() ?? 1) switch
                {
                    0 => "STOPPED",
                    2 => "PAUSED_PLAYBACK",
                    _ => "PLAYING"
                };
                return SoapSuccess("GetTransportInfoResponse", ns, new Dictionary<string, string>
                {
                    ["CurrentTransportState"] = state,
                    ["CurrentTransportStatus"] = "OK",
                    ["CurrentSpeed"] = "1"
                });
            }
            case "GetPositionInfo":
            {
                var (pos, dur) = PositionProvider?.Invoke() ?? (0, 0);
                return SoapSuccess("GetPositionInfoResponse", ns, new Dictionary<string, string>
                {
                    ["Track"] = "0",
                    ["TrackDuration"] = FormatTime(dur),
                    ["TrackMetaData"] = "",
                    ["TrackURI"] = "",
                    ["RelTime"] = FormatTime(pos),
                    ["AbsTime"] = "NOT_IMPLEMENTED",
                    ["RelCount"] = "2147483647",
                    ["AbsCount"] = "2147483647"
                });
            }
            case "GetMediaInfo":
            {
                var (_, dur) = PositionProvider?.Invoke() ?? (0, 0);
                return SoapSuccess("GetMediaInfoResponse", ns, new Dictionary<string, string>
                {
                    ["NrTracks"] = "1",
                    ["MediaDuration"] = FormatTime(dur),
                    ["CurrentURI"] = "",
                    ["CurrentURIMetaData"] = "",
                    ["NextURI"] = "NOT_IMPLEMENTED",
                    ["NextURIMetaData"] = "NOT_IMPLEMENTED",
                    ["PlayMedium"] = "NONE",
                    ["RecordMedium"] = "NOT_IMPLEMENTED",
                    ["WriteStatus"] = "NOT_IMPLEMENTED"
                });
            }
            case "GetTransportSettings":
                return SoapSuccess("GetTransportSettingsResponse", ns, new Dictionary<string, string>
                {
                    ["PlayMode"] = "NORMAL",
                    ["RecQualityMode"] = "NOT_IMPLEMENTED"
                });
            default:
                _log.Warn("[DLNA]", $"不支持的 AVTransport 动作: {action}");
                return SoapError(501);
        }
    }

    private string HandleCm(string action, string ns)
    {
        return action switch
        {
            "GetProtocolInfo" => SoapSuccess("GetProtocolInfoResponse", ns, new Dictionary<string, string>
            {
                ["Source"] = "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:application/x-mpegURL:*,http-get:*:audio/mpeg:*",
                ["Sink"] = "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:application/x-mpegURL:*,http-get:*:video/mp2t:*,http-get:*:audio/mpeg:*"
            }),
            "GetCurrentConnectionInfo" => SoapSuccess("GetCurrentConnectionInfoResponse", ns, new Dictionary<string, string>
            {
                ["RcsID"] = "0",
                ["AVTransportID"] = "0",
                ["ProtocolInfo"] = "http-get:*:*:*",
                ["PeerConnectionManager"] = "",
                ["PeerConnectionID"] = "-1",
                ["Direction"] = "Input",
                ["Status"] = "OK"
            }),
            "GetCurrentConnectionIDs" => SoapSuccess("GetCurrentConnectionIDsResponse", ns, new Dictionary<string, string>
            {
                ["ConnectionIDs"] = "0"
            }),
            _ => SoapError(501)
        };
    }

    private string HandleRc(string action, Dictionary<string, string> args, string ns)
    {
        switch (action)
        {
            case "SetVolume":
                _log.Info("[DLNA]", $"收到音量指令: {args.GetValueOrDefault("DesiredVolume")}");
                return SoapSuccess("SetVolumeResponse", ns);
            case "GetVolume":
                return SoapSuccess("GetVolumeResponse", ns, new Dictionary<string, string>
                {
                    ["CurrentVolume"] = "100"
                });
            case "SetMute":
                return SoapSuccess("SetMuteResponse", ns);
            case "GetMute":
                return SoapSuccess("GetMuteResponse", ns, new Dictionary<string, string>
                {
                    ["CurrentMute"] = "0"
                });
            default:
                return SoapError(501);
        }
    }

    private static string SoapSuccess(string action, string ns, Dictionary<string, string>? args = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n");
        sb.Append($"<s:Body><u:{action} {ns}>");
        if (args != null)
            foreach (var kv in args)
                sb.Append($"<{kv.Key}>{System.Security.SecurityElement.Escape(kv.Value)}</{kv.Key}>");
        sb.Append($"</u:{action}></s:Body></s:Envelope>");
        return sb.ToString();
    }

    private static string SoapError(int code)
    {
        var desc = code switch
        {
            401 => "Invalid Action",
            402 => "Invalid Args",
            501 => "Action Failed",
            701 => "No such object",
            _ => "Error"
        };
        return "<?xml version=\"1.0\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>\r\n" +
               "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code +
               "</errorCode><errorDescription>" + desc + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
    }

    // ==================== XML 构建 ====================

    private string BuildDeviceDescription()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\">\r\n");
        sb.Append("<specVersion><major>1</major><minor>0</minor></specVersion>\r\n");
        sb.Append("<device>\r\n");
        sb.Append($"<deviceType>{DeviceType}</deviceType>\r\n");
        sb.Append($"<friendlyName>{System.Security.SecurityElement.Escape(_deviceName)}</friendlyName>\r\n");
        sb.Append("<manufacturer>ScreenCastReceiver</manufacturer>\r\n");
        sb.Append("<modelName>ScreenCastReceiver DMR</modelName>\r\n");
        sb.Append("<UDN>" + _udn + "</UDN>\r\n");
        sb.Append("<serviceList>\r\n");
        sb.Append(BuildService("AVTransport", AvtServiceType, "/ctl/AVTransport", "/evt/AVTransport", "/scpd/AVTransport.xml"));
        sb.Append(BuildService("ConnectionManager", CmServiceType, "/ctl/ConnectionManager", "/evt/ConnectionManager", "/scpd/ConnectionManager.xml"));
        sb.Append(BuildService("RenderingControl", RcServiceType, "/ctl/RenderingControl", "/evt/RenderingControl", "/scpd/RenderingControl.xml"));
        sb.Append("</serviceList>\r\n");
        sb.Append("</device>\r\n</root>\r\n");
        return sb.ToString();
    }

    private static string BuildService(string id, string type, string control, string evt, string scpd)
    {
        return $"<service><serviceType>{type}</serviceType><serviceId>urn:upnp-org:serviceId:{id}</serviceId>" +
               $"<SCPDURL>{scpd}</SCPDURL><controlURL>{control}</controlURL><eventSubURL>{evt}</eventSubURL></service>\r\n";
    }

    private string BuildScpd(string path)
    {
        // 返回完整、符合 UPnP 规范的服务描述（SCPD）。
        // 重要：严格的投屏客户端（如 vivo/B站）会读取 SCPD 中声明的参数，
        // 只发送声明过的参数。若 SetAVTransportURI 未声明 CurrentURI，客户端就不会发视频 URL。
        return path.Contains("AVTransport") ? BuildScpdAvt()
            : path.Contains("ConnectionManager") ? BuildScpdCm()
            : BuildScpdRc();
    }

    private static string BuildScpdAvt()
    {
        var actions = new (string Name, (string Arg, string Dir, string Var)[] Args)[]
        {
            ("SetAVTransportURI", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("CurrentURI", "in", "AVTransportURI"), ("CurrentURIMetaData", "in", "AVTransportURIMetaData") }),
            ("Play", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Speed", "in", "TransportPlaySpeed") }),
            ("Pause", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID") }),
            ("Stop", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID") }),
            ("Seek", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Unit", "in", "A_ARG_TYPE_SeekMode"), ("Target", "in", "A_ARG_TYPE_SeekTarget") }),
            ("Next", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID") }),
            ("Previous", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID") }),
            ("GetTransportInfo", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("CurrentTransportState", "out", "TransportState"), ("CurrentTransportStatus", "out", "TransportStatus"), ("CurrentSpeed", "out", "TransportPlaySpeed") }),
            ("GetPositionInfo", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Track", "out", "CurrentTrack"), ("TrackDuration", "out", "CurrentTrackDuration"), ("TrackMetaData", "out", "CurrentTrackMetaData"), ("TrackURI", "out", "CurrentTrackURI"), ("RelTime", "out", "RelativeTimePosition"), ("AbsTime", "out", "AbsoluteTimePosition"), ("RelCount", "out", "RelativeCounterPosition"), ("AbsCount", "out", "AbsoluteCounterPosition") }),
            ("GetMediaInfo", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("NrTracks", "out", "NumberOfTracks"), ("MediaDuration", "out", "CurrentMediaDuration"), ("CurrentURI", "out", "AVTransportURI"), ("CurrentURIMetaData", "out", "AVTransportURIMetaData"), ("NextURI", "out", "NextAVTransportURI"), ("NextURIMetaData", "out", "NextAVTransportURIMetaData"), ("PlayMedium", "out", "PlaybackStorageMedium"), ("RecordMedium", "out", "RecordStorageMedium"), ("WriteStatus", "out", "RecordWriteStatus") }),
            ("GetTransportSettings", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("PlayMode", "out", "CurrentPlayMode"), ("RecQualityMode", "out", "CurrentRecordQualityMode") })
        };
        var vars = new (string Name, string Type, bool Events)[]
        {
            ("A_ARG_TYPE_InstanceID", "ui4", false), ("AVTransportURI", "string", false), ("AVTransportURIMetaData", "string", false),
            ("TransportPlaySpeed", "string", false), ("A_ARG_TYPE_SeekMode", "string", false), ("A_ARG_TYPE_SeekTarget", "string", false),
            ("TransportState", "string", true), ("TransportStatus", "string", false), ("CurrentTrack", "ui4", false),
            ("CurrentTrackDuration", "string", false), ("CurrentTrackMetaData", "string", false), ("CurrentTrackURI", "string", false),
            ("RelativeTimePosition", "string", false), ("AbsoluteTimePosition", "string", false), ("RelativeCounterPosition", "i4", false),
            ("AbsoluteCounterPosition", "i4", false), ("NumberOfTracks", "ui4", false), ("CurrentMediaDuration", "string", false),
            ("NextAVTransportURI", "string", false), ("NextAVTransportURIMetaData", "string", false), ("PlaybackStorageMedium", "string", false),
            ("RecordStorageMedium", "string", false), ("RecordWriteStatus", "string", false), ("CurrentPlayMode", "string", false),
            ("CurrentRecordQualityMode", "string", false)
        };
        return BuildScpdXml("AVTransport", actions, vars);
    }

    private static string BuildScpdCm()
    {
        var actions = new (string Name, (string Arg, string Dir, string Var)[] Args)[]
        {
            ("GetProtocolInfo", new[] { ("Source", "out", "SourceProtocolInfo"), ("Sink", "out", "SinkProtocolInfo") }),
            ("GetCurrentConnectionInfo", new[] { ("ConnectionID", "in", "A_ARG_TYPE_ConnectionID"), ("RcsID", "out", "A_ARG_TYPE_RcsID"), ("AVTransportID", "out", "A_ARG_TYPE_AVTransportID"), ("ProtocolInfo", "out", "A_ARG_TYPE_ProtocolInfo"), ("PeerConnectionManager", "out", "A_ARG_TYPE_PeerConnectionManager"), ("PeerConnectionID", "out", "A_ARG_TYPE_PeerConnectionID"), ("Direction", "out", "A_ARG_TYPE_Direction"), ("Status", "out", "A_ARG_TYPE_ConnectionStatus") }),
            ("GetCurrentConnectionIDs", new[] { ("ConnectionIDs", "out", "A_ARG_TYPE_ConnectionIDs") })
        };
        var vars = new (string Name, string Type, bool Events)[]
        {
            ("SourceProtocolInfo", "string", false), ("SinkProtocolInfo", "string", false), ("A_ARG_TYPE_ConnectionID", "i4", false),
            ("A_ARG_TYPE_RcsID", "i4", false), ("A_ARG_TYPE_AVTransportID", "i4", false), ("A_ARG_TYPE_ProtocolInfo", "string", false),
            ("A_ARG_TYPE_PeerConnectionManager", "string", false), ("A_ARG_TYPE_PeerConnectionID", "i4", false),
            ("A_ARG_TYPE_Direction", "string", false), ("A_ARG_TYPE_ConnectionStatus", "string", false), ("A_ARG_TYPE_ConnectionIDs", "string", false)
        };
        return BuildScpdXml("ConnectionManager", actions, vars);
    }

    private static string BuildScpdRc()
    {
        var actions = new (string Name, (string Arg, string Dir, string Var)[] Args)[]
        {
            ("SetVolume", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Channel", "in", "Channel"), ("DesiredVolume", "in", "Volume") }),
            ("GetVolume", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Channel", "in", "Channel"), ("CurrentVolume", "out", "Volume") }),
            ("SetMute", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Channel", "in", "Channel"), ("DesiredMute", "in", "Mute") }),
            ("GetMute", new[] { ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Channel", "in", "Channel"), ("CurrentMute", "out", "Mute") })
        };
        var vars = new (string Name, string Type, bool Events)[]
        {
            ("A_ARG_TYPE_InstanceID", "ui4", false), ("Channel", "string", false), ("Volume", "ui2", false), ("Mute", "boolean", false)
        };
        return BuildScpdXml("RenderingControl", actions, vars);
    }

    private static string BuildScpdXml(string serviceName, (string Name, (string Arg, string Dir, string Var)[] Args)[] actions, (string Name, string Type, bool Events)[] vars)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>\r\n");
        sb.Append($"<!-- {serviceName} SCPD -->\r\n");
        sb.Append("<actionList>\r\n");
        foreach (var (name, args) in actions)
        {
            sb.Append($"<action><name>{name}</name><argumentList>\r\n");
            foreach (var (arg, dir, varr) in args)
                sb.Append($"<argument><name>{arg}</name><direction>{dir}</direction><relatedStateVariable>{varr}</relatedStateVariable></argument>\r\n");
            sb.Append("</argumentList></action>\r\n");
        }
        sb.Append("</actionList>\r\n<serviceStateTable>\r\n");
        foreach (var (name, type, events) in vars)
            sb.Append($"<stateVariable sendEvents=\"{(events ? "yes" : "no")}\"><name>{name}</name><dataType>{type}</dataType></stateVariable>\r\n");
        sb.Append("</serviceStateTable></scpd>");
        return sb.ToString();
    }

    // ==================== 工具 ====================

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            dict[key] = value;
        }
        return dict;
    }

    /// <summary>从 SOAP 请求体提取动作参数（Body 下动作节点的直接子元素）。</summary>
    /// <remarks>
    /// 优先用真正的 XML 解析器（可靠处理命名空间前缀 s:/u: 与 &amp;lt; 实体转义）；
    /// 若 body 含非法字符（如未转义 &amp;）导致解析失败，则回退到"只匹配叶子标签"的正则，
    /// 避免旧正则从 &lt;s:Envelope&gt; 开始匹配，把 InstanceID/CurrentURI 全部吞进外层节点的值里。
    /// </remarks>
    private static Dictionary<string, string> ExtractSoapArgs(string body)
    {
        var dict = new Dictionary<string, string>();

        // 1) 首选 XML 解析：按本地名取 Body 下第一个动作节点，取其直接子元素作为参数
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(body);
            // 注意：不能用 GetElementsByTagName("Body")——它按限定名匹配，带前缀的 <s:Body>
            // （Cling/标准 UPnP 客户端普遍如此）会匹配失败返回空，导致 scope 误取 Envelope、
            // action 误取 Body，把 SetAVTransportURI 当参数键拼进 dict 后提前 return，兜底层被跳过。
            // 必须用 local-name() 才能兼容任意命名空间前缀（s: / u: / 无前缀）。
            var bodyNode = doc.SelectSingleNode("//*[local-name()='Body']");
            var scope = bodyNode ?? doc.DocumentElement!;
            XmlNode? action = null;
            foreach (XmlNode child in scope.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    action = child;
                    break;
                }
            }
            if (action != null)
            {
                foreach (XmlNode arg in action.ChildNodes)
                {
                    if (arg.NodeType == XmlNodeType.Element)
                        dict.TryAdd(arg.LocalName, arg.InnerText.Trim());
                }
            }
            // 即使 XML 解析成功也不提前 return：若因结构异常漏提取，
            // 由下方两层用 TryAdd 补充缺失键（不覆盖已有的正确解码值）。
        }
        catch
        {
            // 忽略，走正则回退
        }

        // 2) 正则回退：值内不允许再出现 '<'，保证只匹配叶子元素，不被外层 Envelope 吞并
        foreach (Match m in Regex.Matches(body, @"<(?:[A-Za-z_][\w-]*:)?([A-Za-z_][\w.]*)(?:\s[^<>]*)?>([^<]*)</(?:[A-Za-z_][\w-]*:)?\1\s*>"))
        {
            var value = m.Groups[2].Value.Trim();
            dict.TryAdd(m.Groups[1].Value, value);
        }

        // 3) 标签边界兜底：用正则只定位 <CurrentURI>...</CurrentURI> 的起止（兼容命名空间前缀、属性），
        //    直接截取中间原文。某些客户端 URL 内可能含裸 < / > / & 等字符，XML 解析与普通正则都会失败。
        if (!dict.ContainsKey("CurrentURI"))
        {
            var ms = Regex.Match(body, @"<(?:[A-Za-z_][\w-]*:)?CurrentURI(?:\s[^<>]*)?>");
            if (ms.Success)
            {
                var me = Regex.Match(body, @"</(?:[A-Za-z_][\w-]*:)?CurrentURI\s*>");
                if (me.Success && me.Index > ms.Index + ms.Length)
                    dict["CurrentURI"] = body[(ms.Index + ms.Length)..me.Index].Trim();
            }
        }
        return dict;
    }

    /// <summary>读取 chunked 编码的请求体。</summary>
    private static string ReadChunkedBody(NetworkStream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var sizeLine = ReadAsciiLine(stream);
            if (sizeLine == null) break;
            var semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size))
                break;
            if (size == 0)
            {
                // 读取 trailer 直到空行
                while (true)
                {
                    var t = ReadAsciiLine(stream);
                    if (t == null || t.Length == 0) break;
                }
                break;
            }
            var buf = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = stream.Read(buf, read, size - read);
                if (n == 0) break;
                read += n;
            }
            sb.Append(Encoding.UTF8.GetString(buf, 0, read));
            ReadAsciiLine(stream); // 消费 chunk 块尾 CRLF
        }
        return sb.ToString();
    }

    /// <summary>读取一行 ASCII（以 CRLF 结尾，不含换行符）。</summary>
    private static string? ReadAsciiLine(NetworkStream stream)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 8192)
        {
            var n = stream.Read(one, 0, 1);
            if (n == 0) return buf.Count == 0 ? null : Encoding.ASCII.GetString(buf.ToArray());
            buf.Add(one[0]);
            if (buf.Count >= 2 && buf[^2] == '\r' && buf[^1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray(), 0, buf.Count - 2);
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    /// <summary>把 "HH:MM:SS.mmm" 或秒数解析为秒。</summary>
    private static double ParseTime(string value)
    {
        if (double.TryParse(value, out var sec)) return sec;
        var parts = value.Split(':');
        if (parts.Length == 3)
        {
            var h = double.TryParse(parts[0], out var hv) ? hv : 0;
            var m = double.TryParse(parts[1], out var mv) ? mv : 0;
            var s = double.TryParse(parts[2], out var sv) ? sv : 0;
            return h * 3600 + m * 60 + s;
        }
        return 0;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) return "0:00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>根据对端地址选择本机出口 IP（用于 LOCATION 头，避免多网卡时给错地址）。</summary>
    private IPAddress GetLocalIpFor(IPAddress remote)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(remote, 1900);
            return ((IPEndPoint)udp.Client.LocalEndPoint).Address;
        }
        catch
        {
            return _bindAddresses.FirstOrDefault(a => !IPAddress.IsLoopback(a)) ?? IPAddress.Loopback;
        }
    }

    /// <summary>停止服务：发送 byebye 广播并释放全部 Socket。</summary>
    public void StopServer()
    {
        try { SendNotify("ssdp:byebye"); } catch { }

        _ssdpCts?.Cancel();
        try { _ssdp?.Close(); } catch { }
        _ssdp = null;
        try { _ssdpThread?.Join(2000); } catch { }
        _ssdpThread = null;

        _httpCts?.Cancel();
        try { _http?.Stop(); } catch { }
        _http = null;
        try { _httpThread?.Join(2000); } catch { }
        _httpThread = null;
    }

    public void Dispose() => StopServer();
}
