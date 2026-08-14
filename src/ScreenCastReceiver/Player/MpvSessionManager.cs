using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;

namespace ScreenCastReceiver.Player;

/// <summary>
/// 本地回环流转发器：把 DLL 回调的 H264/TS 字节流写入本地 TCP 端口，MPV 通过
/// tcp://127.0.0.1:port 读取并渲染（无需落盘，实时性高）。
/// </summary>
public sealed class LocalStreamForwarder : IDisposable
{
    private readonly object _lock = new();
    private TcpListener? _listener;
    private NetworkStream? _client;
    private Thread? _acceptThread;
    private volatile bool _running;

    /// <summary>实际监听端口。</summary>
    public int Port { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LocalStreamForwarder" };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener!.AcceptTcpClient();
                var stream = client.GetStream();
                lock (_lock)
                {
                    _client?.Dispose();
                    _client = stream;
                }
            }
            catch (Exception)
            {
                if (!_running) break;
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>向当前连接的 MPV 客户端写入数据。</summary>
    public void Write(byte[] data, int offset, int count)
    {
        NetworkStream? client;
        lock (_lock) client = _client;
        if (client == null) return;
        try
        {
            client.Write(data, offset, count);
            client.Flush();
        }
        catch (Exception)
        {
            lock (_lock) { _client = null; }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (_lock) { _client?.Dispose(); _client = null; }
    }
}

/// <summary>
/// 单个 mpv.exe 独立进程 + 命名管道 IPC 的封装。
/// 参考 Macast（xfangfang）的实现：启动 mpv 时指定 --input-ipc-server 命名管道，
/// 通过 JSON 行协议发送命令（loadfile / pause / seek / set_property...），
/// 并监听事件（file-loaded / end-file / property-change）。
/// 使用 --wid 把渲染画面嵌入到 WPF 的 HwndHost 窗口。
/// </summary>
/// <summary>硬件加速模式。</summary>
public enum HwAccelMode
{
    Auto,       // 自动选择
    Nvidia,     // NVIDIA NVENC
    Intel,      // Intel QuickSync
    Amd,        // AMD AMF
    D3D11VA,    // Windows D3D11VA
    Adapter,    // 用户指定具体显卡（配合 HwAccelAdapter）
    Off         // 软解
}

public sealed class MpvProcess : IDisposable
{
    private readonly AppLogger _log;
    private readonly string _tag;
    private readonly Process _proc;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly Thread _readThread;
    private readonly object _sendLock = new();
    private readonly object _cacheLock = new();
    private volatile bool _running = true;

    private double _timePos;
    private double _duration;
    private bool _isPaused;
    private double _speed = 1.0;

    /// <summary>当前硬件加速模式（调试用）。</summary>
    public HwAccelMode HwAccel { get; }

    /// <summary>用户指定的显卡适配器名称（HwAccel=Adapter 时生效，传给 mpv --d3d11-adapter）。</summary>
    public string? HwAccelAdapter { get; }

    /// <summary>媒体加载完成。</summary>
    public event Action? MediaLoaded;

    /// <summary>播放结束（eof / stop / 出错均触发）。</summary>
    public event Action? MediaFinished;

    /// <summary>播放错误（end-file reason=error）。</summary>
    public event Action<string>? MediaError;

    public MpvProcess(AppLogger log, string tag, string mpvExePath, IntPtr hwnd,
        HwAccelMode hwAccel = HwAccelMode.Auto, string? hwAccelAdapter = null)
    {
        _log = log;
        _tag = tag;
        HwAccel = hwAccel;
        HwAccelAdapter = hwAccelAdapter;

        var pipeName = $"screencast_{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{pipeName}";

        var psi = new ProcessStartInfo
        {
            FileName = mpvExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        // --wid 必须用十进制窗口句柄；mpv 0.39+ 支持 --wid=<hwnd>[:<widle>]
        psi.ArgumentList.Add($"--input-ipc-server={pipePath}");
        psi.ArgumentList.Add($"--wid={hwnd.ToInt64()}");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--no-osc");
        psi.ArgumentList.Add("--no-input-default-bindings");
        psi.ArgumentList.Add("--volume=100");
        // 窗口隐藏/尺寸为 0 时启动 mpv，D3D11 渲染表面可能初始化失败导致有声音无画面。
        // force-window=immediate 让 mpv 即使窗口不可见也强制创建渲染窗口，配合下方 RebindWindow 在窗口显示后重设 wid。
        psi.ArgumentList.Add("--force-window=immediate");
        // ========== Windows 硬件加速（可选，由界面下拉框决定）==========
        ApplyHwAccel(psi, hwAccel, hwAccelAdapter);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("mpv.exe 启动失败");
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => { if (_running) _log.Warn(_tag, "mpv.exe 进程意外退出"); };

        _pipe = ConnectPipe(pipeName);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // 注册属性观察：位置/时长（与 Macast 相同的机制，GetPlaybackPosition 直接读缓存）
        SendCommand("observe_property", 1, "time-pos");
        SendCommand("observe_property", 2, "duration");
        SendCommand("observe_property", 3, "pause");
        SendCommand("observe_property", 4, "speed");

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"MpvIpc_{tag}" };
        _readThread.Start();

        _log.Info(_tag, $"mpv 进程已启动: {mpvExePath} (pipe={pipeName}, wid={hwnd.ToInt64()}, hwdec={hwAccel})");
    }

    /// <summary>根据用户选择的硬件加速模式注入 mpv 参数。</summary>
    private static void ApplyHwAccel(ProcessStartInfo psi, HwAccelMode mode, string? adapter = null)
    {
        switch (mode)
        {
            case HwAccelMode.Off:
                // 软解
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=no");
                return;

            case HwAccelMode.Nvidia:
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=nvdec");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                return;

            case HwAccelMode.Intel:
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=d3d11va");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                return;

            case HwAccelMode.Amd:
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=d3d11va");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                return;

            case HwAccelMode.Adapter:
                // 用户指定具体显卡：d3d11va + --d3d11-adapter=<显卡名>
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=d3d11va");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                if (!string.IsNullOrWhiteSpace(adapter))
                    psi.ArgumentList.Add($"--d3d11-adapter={adapter}");
                return;

            case HwAccelMode.D3D11VA:
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=d3d11va");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                psi.ArgumentList.Add("--d3d11va-zero-copy=yes");
                return;

            case HwAccelMode.Auto:
            default:
                // 自动：让 mpv 自己选，但在 Windows 优先 d3d11va
                psi.ArgumentList.Add("--vo=gpu-next");
                psi.ArgumentList.Add("--gpu-api=d3d11");
                psi.ArgumentList.Add("--hwdec=auto");
                psi.ArgumentList.Add("--hwdec-codecs=all");
                return;
        }
    }

    /// <summary>连接 mpv 创建的命名管道（启动后需要重试）。</summary>
    private NamedPipeClientStream ConnectPipe(string pipeName)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_proc.HasExited) throw new InvalidOperationException("mpv.exe 已退出，无法建立 IPC");
            var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(1000);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException("无法连接到 mpv IPC 命名管道");
    }

    /// <summary>发送 mpv JSON 命令：{"command":[...]} + 换行。</summary>
    public void SendCommand(params object[] args)
    {
        var json = JsonSerializer.Serialize(new { command = args });
        lock (_sendLock)
        {
            try
            {
                _writer.WriteLine(json);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                if (_running) _log.Warn(_tag, $"IPC 发送失败: {ex.Message}");
            }
        }
    }

    private void ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false));
            while (_running)
            {
                var line = reader.ReadLine();
                if (line == null) break; // 管道关闭
                Parse(line);
            }
        }
        catch (Exception ex)
        {
            if (_running) _log.Warn(_tag, $"IPC 读取异常: {ex.Message}");
        }
    }

    private void Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("event", out var ev))
            {
                switch (ev.GetString())
                {
                    case "file-loaded":
                        MediaLoaded?.Invoke();
                        break;
                    case "end-file":
                        if (root.TryGetProperty("reason", out var reason))
                        {
                            var reasonStr = reason.GetString();
                            if (reasonStr == "error")
                            {
                                var err = root.TryGetProperty("file_error", out var fe)
                                    ? fe.GetString() ?? "unknown"
                                    : "unknown";
                                MediaError?.Invoke(err);
                                MediaFinished?.Invoke();
                            }
                            else if (reasonStr == "eof")
                            {
                                // 正常播完
                                MediaFinished?.Invoke();
                            }
                            // reason=stop/quit/redirect/unknown 不触发 MediaFinished：
                            // loadfile replace 替换旧文件时旧文件 end-file reason=stop，
                            // 若在此触发会把窗口隐藏，导致"新视频有声音但画面区被折叠"。
                            // 主动停止的复位由 MpvSessionManager.Stop() 显式处理。
                        }
                        break;
                    case "property-change":
                        if (root.TryGetProperty("id", out var idProp) &&
                            root.TryGetProperty("data", out var data))
                        {
                            if (data.ValueKind == JsonValueKind.Null) break;
                            lock (_cacheLock)
                            {
                                var id = idProp.GetInt32();
                                if (id == 1 && data.TryGetDouble(out var tp)) _timePos = tp;
                                else if (id == 2 && data.TryGetDouble(out var du)) _duration = du;
                                else if (id == 3 && data.ValueKind == JsonValueKind.True) _isPaused = true;
                                else if (id == 3 && data.ValueKind == JsonValueKind.False) _isPaused = false;
                                else if (id == 4 && data.TryGetDouble(out var sp)) _speed = sp;
                            }
                        }
                        break;
                }
                return;
            }
        }
        catch (Exception)
        {
            // 忽略无法解析的行
        }
    }

    // ---------- 播放控制 ----------

    public void LoadFile(string uri, bool force, string? demuxer = null, string? lavfFormat = null)
    {
        if (demuxer != null) SendCommand("set_property", "demuxer", demuxer);
        if (lavfFormat != null) SendCommand("set_property", "demuxer-lavf-format", lavfFormat);
        SendCommand("loadfile", uri, force ? "replace" : "append");
        SendCommand("set_property", "pause", false);
    }

    public void Pause() { SendCommand("set_property", "pause", true); _isPaused = true; }

    public void Resume() { SendCommand("set_property", "pause", false); _isPaused = false; }

    public void Stop() => SendCommand("stop");

    /// <summary>运行时切换硬解/软解（只改 hwdec 属性，不重启 mpv 进程，播放/DLNA 连接不中断）。</summary>
    public void SetHwDec(HwAccelMode mode)
    {
        var hwdec = mode switch
        {
            HwAccelMode.Off => "no",
            HwAccelMode.Nvidia => "nvdec",
            HwAccelMode.Intel or HwAccelMode.Amd or HwAccelMode.D3D11VA or HwAccelMode.Adapter => "d3d11va",
            _ => "auto"
        };
        SendCommand("set_property", "hwdec", hwdec);
        SendCommand("set_property", "hwdec-codecs", "all");
        _log.Info(_tag, $"硬件加速已即时切换为: hwdec={hwdec}（无需重启，播放保持）");
    }

    public void Seek(double seconds) => SendCommand("seek", Math.Max(0, seconds), "absolute");

    public void SetRotation(int angle) => SendCommand("set_property", "video-rotate", angle);

    /// <summary>窗口显示后重新绑定 wid，解决窗口隐藏时启动 mpv 导致 D3D11 渲染表面初始化失败（有声音无画面）。
    /// 仅重设 wid（mpv 支持运行时修改并重建渲染表面），不重置 vo，避免播放中重建上下文造成闪烁。</summary>
    public void RebindWindow(IntPtr hwnd)
    {
        SendCommand("set_property", "wid", hwnd.ToInt64());
    }

    public void Quit()
    {
        _running = false;
        try { SendCommand("quit"); } catch { }
        try { _proc.WaitForExit(2000); } catch { }
        try
        {
            if (!_proc.HasExited) _proc.Kill();
        }
        catch { }
    }

    public bool IsPaused => _isPaused;

    public double Speed => _speed;

    public void SetAspectMode(string mode)
    {
        switch (mode)
        {
            case "4:3":
                SendCommand("set_property", "video-aspect-override", "4/3");
                SendCommand("set_property", "keepaspect", true);
                SendCommand("set_property", "panscan", 0.0);
                SendCommand("set_property", "video-unscaled", "no");
                break;
            case "16:9":
                SendCommand("set_property", "video-aspect-override", "16/9");
                SendCommand("set_property", "keepaspect", true);
                SendCommand("set_property", "panscan", 0.0);
                SendCommand("set_property", "video-unscaled", "no");
                break;
            case "stretch":
                SendCommand("set_property", "keepaspect", false);
                SendCommand("set_property", "video-aspect-override", -1);
                SendCommand("set_property", "panscan", 0.0);
                SendCommand("set_property", "video-unscaled", "no");
                break;
            case "tile":
                SendCommand("set_property", "video-unscaled", "yes");
                SendCommand("set_property", "keepaspect", true);
                SendCommand("set_property", "panscan", 0.0);
                SendCommand("set_property", "video-aspect-override", -1);
                break;
            case "fit":
                SendCommand("set_property", "keepaspect", true);
                SendCommand("set_property", "panscan", 0.0);
                SendCommand("set_property", "video-unscaled", "no");
                SendCommand("set_property", "video-aspect-override", -1);
                break;
            case "fill":
                SendCommand("set_property", "keepaspect", true);
                SendCommand("set_property", "panscan", 1.0);
                SendCommand("set_property", "video-unscaled", "no");
                SendCommand("set_property", "video-aspect-override", -1);
                break;
        }
    }

    public (double Position, double Duration) GetPosition()
    {
        lock (_cacheLock) return (_timePos, _duration);
    }

    public void Dispose()
    {
        Quit();
        try { _pipe.Dispose(); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}

/// <summary>
/// MPV 会话管理（需求⑧：会话隔离 + 抢占确认）。
/// - DLNA 来源独立 MPV 会话
/// - 新的投屏接入时如果已有画面在播放，触发抢占确认回调由 GUI 弹窗询问
/// - 所有协议的视频流/URL 统一走独立 mpv.exe 进程渲染（通过命名管道 IPC 控制，
///   不再依赖 Mpv.NET/libmpv 内嵌库，规避 libmpv 1.x/2.x API 兼容问题）
/// </summary>
public sealed class MpvSessionManager : IDisposable
{
    private readonly AppLogger _log;

    /// <summary>抢占确认回调：返回 true=抢占播放，false=拒绝新连接。</summary>
    public Func<ServiceKind, string, Task<bool>>? ConflictRequestCallback { get; set; }

    /// <summary>当前激活会话切换事件（GUI 据此切换显示哪个播放窗口）。</summary>
    public Action<ServiceKind?>? ActiveSessionChanged { get; set; }

    /// <summary>当前激活的会话来源。</summary>
    public ServiceKind? ActiveKind { get; private set; }

    private sealed class Session
    {
        public required ServiceKind Kind { get; init; }
        public required Win32HwndHost Host { get; init; }
        public MpvProcess? Mpv;
        public bool Busy;
    }

    private readonly Dictionary<ServiceKind, Session> _sessions;
    private readonly string _mpvExePath;

    public MpvSessionManager(AppLogger log)
    {
        _log = log;
        _sessions = new Dictionary<ServiceKind, Session>
        {
            [ServiceKind.Dlna] = new() { Kind = ServiceKind.Dlna, Host = new Win32HwndHost() }
        };
        _mpvExePath = LocateMpvExe();
    }

    /// <summary>定位 mpv.exe（程序目录下 mpv\mpv.exe / mpv.exe，或系统 PATH）。</summary>
    private static string LocateMpvExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "libs", "mpv", "mpv.exe")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return "mpv"; // 最后尝试 PATH 中的 mpv
    }

    /// <summary>会话对应的 WPF 宿主（用于界面布局）。</summary>
    public Win32HwndHost GetHost(ServiceKind kind) => _sessions[kind].Host;

    /// <summary>
    /// 请求播放普通 URL（DLNA 视频链接 / RTSP 回环地址）。
    /// 需求⑧：新来源接入且当前有其它来源在播放时，触发抢占确认弹窗；拒绝则返回 false。
    /// </summary>
    public async Task<bool> RequestPlayback(ServiceKind kind, string uri)
    {
        var session = _sessions[kind];
        if (!await EnsureCanTakeover(kind)) return false;

        var tag = TagOf(kind);
        try
        {
            // 必须先激活窗口再创建 mpv：若 HwndHost 处于 Collapsed/0 尺寸时启动 mpv，
            // D3D11 渲染表面初始化失败 → 有声音无画面。先激活让窗口可见并获得布局尺寸，
            // 然后 EnsureMpv 用有效句柄创建/复用进程，最后再加载媒体。
            session.Busy = true;
            ActiveKind = kind;
            ActiveSessionChanged?.Invoke(kind);

            var mpv = EnsureMpv(session);
            mpv.LoadFile(uri, force: true);
            _log.Info(tag, $"开始播放: {uri}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(tag, $"MPV 加载失败（可能是 DRM/加密流）: {ex.Message}");
            return false;
        }
    }

    /// <summary>抢占确认：同来源直接允许；不同来源且正忙时回调 GUI 弹窗。</summary>
    private async Task<bool> EnsureCanTakeover(ServiceKind kind)
    {
        var existing = ActiveKind;
        if (existing.HasValue && existing.Value != kind && _sessions[existing.Value].Busy)
        {
            var ok = ConflictRequestCallback != null &&
                     await ConflictRequestCallback(kind, TagOf(kind));
            if (!ok)
            {
                _log.Info(TagOf(kind), $"用户拒绝抢占（当前来源 {TagOf(existing.Value)} 仍在播放），新连接已拒绝");
                return false;
            }
            _log.Info(TagOf(kind), $"用户确认抢占，原来源 {TagOf(existing.Value)} 已停止");
            StopInternal(_sessions[existing.Value].Mpv);
            _sessions[existing.Value].Busy = false;
        }
        return true;
    }

    /// <summary>当前硬件加速模式（新会话启动时生效）。</summary>
    public HwAccelMode HardwareAcceleration { get; set; } = HwAccelMode.Auto;

    /// <summary>对当前正在播放的所有会话即时切换硬解/软解（不重启 mpv，DLNA 连接不中断）。</summary>
    public void ApplyHwAccelSwitch(HwAccelMode mode)
    {
        HardwareAcceleration = mode;
        foreach (var s in _sessions.Values)
        {
            if (s.Mpv == null) continue;
            try { s.Mpv.SetHwDec(mode); }
            catch (Exception ex) { _log.Warn(TagOf(s.Kind), $"切换硬件加速失败: {ex.Message}"); }
        }
    }

    /// <summary>为会话创建（或复用）MpvProcess 并嵌入窗口。</summary>
    private MpvProcess EnsureMpv(Session session)
    {
        if (session.Mpv != null) return session.Mpv;
        var tag = TagOf(session.Kind);
        try
        {
            var hwnd = session.Host.RenderHandle;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("播放窗口尚未创建完成");
            var mpv = new MpvProcess(_log, tag, _mpvExePath, hwnd, HardwareAcceleration);
            mpv.MediaLoaded += () => _log.Info(tag, "MPV 媒体加载完成");
            mpv.MediaFinished += () =>
            {
                _log.Info(tag, "MPV 播放结束");
                session.Busy = false;
                if (ActiveKind == session.Kind)
                {
                    ActiveKind = null;
                    ActiveSessionChanged?.Invoke(null);
                }
            };
            mpv.MediaError += err => _log.Error(tag, $"MPV 播放错误（{err}，可能是加密/不支持的流）");
            session.Mpv = mpv;
            _log.Info(tag, $"MPV 会话创建完成, exe: {_mpvExePath}");
            return mpv;
        }
        catch (Exception ex)
        {
            _log.Error(tag, $"创建 MPV 会话失败（请确认 mpv 播放器已放入程序目录 mpv\\mpv.exe）: {ex.Message}");
            throw;
        }
    }

    // ---------- 播放控制（DLNA 控制指令 / 界面控制） ----------

    public void Pause(ServiceKind kind) => Invoke(kind, m => m.Pause());
    public void Resume(ServiceKind kind) => Invoke(kind, m => m.Resume());

    /// <summary>主动停止：mpv end-file(reason=stop) 不再触发 MediaFinished，
    /// 因此这里需显式复位会话状态并隐藏播放窗口。</summary>
    public void Stop(ServiceKind kind)
    {
        var s = _sessions[kind];
        if (s.Mpv != null)
        {
            try { s.Mpv.Stop(); } catch { }
        }
        s.Busy = false;
        if (ActiveKind == kind)
        {
            ActiveKind = null;
            ActiveSessionChanged?.Invoke(null);
        }
    }

    public void Seek(ServiceKind kind, double seconds)
    {
        var s = _sessions[kind];
        if (s.Mpv == null) return;
        try { s.Mpv.Seek(seconds); }
        catch (Exception ex) { _log.Warn(TagOf(kind), $"Seek 失败: {ex.Message}"); }
    }

    /// <summary>窗口显示后重新绑定 mpv 渲染目标，解决隐藏状态下启动导致的有声音无画面。</summary>
    public void RebindWindow(ServiceKind kind)
    {
        var s = _sessions[kind];
        if (s.Mpv == null) return;
        var hwnd = s.Host.RenderHandle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            s.Mpv.RebindWindow(hwnd);
            _log.Info(TagOf(kind), $"MPV 窗口已重新绑定 (wid={hwnd.ToInt64()})");
        }
        catch (Exception ex)
        {
            _log.Warn(TagOf(kind), $"窗口重绑定失败: {ex.Message}");
        }
    }

    /// <summary>画面旋转：0/90/180/270，仅旋转画面不改变窗口大小。</summary>
    public void SetRotation(int angle)
    {
        angle = ((angle % 360) + 360) % 360;
        foreach (var s in _sessions.Values)
        {
            if (s.Mpv == null) continue;
            try { s.Mpv.SetRotation(angle); }
            catch (Exception ex)
            {
                _log.Warn(TagOf(s.Kind), $"设置旋转失败: {ex.Message}");
            }
        }
        _log.Info("[MPV]", $"画面旋转设置为 {angle}°");
    }

    /// <summary>查询会话当前位置（DLNA GetPositionInfo 用）。</summary>
    public (double Position, double Duration) GetPlaybackPosition(ServiceKind kind)
    {
        var s = _sessions[kind];
        if (s.Mpv == null) return (0, 0);
        return s.Mpv.GetPosition();
    }

    public bool IsPlaying(ServiceKind kind)
    {
        var s = _sessions[kind];
        return s.Mpv != null && s.Busy && !s.Mpv.IsPaused;
    }

    public void SetAspectMode(ServiceKind kind, string mode)
    {
        var s = _sessions[kind];
        if (s.Mpv == null) return;
        try { s.Mpv.SetAspectMode(mode); }
        catch (Exception ex) { _log.Warn(TagOf(kind), $"设置画面比例失败: {ex.Message}"); }
    }

    public double GetSpeed(ServiceKind kind)
    {
        var s = _sessions[kind];
        return s.Mpv?.Speed ?? 1.0;
    }

    private void Invoke(ServiceKind kind, Action<MpvProcess> action)
    {
        var s = _sessions[kind];
        if (s.Mpv == null) return;
        try { action(s.Mpv); }
        catch (Exception ex) { _log.Warn(TagOf(kind), $"控制指令失败: {ex.Message}"); }
    }

    private static void StopInternal(MpvProcess? mpv)
    {
        if (mpv == null) return;
        try { mpv.Stop(); } catch { /* 忽略停止异常 */ }
    }

    private static string TagOf(ServiceKind kind) => "[DLNA]";

    /// <summary>程序退出前释放全部 MPV 会话。</summary>
    public void DisposeAll()
    {
        foreach (var s in _sessions.Values)
        {
            try { s.Mpv?.Dispose(); } catch { }
            s.Busy = false;
        }
        ActiveKind = null;
        ActiveSessionChanged?.Invoke(null);
    }

    public void Dispose() => DisposeAll();
}
