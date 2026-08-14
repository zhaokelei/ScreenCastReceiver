using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;

namespace ScreenCastReceiver.Logging;

/// <summary>
/// 线程安全日志器。
/// - 每条日志带来源标签（[DLNA]）
/// - 通过事件推送到 GUI 文本框
/// - 同时落盘到 Logs 目录
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

    private readonly ConcurrentQueue<string> _pending = new();
    private StreamWriter? _file;
    private readonly object _fileLock = new();

    private AppLogger()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"screencast_{DateTime.Now:yyyyMMdd}.log");
            _file = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch
        {
            // 日志文件失败不影响运行
        }
    }

    /// <summary>记录一条日志（含来源标签）。</summary>
    public void Log(string tag, string message, Level level = Level.Info)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {tag} [{level}] {message}";
        _pending.Enqueue(line);
        lock (_fileLock)
        {
            try { _file?.WriteLine(line); } catch { /* 忽略文件写失败 */ }
        }
        MessagePublished?.Invoke(line);
    }

    public void Info(string tag, string message) => Log(tag, message, Level.Info);
    public void Warn(string tag, string message) => Log(tag, message, Level.Warn);
    public void Error(string tag, string message) => Log(tag, message, Level.Error);

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
            box.ScrollToEnd();
        });
    }

    public void Close()
    {
        lock (_fileLock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }
}
