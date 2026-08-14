using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCastReceiver.Helpers;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Player;
using ScreenCastReceiver.Services;

namespace ScreenCastReceiver;

/// <summary>
/// 主窗口：GUI 与 DLNA 后台服务解耦。
/// - 服务独立开关
/// - 投屏抢占确认弹窗
/// - 网卡绑定 / 旋转 / 防火墙 / 日志
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppLogger _log = AppLogger.Instance;
    private readonly MpvSessionManager _mpv;
    private readonly DlnaDmrService _dlna;

    private readonly Dictionary<ServiceKind, TextBlock> _statusLabels;
    private readonly Dictionary<ServiceKind, TextBlock> _portLabels;
    private readonly Dictionary<ServiceKind, Win32HwndHost> _hosts = new();
    private bool _closing;

    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;
    private bool _scrubbing; // 拖动进度条中（拖动时不回写滑块、释放时才 seek）
    private bool _showSpeed;
    private bool _isFullscreen;
    private GridLength _sidebarWidth;

    public MainWindow()
    {
        InitializeComponent();

        _mpv = new MpvSessionManager(_log);
        _dlna = new DlnaDmrService(_log, _mpv);
        CmbAspect.SelectedIndex = 0; // _mpv 就绪后再设置默认选中，避免初始化期间事件访问 null

        _statusLabels = new Dictionary<ServiceKind, TextBlock>
        {
            [ServiceKind.Dlna] = TxtDlnaStatus
        };
        _portLabels = new Dictionary<ServiceKind, TextBlock>
        {
            [ServiceKind.Dlna] = TxtDlnaPort
        };

        // 投屏抢占确认（需求⑧：禁止无提示直接抢占播放画面）
        _mpv.ConflictRequestCallback = (kind, tag) => PromptTakeoverAsync(kind, tag);

        // 激活会话切换时更新播放窗口
        _mpv.ActiveSessionChanged = kind => Dispatcher.Invoke(() => ShowActiveHost(kind));

        _dlna.StateChanged += OnServiceStateChanged;

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

        Loaded += (_, _) =>
        {
            // 启动即挂载全部播放窗口（HwndHost 挂载后才能创建原生句柄，供 mpv --wid 使用）
            EnsureHostsMounted();
            LoadNetworkAdapters();
            RefreshLanIp();
        };

        // 窗口关闭：强制停止全部服务并释放资源
        Closed += (_, _) => ShutdownAll();
    }

    // ==================== 服务开关（各自独立，互不影响） ====================

    private async void OnDlnaChecked(object sender, RoutedEventArgs e)
    {
        ApplyDlnaSettings();
        await Task.Run(() => _dlna.StartAsync());
    }

    private async void OnDlnaUnchecked(object sender, RoutedEventArgs e)
        => await Task.Run(() => _dlna.StopAsync());

    private void ApplyDlnaSettings()
    {
        _dlna.SetDeviceName(TxtDlnaName.Text);
        if (int.TryParse(TxtDlnaPortInput.Text, out var port) && port >= 0 && port <= 65535)
            _dlna.Port = port;
        else
            _dlna.Port = 0;
    }

    private void OnDlnaNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_dlna?.Status == ServiceStatus.Running)
            _log.Info("[DLNA]", $"设备名称已修改为: {TxtDlnaName.Text}（重启 DLNA 服务后生效）");
    }

    private void OnDlnaPortChanged(object sender, TextChangedEventArgs e)
    {
        if (_dlna?.Status == ServiceStatus.Running)
            _log.Info("[DLNA]", $"端口已修改为: {TxtDlnaPortInput.Text}（重启 DLNA 服务后生效，0=自动）");
    }

    private void OnPortPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>硬解/软解开关：即时切换当前播放，不重启 mpv、不断开 DLNA。</summary>
    private void OnHwAccelSwitchChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 加载 XAML 期间 IsChecked="True" 会提前触发本事件，
        // 此时 _mpv 与 TxtHwAccelStatus 均未创建，必须直接跳过
        if (_mpv == null || TxtHwAccelStatus == null) return;
        var mode = ChkHwAccel.IsChecked == true ? HwAccelMode.Auto : HwAccelMode.Off;
        _mpv.HardwareAcceleration = mode;
        _mpv.ApplyHwAccelSwitch(mode); // 对正在播放的会话实时生效
        TxtHwAccelStatus.Text = ChkHwAccel.IsChecked == true
            ? "当前: 硬解 (D3D11VA) · 已即时应用，投屏不会中断"
            : "当前: 软解 (CPU) · 已即时应用，投屏不会中断";
        TxtHwAccelStatus.Visibility = Visibility.Visible;
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
        foreach (var k in new[] { ServiceKind.Dlna })
        {
            var host = _mpv.GetHost(k);
            host.DoubleClicked += () => Dispatcher.Invoke(ToggleFullscreen);
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
        var kindName = "DLNA 视频投屏";
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

    // ==================== 局域网 IP ====================

    private void OnRefreshLanIp(object sender, RoutedEventArgs e) => RefreshLanIp();

    private void RefreshLanIp()
    {
        var ip = NetworkHelper.GetFirstLanIpv4();
        TxtLanIp.Text = ip?.ToString() ?? "未找到局域网IP";
    }

    // ==================== 全屏（按钮 / 双击 / ESC）====================

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnVideoMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _sidebarWidth = LeftColumn.Width;
            LeftColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            BtnFullscreen.Content = "⛶ 退出全屏";
            BtnFullscreen.Opacity = 0.4;
        }
        else
        {
            LeftColumn.Width = _sidebarWidth.IsStar || _sidebarWidth.Value <= 0
                ? new GridLength(360)
                : _sidebarWidth;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            BtnFullscreen.Content = "⛶ 全屏";
            BtnFullscreen.Opacity = 0.7;
        }
    }

    // ==================== 关闭流程（需求③：强制停止全部服务 → 释放端口 → 退出） ====================

    private void OnWindowClosed(object sender, EventArgs e) => ShutdownAll();

    private void ShutdownAll()
    {
        if (_closing) return;
        _closing = true;

        try
        {
            _log.Info("[APP]", "程序退出：正在停止全部后台服务...");

            // 1. 停止 DLNA 服务
            _dlna.StopAsync().GetAwaiter().GetResult();

            // 2. 释放 MPV 会话
            _mpv.DisposeAll();

            // 3. 日志落盘
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
