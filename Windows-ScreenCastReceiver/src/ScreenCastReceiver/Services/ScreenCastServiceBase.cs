using Microsoft.Win32;
using ScreenCastReceiver.Logging;
using ScreenCastReceiver.Models;
using ScreenCastReceiver.Player;

namespace ScreenCastReceiver.Services;

/// <summary>
/// 后台服务基类（需求⑩）：
/// - 使用 CancellationTokenSource 安全取消
/// - 网络断开 / 休眠唤醒后校验 Socket 状态，异常则自动停止本服务并输出日志（由用户手动重启）
/// - 每个服务完全独立：启动失败不影响其它服务（由调用方逐个 StartAsync）
/// </summary>
public abstract class ScreenCastServiceBase : IDisposable
{
    protected readonly AppLogger Log;
    protected readonly MpvSessionManager Mpv;

    /// <summary>服务类型。</summary>
    public ServiceKind Kind { get; }

    /// <summary>日志来源标签（需求⑪）。</summary>
    public string Tag { get; }

    public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;

    /// <summary>状态附加说明（如异常原因）。</summary>
    public string StatusDetail { get; private set; } = "";

    /// <summary>实际监听端口（探测后写入）。</summary>
    public int ListeningPort { get; protected set; }

    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    protected CancellationTokenSource _cts = new();
    private bool _disposed;

    protected ScreenCastServiceBase(ServiceKind kind, AppLogger log, MpvSessionManager mpv)
    {
        Kind = kind;
        Log = log;
        Mpv = mpv;
        Tag = "[DLNA]";

        // 休眠/唤醒 + 网络变化监听（需求⑩）
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>启动服务（内部捕获异常，不影响其它服务）。</summary>
    public async Task<bool> StartAsync()
    {
        if (Status == ServiceStatus.Running) return true;
        SetStatus(ServiceStatus.Stopped, "启动中...", ListeningPort);
        try
        {
            _cts = new CancellationTokenSource();
            await StartCoreAsync(_cts.Token);
            SetStatus(ServiceStatus.Running, "运行中", ListeningPort);
            Log.Info(Tag, $"服务已启动, 监听端口={ListeningPort}");
            return true;
        }
        catch (Exception ex)
        {
            // 启动失败仅输出普通异常日志，不弹窗、不阻断其它服务（业务约束）
            SetStatus(ServiceStatus.Failed, ex.Message, 0);
            if (ex.Message.Contains("未找到") && ex.Message.Contains(".dll"))
                Log.Warn(Tag, $"可选组件未安装: {ex.Message}");
            else
                Log.Error(Tag, $"启动失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>停止服务：先通知外部 DLL 退出并等待其线程退出，再释放 Socket（需求⑩）。</summary>
    public async Task StopAsync()
    {
        if (Status == ServiceStatus.Stopped) return;
        SetStatus(ServiceStatus.Stopped, "停止中...", ListeningPort);
        try
        {
            await StopCoreAsync();
            Log.Info(Tag, "服务已停止，端口已释放");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"停止过程异常: {ex.Message}");
        }
        finally
        {
            _cts.Cancel();
            SetStatus(ServiceStatus.Stopped, "未启动", 0);
        }
    }

    protected abstract Task StartCoreAsync(CancellationToken ct);
    protected abstract Task StopCoreAsync();

    /// <summary>校验本服务的 Socket 状态（休眠唤醒/网络变化后调用）。</summary>
    protected virtual bool ValidateSockets() => true;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Task.Run(async () =>
        {
            await Task.Delay(3000); // 等待网卡恢复
            if (Status != ServiceStatus.Running) return;
            if (!ValidateSockets())
            {
                Log.Warn(Tag, "休眠唤醒后 Socket 状态异常，自动停止本服务（请手动重新启动）");
                await StopAsync();
            }
            else
            {
                Log.Info(Tag, "休眠唤醒后 Socket 状态正常");
            }
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (Status != ServiceStatus.Running) return;
            if (!ValidateSockets())
            {
                Log.Warn(Tag, "检测到网络断开，Socket 状态异常，自动停止本服务（请手动重新启动）");
                await StopAsync();
            }
        });
    }

    protected void SetStatus(ServiceStatus status, string detail, int port)
    {
        Status = status;
        StatusDetail = detail;
        ListeningPort = port;
        StateChanged?.Invoke(this, new ServiceStateChangedEventArgs(Kind, status, detail, port));
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        try { _cts.Cancel(); _cts.Dispose(); } catch { }
    }
}
