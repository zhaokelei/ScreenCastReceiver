using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenCastReceiver.Detection;
using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Player;
using ScreenCastReceiver.Services;

namespace ScreenCastReceiver;

/// <summary>
/// 主窗口：GUI 与四个后台服务完全解耦（需求①）。
/// - 每个服务独立开关，互不影响
/// - 投屏抢占确认弹窗
/// - 网卡绑定 / 旋转 / 防火墙 / 日志
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppLogger _log = AppLogger.Instance;
    private readonly MpvSessionManager _mpv;
    private readonly DlnaDmrService _dlna;
    private readonly AirPlayBridgeService _airPlay;
    private readonly MiracastBridgeService _miracast;
    private readonly RtspWebRtcMirrorService _rtsp;

    private readonly Dictionary<ServiceKind, TextBlock> _statusLabels;
    private readonly Dictionary<ServiceKind, TextBlock> _portLabels;
    private readonly Dictionary<ServiceKind, Win32HwndHost> _hosts = new();
    private bool _closing;

    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;
    private bool _scrubbing; // 拖动进度条中（拖动时不回写滑块、释放时才 seek）
    private bool _showSpeed;

    public MainWindow()
    {
        InitializeComponent();

        _mpv = new MpvSessionManager(_log);
        _dlna = new DlnaDmrService(_log, _mpv);
        CmbAspect.SelectedIndex = 0; // _mpv 就绪后再设置默认选中，避免初始化期间事件访问 null
        _airPlay = new AirPlayBridgeService(_log, _mpv);
        _miracast = new MiracastBridgeService(_log, _mpv);
        _rtsp = new RtspWebRtcMirrorService(_log, _mpv);

        _statusLabels = new Dictionary<ServiceKind, TextBlock>
        {
            [ServiceKind.Dlna] = TxtDlnaStatus,
            [ServiceKind.AirPlay2] = TxtAirPlayStatus,
            [ServiceKind.Miracast] = TxtMiracastStatus,
            [ServiceKind.RtspWebRtc] = TxtRtspStatus
        };
        _portLabels = new Dictionary<ServiceKind, TextBlock>
        {
            [ServiceKind.Dlna] = TxtDlnaPort,
            [ServiceKind.AirPlay2] = TxtAirPlayPort,
            [ServiceKind.Miracast] = TxtMiracastPort,
            [ServiceKind.RtspWebRtc] = TxtRtspPort
        };

        // 投屏抢占确认（需求⑧：禁止无提示直接抢占播放画面）
        _mpv.ConflictRequestCallback = (kind, tag) => PromptTakeoverAsync(kind, tag);

        // 激活会话切换时更新播放窗口
        _mpv.ActiveSessionChanged = kind => Dispatcher.Invoke(() => ShowActiveHost(kind));

        foreach (var svc in new ScreenCastServiceBase[] { _dlna, _airPlay, _miracast, _rtsp })
        {
            svc.StateChanged += OnServiceStateChanged;
        }

        // 日志定时冲刷（200ms 一次）
        _logTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _logTimer.Tick += (_, _) => _log.DrainToTextBox(TxtLog, Dispatcher);
        _logTimer.Start();

        // 播放进度定时刷新（500ms 一次）
        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _uiTimer.Tick += (_, _) => RefreshPlaybackUi();
        _uiTimer.Start();

        Loaded += async (_, _) =>
        {
            // 启动即挂载全部播放窗口（HwndHost 挂载后才能创建原生句柄，供 mpv --wid 使用）
            EnsureHostsMounted();
            LoadNetworkAdapters();
            RefreshLanIp();
            await DetectMiracastHardwareAsync();
        };

        // 窗口关闭：强制停止全部服务并释放资源
        Closed += (_, _) => ShutdownAll();
    }

    // ==================== 服务开关（各自独立，互不影响） ====================

    private async void OnDlnaChecked(object sender, RoutedEventArgs e)
    {
        _dlna.SetDeviceName(TxtDlnaName.Text);
        await Task.Run(() => _dlna.StartAsync());
    }

    private async void OnDlnaUnchecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _dlna.StopAsync());

    private async void OnAirPlayChecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _airPlay.StartAsync());

    private async void OnAirPlayUnchecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _airPlay.StopAsync());

    private async void OnMiracastChecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _miracast.StartAsync());

    private async void OnMiracastUnchecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _miracast.StopAsync());

    private async void OnRtspChecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _rtsp.StartAsync());

    private async void OnRtspUnchecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _rtsp.StopAsync());

    private void OnDlnaNameChanged(object sender, TextChangedEventArgs e)
    {
        // XAML 初始化阶段 _dlna 可能尚未创建，需防空
        // 运行中修改名称需要重启服务才生效，仅提示日志
        if (_dlna?.Status == ServiceStatus.Running)
            _log.Info("[DLNA]", $"设备名称已修改为: {TxtDlnaName.Text}（重启 DLNA 服务后生效）");
    }

    // ==================== 服务状态更新 ====================

    private void OnServiceStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var status = _statusLabels[e.Kind];
            status.Text = e.Status switch
            {
                ServiceStatus.Running => "运行中",
                ServiceStatus.Failed => "异常",
                _ => "未启动"
            };
            status.Foreground = e.Status switch
            {
                ServiceStatus.Running => Brushes.Green,
                ServiceStatus.Failed => Brushes.Red,
                _ => Brushes.Gray
            };

            var port = _portLabels[e.Kind];
            port.Text = e.Status == ServiceStatus.Running && e.Port > 0 ? $"端口: {e.Port}" : "";

            if (e.Status == ServiceStatus.Failed)
                status.ToolTip = e.Detail;
        });
    }

    // ==================== 网卡绑定（需求⑨） ====================

    private void LoadNetworkAdapters()
    {
        var (physical, virtuals) = NetworkHelper.GetAllAdapters();
        LstPhysical.Items.Clear();
        foreach (var a in physical)
        {
            var item = new ListBoxItem { Content = a.ToString(), Tag = a };
            item.IsSelected = true; // 默认全部物理网卡绑定
            LstPhysical.Items.Add(item);
        }
        LstVirtual.Items.Clear();
        foreach (var a in virtuals)
            LstVirtual.Items.Add(new ListBoxItem { Content = a.ToString(), Tag = a });

        _log.Info("[网络]", $"发现物理网卡 {physical.Count} 张、虚拟网卡 {virtuals.Count} 张");
        RefreshBindConfig();
    }

    private void OnNicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closing) return;
        RefreshBindConfig();
    }

    private void RefreshBindConfig()
    {
        BindConfig.SelectedPhysical = LstPhysical.Items.Cast<ListBoxItem>()
            .Where(i => i.IsSelected).Select(i => (NetAdapterInfo)i.Tag).ToList();
        BindConfig.SelectedVirtual = LstVirtual.Items.Cast<ListBoxItem>()
            .Where(i => i.IsSelected).Select(i => (NetAdapterInfo)i.Tag).ToList();

        var addrs = BindConfig.GetBindAddresses();
        _log.Info("[网络]", $"绑定网卡更新: {addrs.Count} 个 IPv4 地址" +
                            (addrs.Count > 0 ? $" ({string.Join(",", addrs.Select(a => a.ToString()))})" : ""));
    }

    // ==================== 播放窗口（会话隔离显示） ====================

    /// <summary>挂载全部播放窗口（HwndHost 挂载后才能创建原生句柄，供 mpv --wid 使用）。</summary>
    private void EnsureHostsMounted()
    {
        if (_hosts.Count > 0) return;
        foreach (var k in new[] { ServiceKind.Dlna, ServiceKind.AirPlay2, ServiceKind.Miracast, ServiceKind.RtspWebRtc })
        {
            var host = _mpv.GetHost(k);
            PlaybackHosts.Children.Add(host);
            host.Visibility = Visibility.Collapsed;
            _hosts[k] = host;
        }
    }

    private void ShowActiveHost(ServiceKind? kind)
    {
        EnsureHostsMounted();

        // 仅激活会话可见（会话隔离：四个独立 MPV 渲染目标互不干扰）
        foreach (var kv in _hosts)
        {
            var wasVisible = kv.Value.Visibility == Visibility.Visible;
            var shouldVisible = kv.Key == kind;
            kv.Value.Visibility = shouldVisible ? Visibility.Visible : Visibility.Collapsed;
            // 从隐藏变为显示时，重新绑定 mpv 窗口句柄（解决窗口隐藏时启动 mpv 导致 D3D11 渲染表面初始化失败、有声音无画面）
            if (!wasVisible && shouldVisible && kind.HasValue)
                _mpv.RebindWindow(kind.Value);
        }
        TxtNoSignal.Visibility = kind.HasValue ? Visibility.Collapsed : Visibility.Visible;
    }

    // ==================== 抢占确认弹窗（需求⑧） ====================

    private async Task<bool> PromptTakeoverAsync(ServiceKind kind, string tag)
    {
        var tcs = new TaskCompletionSource<bool>();
        var kindName = kind switch
        {
            ServiceKind.Dlna => "DLNA 视频投屏",
            ServiceKind.AirPlay2 => "AirPlay2 (iOS)",
            ServiceKind.Miracast => "Miracast 镜像",
            _ => "RTSP 安卓镜像"
        };
        Dispatcher.Invoke(() =>
        {
            var r = MessageBox.Show(
                $"新的投屏源 [{kindName}] 请求接入，当前已有其他画面在播放。\n\n是否抢占播放？",
                "新投屏接入确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            tcs.SetResult(r == MessageBoxResult.Yes);
        });
        return await tcs.Task;
    }

    // ==================== 旋转（需求：实时旋转画面，不改变窗口大小） ====================

    private void OnRotationChanged(object sender, RoutedEventArgs e)
    {
        if (!(sender is RadioButton rb) || rb.IsChecked != true) return;
        var angle = rb.Name switch
        {
            nameof(Rot90) => 90,
            nameof(Rot180) => 180,
            nameof(Rot270) => 270,
            _ => 0
        };
        _mpv?.SetRotation(angle);
    }

    // ==================== 播放控制（进度条 / 快进快退 / 播放暂停） ====================

    private void RefreshPlaybackUi()
    {
        var kind = _mpv.ActiveKind;
        if (kind == null)
        {
            if (!_scrubbing) SldProgress.Value = 0;
            TxtPos.Text = "00:00";
            TxtDur.Text = "00:00";
            BtnPlayPause.Content = "▶";
            return;
        }

        var (pos, dur) = _mpv.GetPlaybackPosition(kind.Value);
        if (dur > 0)
        {
            SldProgress.Maximum = dur;
            if (!_scrubbing) SldProgress.Value = Math.Max(0, pos);
        }
        TxtPos.Text = FormatTime(pos);
        TxtDur.Text = FormatTime(dur);
        BtnPlayPause.Content = _mpv.IsPlaying(kind.Value) ? "❚❚" : "▶";
        if (_showSpeed)
        {
            var speed = _mpv.GetSpeed(kind.Value);
            TxtSpeed.Text = $"{speed:F1}x";
            TxtSpeed.Visibility = Visibility.Visible;
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void OnSeekBackward(object sender, RoutedEventArgs e) => SeekRelative(-10);

    private void OnSeekForward(object sender, RoutedEventArgs e) => SeekRelative(10);

    private void SeekRelative(double seconds)
    {
        var kind = _mpv.ActiveKind;
        if (kind == null) return;
        var (pos, _) = _mpv.GetPlaybackPosition(kind.Value);
        _mpv.Seek(kind.Value, Math.Max(0, pos + seconds));
    }

    private void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        var kind = _mpv.ActiveKind;
        if (kind == null) return;
        if (_mpv.IsPlaying(kind.Value)) _mpv.Pause(kind.Value);
        else _mpv.Resume(kind.Value);
    }

    private void OnProgressMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _scrubbing = true;

    private void OnProgressMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _scrubbing = false;
        var kind = _mpv.ActiveKind;
        if (kind == null) return;
        _mpv.Seek(kind.Value, SldProgress.Value);
    }

    private void OnAspectModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mpv == null) return; // XAML 加载期间 _mpv 尚未创建
        var kind = _mpv.ActiveKind;
        if (kind == null) return;
        if (CmbAspect.SelectedItem is ComboBoxItem item && item.Tag is string mode)
        {
            _mpv.SetAspectMode(kind.Value, mode);
        }
    }

    private void OnShowSpeedChanged(object sender, RoutedEventArgs e)
    {
        _showSpeed = ChkShowSpeed.IsChecked == true;
        TxtSpeed.Visibility = _showSpeed ? Visibility.Visible : Visibility.Collapsed;
        if (_showSpeed && _mpv != null) RefreshPlaybackUi();
    }

    // ==================== 防火墙（需求：放 UI 面板，不阻塞使用） ====================

    private void OnAddFirewallRule(object sender, RoutedEventArgs e)
    {
        var (success, output) = FirewallHelper.TryAddRule(
            Environment.ProcessPath ?? AppContext.BaseDirectory,
            new[] { _dlna.ListeningPort, _airPlay.ListeningPort, _rtsp.ListeningPort }.Where(p => p > 0).ToArray(),
            new[] { _miracast.ListeningPort }.Where(p => p > 0).ToArray());
        _log.Info("[防火墙]", output);
        if (!success)
            _log.Warn("[防火墙]", "如未弹出 UAC 授权，请右键“以管理员身份运行”本程序，或复制命令手动执行");
    }

    private void OnCopyFirewallCommand(object sender, RoutedEventArgs e)
    {
        var cmd = FirewallHelper.BuildNetshCommand(
            Environment.ProcessPath ?? AppContext.BaseDirectory,
            new[] { 49152, 5000, 7236, 8554, 8555 },
            new[] { 1900, 7250, 45678 });
        TxtFirewallCommand.Text = cmd;
        try
        {
            Clipboard.SetText(cmd);
            _log.Info("[防火墙]", "netsh 命令已复制到剪贴板（可在管理员命令行中执行）");
        }
        catch (Exception ex)
        {
            _log.Warn("[防火墙]", $"复制失败: {ex.Message}");
        }
    }

    // ==================== 局域网 IP ====================

    private void OnRefreshLanIp(object sender, RoutedEventArgs e) => RefreshLanIp();

    private void RefreshLanIp()
    {
        var ip = NetworkHelper.GetFirstLanIpv4();
        TxtLanIp.Text = ip?.ToString() ?? "未找到局域网IP";
    }

    // ==================== Miracast 硬件检测（需求④） ====================

    private async Task DetectMiracastHardwareAsync()
    {
        TxtMiracastHw.Text = "检测中...";
        var (supported, details) = await Task.Run(MiracastHardwareDetector.Detect);

        Dispatcher.Invoke(() =>
        {
            TxtMiracastHw.Text = supported switch
            {
                true => "支持",
                false => "不支持",
                _ => details.Count > 1 ? "多张网卡结果见日志" : "未知"
            };
            TxtMiracastHw.Foreground = supported == true ? Brushes.Green : Brushes.OrangeRed;
        });

        // 结果写入日志（不弹窗，不灰化复选框，用户仍可手动开启服务）
        if (details.Count == 0)
        {
            _log.Warn("[Miracast]", "硬件检测：未找到无线网卡，Miracast 服务仍可手动开启（可能无法被系统投屏发现）");
        }
        else
        {
            foreach (var d in details)
                _log.Info("[Miracast]", $"硬件检测: {d.AdapterName} -> 无线显示支持={d.Supported?.ToString() ?? "未知"}");
        }
    }

    // ==================== 关闭流程（需求③：强制停止全部服务 → DLL 退出 → 释放端口 → 退出） ====================

    private void OnWindowClosed(object sender, EventArgs e) => ShutdownAll();

    private void ShutdownAll()
    {
        if (_closing) return;
        _closing = true;

        try
        {
            _log.Info("[APP]", "程序退出：正在停止全部后台服务...");

            // 1. 按顺序停止四个服务（每个内部都会先停 DLL 等待其线程退出，再释放 Socket）
            foreach (var svc in new ScreenCastServiceBase[] { _dlna, _airPlay, _miracast, _rtsp })
            {
                svc.StopAsync().GetAwaiter().GetResult();
            }

            // 2. 强制释放原生 DLL（AirPlay/Miracast 的 Free 兜底）
            _airPlay.FreeNative();
            _miracast.Dispose();

            // 3. 释放 MPV 会话
            _mpv.DisposeAll();

            // 4. 日志落盘
            _log.Info("[APP]", "全部服务已停止，端口已释放，程序退出");
            _log.Close();
        }
        catch (Exception ex)
        {
            try { _log.Error("[APP]", $"退出清理异常: {ex.Message}"); } catch { }
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}
