using System.IO;
using System.Runtime.InteropServices;

namespace ScreenCastReceiver.Native;

/// <summary>
/// go2rtc (RTSP/WebRTC) x64 DLL 的 P/Invoke 封装。
///
/// 接口规范（libs/go2rtc-bridge/bridge.go 与之对应，go build -buildmode=c-shared）：
///   int32 go2rtc_start(const char* configJson);   // 传入 {"rtsp":{"listen":":8554"},...} 配置
///   void  go2rtc_stop();                          // 阻塞直到内部所有协程/端口退出
///   int32 go2rtc_is_running();
///
/// 功能：Windows 侧作为 RTSP 服务端监听端口，接收安卓端（本工程 Android-ScreenPush）
///       推上来的 H264/H265+AAC 流（rtsp://PC-IP:port/screen），再交给 MPV 渲染；
///       同时支持 WebRTC 播放（备用）。
///
/// 生命周期约束（需求⑤）：Stop() 必须阻塞等待 DLL 内部线程退出，C# 侧再确认端口已释放。
/// </summary>
public static class Go2RtcNative
{
    [DllImport("go2rtc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int go2rtc_start([MarshalAs(UnmanagedType.LPUTF8Str)] string configJson);

    [DllImport("go2rtc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void go2rtc_stop();

    [DllImport("go2rtc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int go2rtc_is_running();

    private static bool _started;

    /// <summary>检查 DLL 是否就位。</summary>
    public static bool DllAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "go2rtc.dll"));

    /// <summary>启动 go2rtc（阻塞至就绪或失败），configJson 示例：
    /// {"log":{"level":"warn"},"rtsp":{"listen":":8554"},"api":{"listen":"127.0.0.1:1984"},"webrtc":{"listen":":8555"}}
    /// </summary>
    public static void Start(string configJson)
    {
        var rc = go2rtc_start(configJson);
        if (rc != 0) throw new Exception($"go2rtc_start 失败, code={rc}");
        _started = true;
    }

    /// <summary>停止 go2rtc（阻塞直到内部线程退出、端口释放）。</summary>
    public static void Stop()
    {
        if (!_started) return;
        go2rtc_stop();
        _started = false;
    }

    public static bool IsRunning()
    {
        try { return _started && go2rtc_is_running() != 0; }
        catch { return false; }
    }
}
