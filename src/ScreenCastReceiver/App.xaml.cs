using System.Windows;

namespace ScreenCastReceiver;

/// <summary>
/// App 入口：程序退出前统一停掉全部后台服务。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            // 未捕获异常只记录日志，不强制弹窗（业务约束：异常不弹窗）
            try
            {
                Logging.AppLogger.Instance.Error("[APP]", $"未处理异常: {args.Exception}");
            }
            catch { /* 日志失败也忽略 */ }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 主窗口关闭时已按顺序停止全部服务；此处兜底再次停止
        base.OnExit(e);
    }
}
