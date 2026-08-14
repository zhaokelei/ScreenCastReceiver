using System.Collections.Concurrent;
using System.Windows.Threading;

namespace ScreenCastReceiver.Logging;

/// <summary>
/// 线程安全内存日志器（纯内存，不落盘）。
/// - 每条日志带来源标签（[DLNA]）
/// - 通过事件推送到 GUI 文本框
/// - 只占用内存缓冲，不写任何磁盘文件
/// </summary>
public sealed class AppLogger
{
    public static AppLogger Instance { get; } = new();

    /// <summary>日志级别</summary>
    public enum Level
    {
        Info,
        Warn,
        Error
    }

    /// <summary>日志消息事件（GUI 订阅，需自行切回 UI 线程）。</summary>
    public event Action<string>? MessagePublished;

    /// <summary>内存缓冲上限（超出丢弃最旧，防止长时间运行无限占用内存）。</summary>
    private const int MaxBufferedLines = 2000;

    private readonly ConcurrentQueue<string> _pending = new();
    // 队列满时执行"丢最旧 + 入队"需要原子完成，避免多线程写入时超过上限
    private readonly object _sync = new();

    private AppLogger()
    {
    }

    /// <summary>记录一条日志（含来源标签）。</summary>
    public void Log(string tag, string message, Level level = Level.Info)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {tag} [{level}] {message}";
        // 内存缓冲上限控制：超出时丢弃最旧一条（加锁保证并发下不超过上限）
        lock (_sync)
        {
            if (_pending.Count >= MaxBufferedLines)
                _pending.TryDequeue(out _);
            _pending.Enqueue(line);
        }
        MessagePublished?.Invoke(line);
    }

    public void Info(string tag, string message) => Log(tag, message, Level.Info);
    public void Warn(string tag, string message) => Log(tag, message, Level.Warn);
    public void Error(string tag, string message) => Log(tag, message, Level.Error);

    /// <summary>文本框保留的最大行数（超出删除最旧行，防止 UI 控件文本无限增长爆内存）。</summary>
    private const int MaxTextBoxLines = 1000;

    /// <summary>供 GUI 用 Dispatcher 定时冲刷日志队列到文本框（避免高频跨线程）。</summary>
    public void DrainToTextBox(System.Windows.Controls.TextBox box, Dispatcher dispatcher)
    {
        if (_pending.IsEmpty) return;
        var lines = new List<string>();
        while (_pending.TryDequeue(out var l))
        {
            lines.Add(l);
            if (lines.Count >= 200) break;
        }
        dispatcher.Invoke(() =>
        {
            box.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
            // 行数上限：一次性删除最旧的超限行，保证文本框内存占用有界
            var overflow = box.LineCount - MaxTextBoxLines;
            if (overflow > 0)
            {
                var cut = box.GetCharacterIndexFromLineIndex(overflow);
                if (cut > 0) box.Text = box.Text.Remove(0, cut);
            }
            box.ScrollToEnd();
        });
    }

    /// <summary>无文件句柄，保留空实现以兼容调用方。</summary>
    public void Close()
    {
        // 纯内存日志，无需释放外部资源
    }
}
