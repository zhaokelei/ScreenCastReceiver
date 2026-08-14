using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenCastReceiver.Player;

/// <summary>
/// 在 WPF 中承载原生窗口的 HwndHost。
/// libmpv (Mpv.NET) 需要原生 HWND 作为渲染目标，WPF 无原生句柄，
/// 因此通过 HwndHost 创建一个子窗口并把句柄交给 MpvPlayer。
/// </summary>
public sealed class Win32HwndHost : HwndHost
{
    private const string WndClassName = "MpvHostWindow";
    private static bool _classRegistered;

    private static readonly IntPtr WmEraseBackground = new(0x0014);

    private IntPtr _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hInstance = Marshal.GetHINSTANCE(typeof(Win32HwndHost).Module);
        if (!_classRegistered)
        {
            var wndClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(NativeWndProc),
                hInstance = hInstance,
                lpszClassName = WndClassName
            };
            if (RegisterClassEx(ref wndClass) != 0) _classRegistered = true;
        }

        _hwnd = CreateWindowEx(
            0, WndClassName, "MpvHost",
            0x40000000 /* WS_CHILD */ | 0x40000000 /* WS_CLIPSIBLINGS */ | 0x10000000 /* WS_VISIBLE */,
            0, 0, 640, 360, hwndParent.Handle, IntPtr.Zero, hInstance, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
    }

    /// <summary>播放器渲染目标句柄。</summary>
    public IntPtr RenderHandle => _hwnd;

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 返回空白背景，避免闪烁
        if (msg == (int)WmEraseBackground)
        {
            handled = true;
            return new IntPtr(1);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static readonly WndProcDelegate NativeWndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
