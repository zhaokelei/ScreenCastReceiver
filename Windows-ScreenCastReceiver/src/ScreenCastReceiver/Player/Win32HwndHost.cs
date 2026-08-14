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
    private GCHandle _handle;

    /// <summary>在原生渲染目标上发生双击时触发（用于全屏切换）。</summary>
    public event Action? DoubleClicked;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // 单文件发布时 Marshal.GetHINSTANCE 会返回 -1，改用 GetModuleHandle(null) 获取进程模块句柄
        var hInstance = GetModuleHandle(null);
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

        // 把 host 实例挂到窗口 USERDATA，供 NativeWndProc 触发双击事件
        _handle = GCHandle.Alloc(this);
        SetWindowLong(_hwnd, GWL_USERDATA, GCHandle.ToIntPtr(_handle));

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_handle.IsAllocated)
            _handle.Free();
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

    private static readonly WndProcDelegate NativeWndProc = (h, m, w, l) =>
    {
        const int WM_LBUTTONDBLCLK = 0x0203;
        // 通过 GWL_USERDATA 取回 host 实例以触发双击事件
        if (m == WM_LBUTTONDBLCLK && GetWindowLong(h, GWL_USERDATA) is IntPtr ptr && ptr != IntPtr.Zero)
        {
            var host = GCHandle.FromIntPtr(ptr).Target as Win32HwndHost;
            host?.DoubleClicked?.Invoke();
        }
        return DefWindowProc(h, m, w, l);
    };

    private const int GWL_USERDATA = -21;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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
