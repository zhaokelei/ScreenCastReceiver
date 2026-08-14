namespace ScreenCastReceiver.Models;

/// <summary>后台服务来源类型（决定日志标签与 MPV 会话隔离）。</summary>
public enum ServiceKind
{
    Dlna
}

/// <summary>服务运行状态。</summary>
public enum ServiceStatus
{
    /// <summary>未启动</summary>
    Stopped,
    /// <summary>运行中</summary>
    Running,
    /// <summary>启动失败/运行异常已停止</summary>
    Failed,
    /// <summary>缺少可选依赖组件（如 DLL 未安装）</summary>
    NotInstalled
}

/// <summary>服务状态变更事件参数。</summary>
public sealed class ServiceStateChangedEventArgs : EventArgs
{
    public ServiceKind Kind { get; }
    public ServiceStatus Status { get; }
    public string Detail { get; }
    public int Port { get; }

    public ServiceStateChangedEventArgs(ServiceKind kind, ServiceStatus status, string detail, int port)
    {
        Kind = kind;
        Status = status;
        Detail = detail;
        Port = port;
    }
}

/// <summary>服务端口信息（实际监听端口）。</summary>
public sealed record PortInfo(int Tcp, int Udp);
