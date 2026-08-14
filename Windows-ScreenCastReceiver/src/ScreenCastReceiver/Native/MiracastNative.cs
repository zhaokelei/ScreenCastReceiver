using System.IO;
using System.Runtime.InteropServices;

namespace ScreenCastReceiver.Native;

/// <summary>
/// Miracast-Windows x64 DLL 的 P/Invoke 封装。
///
/// 接口规范（libs/Miracast-dll/export_interface.h 与之对应）：
///   int  miracast_init(const MiracastCallbacks* cb);  // 注册回调
///   int  miracast_start(int udpRtpPort);              // 启动接收，默认 UDP 7250
///   int  miracast_stop();                             // 阻塞直到 DLL 内部线程退出并释放端口
///   int  miracast_free();
///
/// 回调约定：OnTsPacket 输出 MPEG-TS 数据（H264+AAC 封装），由 C# 侧转发给 MPV 渲染。
/// DLL 获取方式见 libs/README.md（基于开源 Miracast_Windows/Miracast-Sink 系源码编译）。
///
/// 生命周期约束（需求③）：Stop() 必须阻塞等待 DLL 内部线程退出，C# 侧再确认端口已释放。
/// </summary>
public static class MiracastNative
{
    /// <summary>回调类型（必须保持为静态字段引用，防止被 GC 回收）。</summary>
    public delegate void TsPacketCallback(IntPtr data, int length);

    public delegate void StateCallback(int state);          // 0=断开 1=已连接 2=流启动
    public delegate void TextCallback(IntPtr textUtf8);

    /// <summary>与 export_interface.h 的 MiracastCallbacks 结构一一对应。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MiracastCallbacks
    {
        public TsPacketCallback OnTsPacket;
        public StateCallback OnState;
        public TextCallback OnText;
        public IntPtr UserData;
    }

    [DllImport("Miracast.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int miracast_init(ref MiracastCallbacks callbacks);

    [DllImport("Miracast.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int miracast_start(int udpRtpPort);

    [DllImport("Miracast.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int miracast_stop();

    [DllImport("Miracast.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int miracast_free();

    private static MiracastCallbacks _callbacks;
    private static bool _initialized;
    private static bool _started;

    /// <summary>检查 DLL 是否就位。</summary>
    public static bool DllAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Miracast.dll"));

    public static void Init(MiracastCallbacks callbacks)
    {
        if (_initialized) throw new InvalidOperationException("MiracastNative 已初始化");
        _callbacks = callbacks;
        var rc = miracast_init(ref _callbacks);
        if (rc != 0) throw new Exception($"miracast_init 失败, code={rc}");
        _initialized = true;
    }

    public static void Start(int udpRtpPort)
    {
        if (!_initialized) throw new InvalidOperationException("未初始化");
        var rc = miracast_start(udpRtpPort);
        if (rc != 0) throw new Exception($"miracast_start 失败, code={rc}");
        _started = true;
    }

    /// <summary>停止服务：阻塞等待 DLL 内部线程退出。</summary>
    public static void Stop()
    {
        if (!_started) return;
        var rc = miracast_stop();
        if (rc != 0) throw new Exception($"miracast_stop 失败, code={rc}");
        _started = false;
    }

    public static void Free()
    {
        if (!_initialized) return;
        try { miracast_free(); } catch { /* 忽略释放异常 */ }
        _initialized = false;
    }
}
