using System.IO;
using System.Runtime.InteropServices;

namespace ScreenCastReceiver.Native;

/// <summary>
/// RPiPlay (AirPlay2) x64 DLL 的 P/Invoke 封装。
///
/// 接口规范（libs/RPiPlay-dll/export_interface.h 与之对应）：
///   int  rpiplay_init(const RPiPlayCallbacks* cb);   // 注册回调，回调在 DLL 内部线程触发
///   int  rpiplay_set_name(const char* name);
///   int  rpiplay_start(int listenPort);              // 启动 AirPlay 服务（默认 7000）
///   int  rpiplay_stop();                             // 阻塞直到 DLL 内部线程全部退出并释放端口
///   int  rpiplay_free();
///
/// DLL 获取方式见 libs/README.md（基于开源 FD-/RPiPlay 源码编译 Windows x64 DLL，
/// 使 H.264 透传输出到 OnVideoSample 回调）。
///
/// 生命周期约束（需求②）：Stop() 必须阻塞等待 DLL 内部线程退出，C# 侧再确认端口已释放。
/// </summary>
public static class RPiPlayNative
{
    /// <summary>回调类型（必须保持为静态字段引用，防止被 GC 回收）。</summary>
    public delegate void VideoSampleCallback(
        IntPtr data, int length, double ptsSeconds, int isKeyFrame);

    public delegate void AudioSampleCallback(
        IntPtr data, int length, double ptsSeconds, int sampleRate, int channels);

    public delegate void MirrorStateCallback(int mirroring);      // 0=结束 1=开始
    public delegate void PlaybackStateCallback(int state);        // 0=stop 1=play 2=pause
    public delegate void VolumeCallback(float volume);
    public delegate void TextCallback(IntPtr textUtf8);

    /// <summary>与 export_interface.h 的 RPiPlayCallbacks 结构一一对应。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RPiPlayCallbacks
    {
        public VideoSampleCallback OnVideoSample;
        public AudioSampleCallback OnAudioSample;
        public MirrorStateCallback OnMirrorState;
        public PlaybackStateCallback OnPlaybackState;
        public VolumeCallback OnSetVolume;
        public TextCallback OnText;
        public IntPtr UserData;
    }

    [DllImport("RPiPlay.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rpiplay_init(ref RPiPlayCallbacks callbacks);

    [DllImport("RPiPlay.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rpiplay_set_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("RPiPlay.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rpiplay_start(int listenPort);

    [DllImport("RPiPlay.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rpiplay_stop();

    [DllImport("RPiPlay.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rpiplay_free();

    // 回调句柄持有（防止 GC 回收导致原生侧崩溃）
    private static RPiPlayCallbacks _callbacks;
    private static bool _initialized;
    private static bool _started;

    /// <summary>检查 DLL 是否就位。</summary>
    public static bool DllAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "RPiPlay.dll"));

    /// <summary>初始化并注册回调（只能调用一次）。</summary>
    public static void Init(RPiPlayCallbacks callbacks)
    {
        if (_initialized) throw new InvalidOperationException("RPiPlayNative 已初始化");
        _callbacks = callbacks;
        var rc = rpiplay_init(ref _callbacks);
        if (rc != 0) throw new Exception($"rpiplay_init 失败, code={rc}");
        _initialized = true;
    }

    public static void SetDeviceName(string name) => rpiplay_set_name(name);

    /// <summary>启动服务。port 由上层 PortProbe 探测后传入。</summary>
    public static void Start(int port)
    {
        if (!_initialized) throw new InvalidOperationException("未初始化");
        var rc = rpiplay_start(port);
        if (rc != 0) throw new Exception($"rpiplay_start 失败, code={rc}");
        _started = true;
    }

    /// <summary>
    /// 停止服务：阻塞等待 DLL 内部线程退出（接口规范要求 stop 内部 Join 全部线程）。
    /// </summary>
    public static void Stop()
    {
        if (!_started) return;
        var rc = rpiplay_stop();
        if (rc != 0) throw new Exception($"rpiplay_stop 失败, code={rc}");
        _started = false;
    }

    /// <summary>释放全部资源（进程退出前调用）。</summary>
    public static void Free()
    {
        if (!_initialized) return;
        try { rpiplay_free(); } catch { /* 忽略释放异常 */ }
        _initialized = false;
    }
}
