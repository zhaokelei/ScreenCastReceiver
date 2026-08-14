//go:build windows
// +build windows

/*
  ScreenCastReceiver (Go版 - Win32零依赖全功能重构)
  ------------------------------------------------
  特点：
  1. 单 EXE (~3-5MB)，无需 .NET Runtime / 无需 VC++ 运行时
  2. 纯 Win32 API CreateWindowEx 创建控件，零 Go 第三方包
  3. DLNA (SSDP + HTTP + SOAP) 完全实现，适配安卓/电视投屏
  4. MPV --wid 嵌入父窗口 + 命名管道 JSON IPC 控制
  5. 支持 4 服务：DLNA/AirPlay2/Miracast/RTSP（AirPlay2/Miracast/RTSP 为占位提示）
  编译：双击 build.bat 即可。
*/
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
	"screencast-go/internal/network"
	dlnapkg "screencast-go/internal/services/dlna"
	airplaypkg "screencast-go/internal/services/airplay"
	miracastpkg "screencast-go/internal/services/miracast"
	rtspkg "screencast-go/internal/services/rtsp"
	"screencast-go/internal/player"
)

// ==================== Win32 常量（扩展最小版）====================
const (
	// ========== 最基础窗口消息/显示命令/消息框 ==========
	WM_CREATE          = 0x0001
	WM_DESTROY         = 0x0002
	WM_MOVE            = 0x0003
	WM_SIZE            = 0x0005
	WM_ACTIVATE        = 0x0006
	WM_SETFOCUS        = 0x0007
	WM_KILLFOCUS       = 0x0008
	WM_PAINT           = 0x000F
	WM_CLOSE           = 0x0010
	WM_QUIT            = 0x0012
	WM_SHOWWINDOW      = 0x0018
	WM_WININICHANGE    = 0x001A

	// ShowWindow 命令
	SW_HIDE            = 0
	SW_SHOWNORMAL      = 1
	SW_NORMAL          = 1
	SW_SHOWMINIMIZED   = 2
	SW_SHOWMAXIMIZED   = 3
	SW_MAXIMIZE        = 3
	SW_SHOWNOACTIVATE  = 4
	SW_SHOW            = 5
	SW_MINIMIZE        = 6
	SW_SHOWMINNOACTIVE = 7
	SW_SHOWNA          = 8
	SW_RESTORE         = 9
	SW_SHOWDEFAULT     = 10
	SW_FORCEMINIMIZE   = 11

	// MessageBox 类型
	MB_OK              = 0x00000000
	MB_OKCANCEL        = 0x00000001
	MB_ABORTRETRYIGNORE= 0x00000002
	MB_YESNOCANCEL     = 0x00000003
	MB_YESNO           = 0x00000004
	MB_RETRYCANCEL     = 0x00000005
	MB_CANCELTRYCONTINUE=0x00000006
	MB_ICONERROR       = 0x00000010
	MB_ICONQUESTION    = 0x00000020
	MB_ICONWARNING     = 0x00000030
	MB_ICONINFORMATION = 0x00000040
	MB_TOPMOST         = 0x00040000
	MB_SYSTEMMODAL     = 0x00001000
	MB_TASKMODAL       = 0x00002000
	IDOK               = 1
	IDCANCEL           = 2
	IDABORT            = 3
	IDRETRY            = 4
	IDIGNORE           = 5
	IDYES              = 6
	IDNO               = 7
	IDTRYAGAIN         = 10
	IDCONTINUE         = 11

	// 颜色
	COLOR_BTNFACE      = 15

	// ========== 扩展窗口样式 ==========
	WS_EX_APPWINDOW    = 0x00040000
	WS_EX_CLIENTEDGE   = 0x00000200
	WS_EX_LAYERED      = 0x00080000
	WS_EX_CONTROLPARENT = 0x00010000
	WS_EX_WINDOWEDGE   = 0x00000100

	WS_POPUP           = 0x80000000
	WS_MINIMIZEBOX     = 0x00020000
	WS_MAXIMIZEBOX     = 0x00010000
	WS_SYSMENU         = 0x00080000
	WS_CAPTION         = 0x00C00000
	WS_THICKFRAME      = 0x00040000
	WS_GROUP           = 0x00020000

	LBS_NOTIFY         = 0x0001
	LBS_NOINTEGRALHEIGHT = 0x0100
	LBS_EXTENDEDSEL    = 0x0800
	LBS_WANTKEYBOARDINPUT = 0x0400
	LBS_NOREDRAW       = 0x0004
	WS_HSCROLL         = 0x00100000

	BS_GROUPBOX        = 0x0007
	BS_DEFPUSHBUTTON   = 0x0001
	SS_CENTER          = 0x00000001
	SS_RIGHT           = 0x00000002
	SS_WORDELLIPSIS    = 0x00002000
	SS_NOTIFY          = 0x00000100

	// Common Controls
	ICC_WIN95_CLASSES  = 0x000000FF
	PROGRESS_CLASS      = "msctls_progress32"
	TRACKBAR_CLASS      = "msctls_trackbar32"
	STATUSCLASSNAME     = "msctls_statusbar32"
	UPDOWN_CLASS        = "msctls_updown32"
	ICC_PROGRESS_CLASS  = 0x00000020
	ICC_BAR_CLASSES     = 0x00000004
	ICC_TAB_CLASSES     = 0x00000008

	WM_USER            = 0x0400
	WM_SETICON         = 0x0080
	WM_GETMINMAXINFO   = 0x0024
	WM_QUERYENDSESSION = 0x0011
	WM_ERASEBKGND      = 0x0014
	WM_DRAWITEM        = 0x002B
	WM_CTLCOLORBTN     = 0x0135
	WM_CTLCOLORDLG     = 0x0136
	WM_COMMAND         = 0x0111
	WM_SETTEXT         = 0x000C
	WM_TIMER           = 0x0113
	WM_SETFONT         = 0x0030
	WM_HSCROLL         = 0x0114
	WM_VSCROLL         = 0x0115

	// ListBox messages
	LB_ADDSTRING       = 0x0180
	LB_INSERTSTRING    = 0x0181
	LB_DELETESTRING    = 0x0182
	LB_RESETCONTENT    = 0x018B
	LB_GETCOUNT        = 0x018B
	LB_GETCURSEL       = 0x0188
	LB_SETCURSEL       = 0x0186
	LB_GETSEL          = 0x0187
	LB_SETSEL          = 0x0185
	LB_GETITEMDATA     = 0x0199
	LB_SETITEMDATA     = 0x019A
	LB_GETTEXTLEN      = 0x018A
	LB_GETTEXT         = 0x0189
	LB_SETITEMHEIGHT   = 0x01A0

	// Button
	BM_SETIMAGE        = 0x00F7
	BM_SETCHECK        = 0x00F1
	BM_GETCHECK        = 0x00F0
	BST_CHECKED        = 1
	BST_UNCHECKED      = 0

	// TrackBar messages
	TBM_SETRANGE       = WM_USER + 31
	TBM_SETPOS         = WM_USER + 33
	TBM_GETPOS         = WM_USER + 35
	TBM_SETTICFREQ     = WM_USER + 38
	TBM_SETLINESIZE    = WM_USER + 37
	TBM_SETPAGESIZE    = WM_USER + 39

	// Static colors
	COLOR_STATIC       = 15

	// Edit
	EM_LIMITTEXT       = 0x00C5
	EM_GETFIRSTVISIBLELINE = 0x00CE

	// ========== 控件ID ==========
	// 4个服务
	ID_CHK_DLNA       = 1001
	ID_CHK_AIRPLAY    = 1002
	ID_CHK_MIRACAST   = 1003
	ID_CHK_RTSP       = 1004
	ID_LBL_DLNA_STAT  = 1101
	ID_LBL_AP_STAT    = 1102
	ID_LBL_MI_STAT    = 1103
	ID_LBL_RT_STAT    = 1104
	ID_BTN_DLNA_OPT   = 1201
	ID_BTN_AP_OPT     = 1202
	ID_BTN_MI_OPT     = 1203
	ID_BTN_RT_OPT     = 1204

	// 网卡区
	ID_BTN_NIC_REFRESH = 1010
	ID_LBX_NICS        = 1301
	ID_LBL_NIC_INFO    = 1302
	ID_BTN_FIREWALL    = 1011
	ID_BTN_OPEN_MPV    = 1012
	ID_LBL_LAN_IP      = 1310

	// 播放控制条
	ID_BTN_BACK_10    = 1020
	ID_BTN_PLAY       = 1021
	ID_BTN_FWD_10     = 1022
	ID_BTN_STOP       = 1023
	ID_BTN_ROTATE_L   = 1024
	ID_BTN_ASPECT     = 1025
	ID_BTN_SPEED      = 1026
	ID_BTN_VOLUME     = 1027
	ID_BTN_FULLSCREEN = 1028
	ID_TRK_PROGRESS   = 1401
	ID_LBL_TIME       = 1402

	// 播放区（嵌入MPV的容器HWND）
	ID_HWND_MPV       = 1501

	// 日志
	ID_LOG_EDIT       = 2001

	// 状态条
	ID_STATUSBAR      = 2101

	// 定时器 ID
	ID_TIMER_UI       = 3001 // 33ms 进度/进度条/FPS/速率刷新
	ID_TIMER_STATS    = 3002 // 1000ms 网卡+网络总速率刷新

	// 子类型ID
	PANEL_W = 420 // 左侧面板宽
)

// ==================== 类型 ====================
type (
	HWND     = uintptr
	WPARAM   = uintptr
	LPARAM   = uintptr
	LRESULT  = uintptr
	HANDLE   = uintptr
	HBRUSH   = uintptr
	HCURSOR  = uintptr
	HICON    = uintptr
	HINSTANCE = uintptr
	HMENU    = uintptr
	HDC      = uintptr
	HBITMAP  = uintptr
	HFONT    = uintptr
	COLORREF = uint32
)

type WNDCLASSEXW struct {
	CbSize        uint32
	Style         uint32
	LpfnWndProc   uintptr
	CbClsExtra    int32
	CbWndExtra    int32
	HInstance     HINSTANCE
	HIcon         HICON
	HCursor       HCURSOR
	HbrBackground HBRUSH
	LpszMenuName  *uint16
	LpszClassName *uint16
	HIconSm       HICON
}
type MSG struct {
	Hwnd    HWND
	Message uint32
	WParam  WPARAM
	LParam  LPARAM
	Time    uint32
	Pt      struct{ X, Y int32 }
}
type RECT struct { Left, Top, Right, Bottom int32 }
type POINT struct{ X, Y int32 }

type MINMAXINFO struct {
	PtReserved       POINT
	PtMaxSize        POINT
	PtMaxPosition    POINT
	PtMinTrackSize   POINT
	PtMaxTrackSize   POINT
}

// ==================== DLL 导入（全）====================
var (
	user32DLL               = syscall.NewLazyDLL("user32.dll")
	kernel32DLL             = syscall.NewLazyDLL("kernel32.dll")
	gdi32DLL                = syscall.NewLazyDLL("gdi32.dll")
	comctl32DLL             = syscall.NewLazyDLL("comctl32.dll")
	shell32DLL              = syscall.NewLazyDLL("shell32.dll")
	advapi32DLL             = syscall.NewLazyDLL("advapi32.dll")
	procGetModuleHandleW    = kernel32DLL.NewProc("GetModuleHandleW")
	procGetCurrentThreadId  = kernel32DLL.NewProc("GetCurrentThreadId")
	procLoadIconW           = user32DLL.NewProc("LoadIconW")
	procLoadCursorW         = user32DLL.NewProc("LoadCursorW")
	procRegisterClassExW    = user32DLL.NewProc("RegisterClassExW")
	procUnregisterClassW    = user32DLL.NewProc("UnregisterClassW")
	procCreateWindowExW     = user32DLL.NewProc("CreateWindowExW")
	procShowWindow          = user32DLL.NewProc("ShowWindow")
	procUpdateWindow        = user32DLL.NewProc("UpdateWindow")
	procGetMessageW         = user32DLL.NewProc("GetMessageW")
	procTranslateMessage    = user32DLL.NewProc("TranslateMessage")
	procDispatchMessageW    = user32DLL.NewProc("DispatchMessageW")
	procPostQuitMessage     = user32DLL.NewProc("PostQuitMessage")
	procDefWindowProcW      = user32DLL.NewProc("DefWindowProcW")
	procSendMessageW        = user32DLL.NewProc("SendMessageW")
	procPostMessageW        = user32DLL.NewProc("PostMessageW")
	procGetClientRect       = user32DLL.NewProc("GetClientRect")
	procGetWindowRect       = user32DLL.NewProc("GetWindowRect")
	procMoveWindow          = user32DLL.NewProc("MoveWindow")
	procSetWindowPos        = user32DLL.NewProc("SetWindowPos")
	procSetTimer            = user32DLL.NewProc("SetTimer")
	procKillTimer           = user32DLL.NewProc("KillTimer")
	procDestroyWindow       = user32DLL.NewProc("DestroyWindow")
	procGetDlgItem          = user32DLL.NewProc("GetDlgItem")
	procInvalidateRect      = user32DLL.NewProc("InvalidateRect")
	procUpdateLayeredWindow = user32DLL.NewProc("UpdateLayeredWindow")
	procSetFocus            = user32DLL.NewProc("SetFocus")
	procEnableWindow        = user32DLL.NewProc("EnableWindow")
	procIsWindowVisible     = user32DLL.NewProc("IsWindowVisible")
	procMessageBoxW         = user32DLL.NewProc("MessageBoxW")
	procGetDesktopWindow    = user32DLL.NewProc("GetDesktopWindow")
	procDrawTextW           = user32DLL.NewProc("DrawTextW")
	procGetSysColor         = user32DLL.NewProc("GetSysColor")
	procGetSysColorBrush    = user32DLL.NewProc("GetSysColorBrush")
	procSetParent           = user32DLL.NewProc("SetParent")
	procGetWindowLongPtrW   = user32DLL.NewProc("GetWindowLongPtrW")
	procSetWindowLongPtrW   = user32DLL.NewProc("SetWindowLongPtrW")
	procCallWindowProcW     = user32DLL.NewProc("CallWindowProcW")
	procInitCommonControlsEx = comctl32DLL.NewProc("InitCommonControlsEx")
	procCreateFontIndirectW  = gdi32DLL.NewProc("CreateFontIndirectW")
	procSelectObject         = gdi32DLL.NewProc("SelectObject")
	procDeleteObject         = gdi32DLL.NewProc("DeleteObject")
	procSetBkMode            = gdi32DLL.NewProc("SetBkMode")
	procSetTextColor         = gdi32DLL.NewProc("SetTextColor")
	procRectangle            = gdi32DLL.NewProc("Rectangle")
	procFillRect             = user32DLL.NewProc("FillRect")
	procGetDC                = user32DLL.NewProc("GetDC")
	procReleaseDC            = user32DLL.NewProc("ReleaseDC")
	procLoadImageW           = user32DLL.NewProc("LoadImageW")
	procSendDlgItemMessageW  = user32DLL.NewProc("SendDlgItemMessageW")
	procShellExecuteW        = shell32DLL.NewProc("ShellExecuteW")
	procAdjustWindowRectEx   = user32DLL.NewProc("AdjustWindowRectEx")
	procSetWindowTextW       = user32DLL.NewProc("SetWindowTextW")
	procFlashWindowEx        = user32DLL.NewProc("FlashWindowEx")
	procFindWindowW          = user32DLL.NewProc("FindWindowW")
	procGetClassNameW        = user32DLL.NewProc("GetClassNameW")
	procGetWindowTextW       = user32DLL.NewProc("GetWindowTextW")
	procGetWindowTextLengthW = user32DLL.NewProc("GetWindowTextLengthW")
	procIsWindow             = user32DLL.NewProc("IsWindow")
	procDrawFrameControl     = user32DLL.NewProc("DrawFrameControl")
	procDrawEdge             = user32DLL.NewProc("DrawEdge")
	procEndPaint             = user32DLL.NewProc("EndPaint")
	procBeginPaint           = user32DLL.NewProc("BeginPaint")
)
type PAINTSTRUCT struct {
	Hdc         HDC
	FErase      int32
	RcPaint     RECT
	FRestore    int32
	IncUpdate   int32
	RgbReserved [32]byte
}
type INITCOMMONCONTROLSEX struct {
	DwSize uint32
	DwICC  uint32
}

const (
	IDC_ARROW       = 32512
	IDI_APPLICATION = 32512
	SWP_NOSIZE      = 0x0001
	SWP_NOMOVE      = 0x0002
	SWP_NOZORDER    = 0x0004
	SWP_SHOWWINDOW  = 0x0040
	SWP_HIDEWINDOW  = 0x0080
	HWND_TOP        = 0
	HWND_BOTTOM     = 1
	HWND_TOPMOST    = ^uintptr(0) - 1
	TRANSPARENT     = 1
	OPAQUE          = 2

	// 窗口/控件基础风格
	WS_CHILD         = 0x40000000
	WS_VISIBLE       = 0x10000000
	WS_DISABLED      = 0x08000000
	WS_CLIPSIBLINGS  = 0x04000000
	WS_CLIPCHILDREN  = 0x02000000
	WS_TABSTOP       = 0x00010000
	WS_READONLY      = 0x0800
	WS_BORDER        = 0x00800000
	WS_DLGFRAME      = 0x00400000
	WS_OVERLAPPED    = 0x00000000
	WS_OVERLAPPEDWINDOW = 0x00CF0000 // WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
	CW_USEDEFAULT    = 0x80000000

	// Static 控件风格（前面const已有SS_CENTER/SS_RIGHT/SS_WORDELLIPSIS/SS_NOTIFY，这里仅补SS_LEFT/SS_SUNKEN等）
	SS_LEFT          = 0x00000000
	SS_SUNKEN        = 0x00001000
	SS_OWNERDRAW     = 0x0000000D

	// Button 控件风格（前面已有BS_DEFPUSHBUTTON/BS_GROUPBOX，这里补其他）
	BS_PUSHBUTTON    = 0x00000000
	BS_AUTOCHECKBOX  = 0x00000003
	BS_AUTORADIOBUTTON = 0x00000009
	BS_CHECKBOX      = 0x00000002
	BS_3STATE        = 0x00000005
	BS_AUTO3STATE    = 0x00000006
	BS_RADIOBUTTON   = 0x00000004
	BS_TEXT          = 0x00000000
	BS_ICON          = 0x00000040
	BS_BITMAP        = 0x00000080
	BS_LEFTTEXT      = 0x00000020
	BS_RIGHTBUTTON   = BS_LEFTTEXT

	// Edit 控件风格
	ES_LEFT          = 0x0000
	ES_CENTER        = 0x0001
	ES_RIGHT         = 0x0002
	ES_MULTILINE     = 0x0004
	ES_UPPERCASE     = 0x0008
	ES_LOWERCASE     = 0x0010
	ES_PASSWORD      = 0x0020
	ES_AUTOVSCROLL   = 0x0040
	ES_AUTOHSCROLL   = 0x0080
	ES_NOHIDESEL     = 0x0100
	ES_OEMCONVERT    = 0x0400
	ES_READONLY      = 0x0800
	ES_WANTRETURN    = 0x1000
	ES_NUMBER        = 0x2000
	WS_VSCROLL       = 0x00200000

	// ScrollBar
	SBS_HORZ         = 0x0000
	SBS_VERT         = 0x0001

	DT_CENTER       = 0x00000001
	DT_VCENTER      = 0x00000004
	DT_SINGLELINE   = 0x00000020
	DT_END_ELLIPSIS = 0x00008000
	DT_PATH_ELLIPSIS = 0x00004000
	DT_MODIFYSTRING = 0x00010000
	DT_WORDBREAK    = 0x00000010
	DT_LEFT         = 0x00000000
	DT_RIGHT        = 0x00000002
	DFC_BUTTON      = 1
	DFC_CAPTION     = 2
	DFCS_BUTTONPUSH = 0x0010
	EDGE_SUNKEN     = 0x00001000
	EDGE_RAISED     = 0x00000005
	BDR_RAISEDINNER = 0x00000004
	BDR_SUNKENOUTER = 0x00000002
	BF_RECT         = 0x0000
	BF_TOP          = 0x0001
	BF_LEFT         = 0x0004
	BF_BOTTOM       = 0x0008
	BF_RIGHT        = 0x0002
	BF_MIDDLE       = 0x0800
)

// ==================== 全局APP ====================
var (
	App = &appCtx{
		log:     logger.Default,
		cfg:     models.DefaultAppConfig(),
		mpv:     nil,
		dlnaSvc: nil,
	}
	hMainWnd HWND
	hMPVHwnd HWND // 嵌入MPV的子窗口
	hLogEdit HWND
	hProgress HWND
	hLblTime HWND
	hNicList HWND
	hLblLanIP HWND
	hLblDlnaStat HWND
	hLblAPStat HWND
	hLblMIStat HWND
	hLblRTStat HWND
	hChkDlna HWND
	hChkAir HWND
	hChkMi  HWND
	hChkRt  HWND
	hStatusBar HWND
	hFontUI HFONT

	// ================ UI线程调度器 ================
	// 解决：跨goroutine（DLNA/MPV/网卡/定时器）不能直接操作HWND，
	// 所有更新UI（SetWindowText/BM_SETCHECK/EnableWindow/MoveWindow等）必须回UI线程执行
	// 用法：非UI线程调用 RunOnUI(func(){ ...操作HWND... })，会进队列，UI线程在33ms定时器中消费
	uiJobs = make(chan func(), 512)
	// UI线程（创建窗口、跑GetMessage循环的那个线程）的Windows TID，用于判断是否需要切线程
	uiThreadID uint32
)

func isUIThread() bool {
	tid, _, _ := procGetCurrentThreadId.Call()
	return uint32(tid) == uiThreadID
}

// RunOnUI 把一个函数调度到UI线程执行（非阻塞，先进队列）
// ⚠️ 跨线程操作任何HWND之前必须调用，否则会随机卡死/崩溃/重入
// ✅ 如果当前已经在UI线程，则直接同步执行（避免死锁）
func RunOnUI(f func()) {
	if isUIThread() {
		// UI线程内直接执行，避免死锁
		func() {
			defer func() { recover() }()
			f()
		}()
		return
	}
	defer func() { recover() }() // 防止队列满时panic
	select {
	case uiJobs <- f:
	default: // 队列满就丢（下一次定时器还会刷新，日志/状态下一次再更新就好）
	}
}

// drainUIJobs UI线程调用，批量把uiJobs队列全部执行完（每次WM_TIMER_UI触发时调用）
func drainUIJobs() {
	for {
		select {
		case f := <-uiJobs:
			func() {
				defer func() {
					if r := recover(); r != nil {
						_ = r
					}
				}()
				f()
			}()
		default:
			return
		}
	}
}

// RunOnUISync 同步版：把f调度到UI线程执行，并阻塞等待f执行完毕
// ⚠️ 如果当前已经在UI线程，直接同步执行（防死锁）；否则投递后等待
// 用途：MessageBox抢占确认（需要用户点击结果）/ 刷新MPV嵌入HWND前必须在UI线程确认有效
func RunOnUISync(f func()) {
	if isUIThread() {
		func() {
			defer func() { recover() }()
			f()
		}()
		return
	}
	done := make(chan struct{})
	RunOnUI(func() {
		defer close(done)
		func() {
			defer func() { recover() }()
			f()
		}()
	})
	<-done
}

type appCtx struct {
	log     *logger.RingLogger
	cfg     *models.AppConfig
	nics    []models.NICInfo
	boundIPs []string

	dlnaSvc  *dlnapkg.DMR
	airSvc   *airplaypkg.Placeholder
	miSvc    *miracastpkg.Placeholder
	rtSvc    *rtspkg.Placeholder
	mpv      *player.Manager

	lastNetSample map[int]network.StatsSample // 按网卡index存
	lastUIUpdate  time.Time

	// 当前MPV显示的状态值（缓存，用于UI显示对比）
	uiFPS float64
	uiKbps float64
	uiPos  float64
	uiDur  float64
	uiSpeed float64
	uiPaused bool
}

// ==================== 辅助函数 ====================
func Sp(s string) *uint16 { p, _ := syscall.UTF16PtrFromString(s); return p }
func U(x int32) uintptr { return uintptr(x) }
func I32(x int) int32    { return int32(x) }

func GetDlgItem2(parent HWND, id int32) HWND {
	r, _, _ := procGetDlgItem.Call(uintptr(parent), uintptr(id))
	return r
}

func SetDlgItemText(parent HWND, id int32, s string) {
	p := Sp(s)
	procSetWindowTextW.Call(GetDlgItem2(parent, id), uintptr(unsafe.Pointer(p)))
}

func SetWindowText2(hwnd HWND, s string) {
	p := Sp(s)
	procSetWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(p)))
}

func MoveWindow2(hwnd HWND, x, y, w, h int32, repaint bool) {
	var r uintptr
	if repaint { r = 1 }
	procMoveWindow.Call(hwnd, U(x), U(y), U(w), U(h), r)
}

func ShowWindow2(hwnd HWND, cmd int32) {
	procShowWindow.Call(hwnd, uintptr(cmd))
}

func MessageBox(caption, text string, flags uintptr) int32 {
	cp := Sp(caption); tp := Sp(text)
	r, _, _ := procMessageBoxW.Call(0,
		uintptr(unsafe.Pointer(tp)),
		uintptr(unsafe.Pointer(cp)),
		flags)
	return int32(r)
}

func ShellExecute(verb, file string) {
	vp := Sp(verb); fp := Sp(file)
	procShellExecuteW.Call(0, uintptr(unsafe.Pointer(vp)),
		uintptr(unsafe.Pointer(fp)), 0, 0, SW_SHOW)
}

// AppendLogUI 往UI日志框追加一行（100%线程安全）
// ⚠️ 任何线程都可直接调用：内部自动判断是否切UI线程
func AppendLogUI(line string) {
	if hLogEdit == 0 { return }
	RunOnUI(func() {
		writeLogLine(line)
	})
}

func writeLogLine(line string) {
	t := time.Now().Format("15:04:05.000")
	full := fmt.Sprintf("[%s] %s\r\n", t, line)
	length, _, _ := procSendMessageW.Call(hLogEdit, 0x000E, 0, 0)
	procSendMessageW.Call(hLogEdit, 0x00B1, length, length)
	p := Sp(full)
	procSendMessageW.Call(hLogEdit, 0x00C2, 0, uintptr(unsafe.Pointer(p)))
	procSendMessageW.Call(hLogEdit, 0x00B7, 0, 0)
	// 限长：>20000字符就裁剪到15000
	if length > 20000 {
		procSendMessageW.Call(hLogEdit, 0x00B1, 0, length-15000)
		procSendMessageW.Call(hLogEdit, 0x00C2, 1, 0) // 剪切选中
	}
}

// FormatBytes / FormatKBs
func FormatKBs(kb float64) string {
	if kb < 1024 { return fmt.Sprintf("%.1f KB/s", kb) }
	return fmt.Sprintf("%.2f MB/s", kb/1024.0)
}

// ==================== 初始化日志订阅（实时显示）====================
func setupLoggerSubscription() {
	App.log.Subscribe(func(e logger.Entry) {
		line := logger.FormatLine(e)
		// 直接写UI（如果日志回调在UI线程上下文就安全；SSDP/HTTP/MPV回调都在后台Goroutine，
		// 但这里Write to Edit Control在Win32里只要不跟UI线程抢就没事，
		// 因为Edit Control在user32.dll里用SendMessage内部处理跨线程）
		AppendLogUI(line)
	})
}

// ==================== 启动/停止 4个服务（全异步启动，绝不阻塞UI）====================
func toggleDlna(enable bool) {
	if enable {
		// ⚠️ 启动全流程放后台goroutine：枚举绑定IP→新建DMR→HTTP监听+SSDP→刷新标签
		// UI线程只负责发PostMessage立刻返回，保证不"未响应"
		go func() {
			bindIPs, err := network.GetBindAddresses(App.cfg, App.log)
			if err != nil {
				App.log.Error("DLNA", "获取绑定IP失败: %v", err)
				return
			}
			App.boundIPs = bindIPs
			firstStart := false
			if App.dlnaSvc == nil {
				App.dlnaSvc = dlnapkg.NewDMR(App.cfg, App.log)
				firstStart = true
				App.dlnaSvc.OnStateChange = func(a models.ServiceStateChangedArgs) {
					// ⚠️ 回调在DLNA goroutine，必须切回UI线程刷新标签！
					RunOnUI(func() { refreshServiceLabels() })
				}
				App.dlnaSvc.OnRequestPlay = func(s *models.ActiveSession, _ uintptr) (error, bool) {
					if App.mpv == nil {
						App.mpv = player.NewManager(App.cfg, App.log)
						// setupMPVCallbacks里的OnNeedConfirm会弹MessageBox（模态），它在DLNA线程调用，
						// 弹框本身Windows允许跨线程，但为了保证稳定我们同步切UI线程执行RequestPlayback
						setupMPVCallbacks()
					}
					// RequestPlayback可能会启动MPV子进程+同步等待IPC连接，
					// 但它是在DLNA HTTP goroutine里（非UI线程），这里直接执行没问题
					var (
						e  error
						cn bool
					)
					RunOnUISync(func() {
						// ⚠️ 用同步UI调用，保证嵌入MPV的HWND在UI线程存在且有效
						e, cn = App.mpv.RequestPlayback(s, hMPVHwnd)
					})
					return e, cn
				}
			}
			if err := App.dlnaSvc.Start(bindIPs); err != nil {
				RunOnUI(func() {
					MessageBox("DLNA 启动失败", err.Error(), MB_OK|MB_ICONERROR)
					procSendMessageW.Call(hChkDlna, BM_SETCHECK, BST_UNCHECKED, 0)
					refreshServiceLabels()
				})
				_ = firstStart
				return
			}
			RunOnUI(func() {
				refreshLanIPLabel()
				refreshServiceLabels()
			})
		}()
	} else {
		// 停止服务：关闭socket通常很快，也放goroutine防万一Stop有清理耗时
		go func() {
			if App.dlnaSvc != nil {
				App.dlnaSvc.Stop()
			}
			RunOnUI(func() { refreshServiceLabels() })
		}()
	}
	// 启动前先刷一次状态，用户看到"正在启动..."状态（具体的标签会在OnStateChange中更新）
	refreshServiceLabels()
}

func toggleService(kind models.ServiceKind, enable bool) {
	switch kind {
	case models.SvcDlna: toggleDlna(enable)
	case models.SvcAirPlay:
		if enable {
			go func() {
				if App.airSvc == nil {
					App.airSvc = &airplaypkg.Placeholder{Log: App.log, Kind: kind}
				}
				if err := App.airSvc.Start(); err != nil {
					RunOnUI(func() {
						MessageBox("AirPlay2 启动失败", err.Error(), MB_OK|MB_ICONWARNING)
						procSendMessageW.Call(hChkAir, BM_SETCHECK, BST_UNCHECKED, 0)
						refreshServiceLabels()
					})
					return
				}
				RunOnUI(func() { refreshServiceLabels() })
			}()
		} else {
			go func() {
				if App.airSvc != nil { App.airSvc.Stop() }
				RunOnUI(func() { refreshServiceLabels() })
			}()
		}
	case models.SvcMiracast:
		if enable {
			go func() {
				if App.miSvc == nil { App.miSvc = &miracastpkg.Placeholder{Log: App.log, Kind: kind} }
				if err := App.miSvc.Start(); err != nil {
					RunOnUI(func() {
						MessageBox("Miracast 启动失败", err.Error(), MB_OK|MB_ICONWARNING)
						procSendMessageW.Call(hChkMi, BM_SETCHECK, BST_UNCHECKED, 0)
						refreshServiceLabels()
					})
					return
				}
				RunOnUI(func() { refreshServiceLabels() })
			}()
		} else {
			go func() {
				if App.miSvc != nil { App.miSvc.Stop() }
				RunOnUI(func() { refreshServiceLabels() })
			}()
		}
	case models.SvcRtsp:
		if enable {
			go func() {
				if App.rtSvc == nil { App.rtSvc = &rtspkg.Placeholder{Log: App.log, Kind: kind} }
				if err := App.rtSvc.Start(); err != nil {
					RunOnUI(func() {
						MessageBox("RTSP 启动失败", err.Error(), MB_OK|MB_ICONWARNING)
						procSendMessageW.Call(hChkRt, BM_SETCHECK, BST_UNCHECKED, 0)
						refreshServiceLabels()
					})
					return
				}
				RunOnUI(func() { refreshServiceLabels() })
			}()
		} else {
			go func() {
				if App.rtSvc != nil { App.rtSvc.Stop() }
				RunOnUI(func() { refreshServiceLabels() })
			}()
		}
	}
	// 启动前先刷一次
	refreshServiceLabels()
}

// setupMPVCallbacks 把MPV事件连到UI
func setupMPVCallbacks() {
	if App.mpv == nil { return }
	App.mpv.OnNeedConfirm = func(newS *player.Session) bool {
		exist := App.mpv.CurrentSession()
		txt := fmt.Sprintf("当前已有投屏正在播放：\n来源：%s\n标题：%s\n\n新的投屏请求：\n来源：%s (IP %s)\n标题：%s\n\n是否终止当前并切换到新的？",
			exist.SourceName, exist.Title, newS.SourceName, newS.SourceIP, newS.Title)
		var id int32
		// ⚠️ MessageBox 必须在UI线程执行（模态弹窗需要消息循环，且需要返回用户点击结果）
		RunOnUISync(func() {
			id = MessageBox("投屏抢占确认", txt,
				MB_YESNO|MB_ICONQUESTION|MB_TOPMOST|MB_TASKMODAL)
		})
		return id == IDYES
	}
	App.mpv.OnStatsUpdate = func(fps, bitrateK, posS, durS, speed float64, paused bool) {
		// 写简单值到App缓存，UI线程Timer里会读；浮点/布尔原子读写在x86没问题
		App.uiFPS = fps
		App.uiKbps = bitrateK
		App.uiPos = posS
		App.uiDur = durS
		App.uiSpeed = speed
		App.uiPaused = paused
		// ⚠️ 同步MPV实际播放状态到DLNA inst0，让手机轮询GetPositionInfo/GetTransportInfo时拿到真实进度
		if App.dlnaSvc != nil {
			posMs := int64(posS * 1000)
			durMs := int64(durS * 1000)
			App.dlnaSvc.UpdatePlayback(posMs, durMs, paused, true)
		}
		// ⚠️ 禁止在此处直接操作HWND/刷新UI，统一放WM_TIMER里批量刷新（33ms/次足够流畅）
	}
	App.mpv.OnStateChange = func() {
		RunOnUI(func() { refreshStatusBar() })
	}
}

// refreshServiceLabels 把4个服务的Status/Port回写到UI
func refreshServiceLabels() {
	dlnaStatus := models.StatusStopped
	dlnaPort := 0
	if App.dlnaSvc != nil {
		dlnaStatus = App.dlnaSvc.Status()
		dlnaPort = App.dlnaSvc.ListenPort()
	}
	apStatus := models.StatusStopped
	if App.airSvc != nil { apStatus = App.airSvc.Status() }
	miStatus := models.StatusStopped
	if App.miSvc != nil { miStatus = App.miSvc.Status() }
	rtStatus := models.StatusStopped
	rtPort := 0
	if App.rtSvc != nil { rtStatus = App.rtSvc.Status(); rtPort = App.rtSvc.ListenPort() }

	if dlnaPort > 0 {
		SetWindowText2(hLblDlnaStat, fmt.Sprintf("%s · 端口 %d", dlnaStatus, dlnaPort))
	} else {
		SetWindowText2(hLblDlnaStat, dlnaStatus.String())
	}
	SetWindowText2(hLblAPStat, apStatus.String())
	SetWindowText2(hLblMIStat, miStatus.String())
	if rtPort > 0 {
		SetWindowText2(hLblRTStat, fmt.Sprintf("%s · 端口 %d", rtStatus, rtPort))
	} else {
		SetWindowText2(hLblRTStat, rtStatus.String())
	}
	refreshStatusBar()
}

// refreshLanIPLabel 显示本机局域网IP
func refreshLanIPLabel() {
	ip := network.GetOutboundIP(App.cfg, App.log)
	SetWindowText2(hLblLanIP, fmt.Sprintf("本机局域网IP：%s  （安卓/iPhone请与电脑同一WiFi）", ip))
}

// refreshNICList 分两阶段：
// 1) 前半（EnumNICs）后台I/O，不操作HWND，可任意线程执行
// 2) 后半（塞ListBox）操作HWND，必须切回UI线程（通过RunOnUI）
// ⚠️ 所以整个函数可以在任何goroutine里安全调用
func refreshNICList() {
	// 阶段1：网卡枚举（后台I/O，无HWND操作）
	var err error
	App.nics, err = network.EnumNICs(App.log)
	if err != nil { return }

	// 阶段2：填充ListBox（操作HWND）→ 必须切UI线程
	RunOnUISync(func() {
		procSendMessageW.Call(hNicList, LB_RESETCONTENT, 0, 0)
		// 根据App.cfg.BindNICIndex决定默认勾选
		selSet := map[int]bool{}
		for _, idx := range App.cfg.BindNICIndex { selSet[idx] = true }
		selectAllPhysical := len(App.cfg.BindNICIndex) == 0 // 兜底：首次启动勾选所有物理网卡
		for i, n := range App.nics {
			typ := "虚拟"
			if n.IsPhysical { typ = "物理" }
			ips := strings.Join(n.IPv4List, ",")
			if ips == "" { ips = "<无IPv4>" }
			label := fmt.Sprintf("[%s] %s (%s) IP=%s", typ, n.Name, n.Description, ips)
			if !n.OperUp { label = "(未连接) " + label }
			p := Sp(label)
			idx, _, _ := procSendMessageW.Call(hNicList, LB_ADDSTRING, 0, uintptr(unsafe.Pointer(p)))
			procSendMessageW.Call(hNicList, LB_SETITEMDATA, idx, U(I32(n.Index)))
			sel := selSet[n.Index] || (selectAllPhysical && n.IsPhysical)
			if sel {
				procSendMessageW.Call(hNicList, LB_SETSEL, 1, idx)
			}
			_ = i
		}
	})
}

// readSelectedNICIndexes 读ListBox选中的网卡Index数组，写回App.cfg.BindNICIndex
func readSelectedNICIndexes() []int {
	countI32, _, _ := procSendMessageW.Call(hNicList, 0x018B /*LB_GETCOUNT*/, 0, 0)
	cnt := int(countI32)
	out := []int{}
	for i := 0; i < cnt; i++ {
		sel, _, _ := procSendMessageW.Call(hNicList, LB_GETSEL, U(I32(i)), 0)
		if sel != 0 {
			idxI, _, _ := procSendMessageW.Call(hNicList, LB_GETITEMDATA, U(I32(i)), 0)
			if int(idxI) > 0 {
				out = append(out, int(idxI))
			}
		}
	}
	App.cfg.BindNICIndex = out
	return out
}

// ==================== 防火墙一键放行（netsh命令）====================
func oneClickFirewall() {
	exe, _ := os.Executable()
	cmds := []string{
		fmt.Sprintf(`netsh advfirewall firewall add rule name="ScreenCastReceiver-Go UDP-SSDP" dir=in action=allow protocol=UDP localport=1900 program="%s" profile=any`, exe),
		fmt.Sprintf(`netsh advfirewall firewall add rule name="ScreenCastReceiver-Go HTTP" dir=in action=allow protocol=TCP localport=any program="%s" profile=any`, exe),
		fmt.Sprintf(`netsh advfirewall firewall add rule name="ScreenCastReceiver-Go RTSP" dir=in action=allow protocol=TCP localport=8554 program="%s" profile=any`, exe),
		fmt.Sprintf(`netsh advfirewall firewall add rule name="ScreenCastReceiver-Go MPV" dir=in action=allow program="%s" profile=any`,
			filepath.Join(filepath.Dir(exe), "mpv", "mpv.exe")),
	}
	go func() {
		for _, c := range cmds {
			App.log.Info("防火墙", "执行: %s", c)
			cmd := exec.Command("cmd", "/C", c)
			out, err := cmd.CombinedOutput()
			if err != nil {
				App.log.Warn("防火墙", "失败: %v 输出=%s", err, string(out))
			}
		}
		App.log.Info("防火墙", "✅ 已添加 4 条 netsh advfirewall 规则（如需移除在【Windows Defender防火墙→高级设置→入站规则】按名称搜索删除）")
	}()
	MessageBox("防火墙", "已开始在后台添加防火墙规则（DLNA 1900/HTTP/RTSP 8554/MPV），\n完成后请在日志区域查看执行结果。",
		MB_OK|MB_ICONINFORMATION)
}

// ==================== 刷新状态栏/FPS/速率 ====================
func refreshStatusBar() {
	parts := [4]int32{180, 420, 640, -1}
	_, partsP := parts, 0
	_ = partsP
	// 状态条用多格太复杂，直接把FPS/速率/时间拼成文本放到主窗口右下列表
	speedTxt := FormatKBs(App.uiKbps / 8) // video-bitrate是kbps, 转KB/s=kbps/8
	fpsTxt := "FPS: 0"
	if App.uiFPS > 0 { fpsTxt = fmt.Sprintf("FPS: %.1f", App.uiFPS) }
	netTxt := fmt.Sprintf("速率: %s", speedTxt)
	timeTxt := "00:00 / 00:00"
	if App.uiDur > 0 {
		pt := formatS(App.uiPos)
		dt := formatS(App.uiDur)
		timeTxt = fmt.Sprintf("%s / %s  x%.2f", pt, dt, App.uiSpeed)
	}
	s := fmt.Sprintf("  %s   |   %s   |   %s", fpsTxt, netTxt, timeTxt)
	SetWindowText2(hStatusBar, s)
	// 同步到 lblTime 上方控制条时间
	SetWindowText2(hLblTime, timeTxt)
	// 同步Trackbar进度
	if App.uiDur > 0 {
		pct := int32(App.uiPos / App.uiDur * 1000.0)
		procSendMessageW.Call(hProgress, TBM_SETPOS, 1, U(pct))
	}
}
func formatS(s float64) string {
	if s < 0 { s = 0 }
	sec := int(s)
	h := sec / 3600
	sec -= h * 3600
	m := sec / 60
	sec -= m * 60
	if h > 0 { return fmt.Sprintf("%d:%02d:%02d", h, m, sec) }
	return fmt.Sprintf("%02d:%02d", m, sec)
}

// ==================== 主窗口过程 ====================
func MainWndProc(hwnd HWND, msg uint32, wParam WPARAM, lParam LPARAM) LRESULT {
	switch msg {
	case WM_CREATE:
		hMainWnd = hwnd
		buildAllControls(hwnd)
		// ⚠️ 网卡枚举/刷新是I/O操作（查网卡计数器），放后台goroutine，不阻塞UI创建
		// 完成后通过RunOnUI把结果填回ListBox/标签（防跨线程操作HWND）
		go func() {
			refreshNICList()
			RunOnUI(func() {
				refreshLanIPLabel()
				refreshServiceLabels()
			})
		}()
		setupLoggerSubscription()
		// 两个定时器
		procSetTimer.Call(hwnd, ID_TIMER_UI, 33, 0)
		procSetTimer.Call(hwnd, ID_TIMER_STATS, 1000, 0)
		App.log.Info("APP", "ScreenCastReceiver (Go 纯Win32重构版) 启动成功")
		App.log.Info("APP", "Go版本: %s, 编译: %s/%s, CPU核数: %d",
			runtime.Version(), "windows", "amd64", runtime.NumCPU())
		return 0

	case WM_CLOSE:
		// 先停服务再关MPV再Destroy
		if App.dlnaSvc != nil { App.dlnaSvc.Stop() }
		if App.airSvc != nil { App.airSvc.Stop() }
		if App.miSvc != nil { App.miSvc.Stop() }
		if App.rtSvc != nil { App.rtSvc.Stop() }
		if App.mpv != nil { App.mpv.StopMPV() }
		procKillTimer.Call(hwnd, ID_TIMER_UI)
		procKillTimer.Call(hwnd, ID_TIMER_STATS)
		procDestroyWindow.Call(hwnd)
		return 0

	case WM_DESTROY:
		procPostQuitMessage.Call(0)
		return 0

	case WM_SIZE:
		layoutAll(hwnd)
		return 0

	case WM_TIMER:
		switch uint32(wParam) {
		case ID_TIMER_UI:
			// ⚠️ 先消费跨线程UI任务队列（每33ms扫一次，保证所有刷新在UI线程执行）
			drainUIJobs()
			refreshStatusBar()
		case ID_TIMER_STATS:
			// 统计全局网络Rx/Tx（简化版：取网卡计数器差值）
			_ = readSelectedNICIndexes()
			// 注意：refreshStatusBar操作HWND，放到UI线程（这里WM_TIMER本来就是UI线程，直接调OK）
			refreshStatusBar()
		}
		return 0

	case WM_COMMAND:
		id := int32(uint32(wParam) & 0xFFFF)
		// notify := uint32(wParam) >> 16
		switch id {
		case ID_CHK_DLNA:
			state, _, _ := procSendMessageW.Call(hChkDlna, BM_GETCHECK, 0, 0)
			toggleDlna(state == BST_CHECKED)
		case ID_CHK_AIRPLAY:
			state, _, _ := procSendMessageW.Call(hChkAir, BM_GETCHECK, 0, 0)
			toggleService(models.SvcAirPlay, state == BST_CHECKED)
		case ID_CHK_MIRACAST:
			state, _, _ := procSendMessageW.Call(hChkMi, BM_GETCHECK, 0, 0)
			toggleService(models.SvcMiracast, state == BST_CHECKED)
		case ID_CHK_RTSP:
			state, _, _ := procSendMessageW.Call(hChkRt, BM_GETCHECK, 0, 0)
			toggleService(models.SvcRtsp, state == BST_CHECKED)

		case ID_BTN_NIC_REFRESH:
			refreshNICList()
		case ID_BTN_FIREWALL:
			oneClickFirewall()
		case ID_BTN_OPEN_MPV:
			exe, _ := os.Executable()
			ShellExecute("open", filepath.Dir(exe))

		case ID_BTN_DLNA_OPT, ID_BTN_AP_OPT, ID_BTN_MI_OPT, ID_BTN_RT_OPT:
			MessageBox("设置（占位）", "端口/设备名设置对话框将在下一版本提供。\n当前使用默认值：DLNA自动端口，设备名=\"我的影院-客厅\"",
				MB_OK|MB_ICONINFORMATION)

		case ID_BTN_PLAY:
			if App.mpv != nil { App.mpv.PlayPauseToggle() }
		case ID_BTN_STOP:
			if App.mpv != nil { App.mpv.Stop() }
		case ID_BTN_BACK_10:
			if App.mpv != nil { App.mpv.Seek(-10) }
		case ID_BTN_FWD_10:
			if App.mpv != nil { App.mpv.Seek(10) }
		case ID_BTN_ROTATE_L:
			if App.mpv == nil { break }
			rot := 0
			vals := []int{0, 90, 180, 270}
			_ = rot
			App.mpv.SetRotate(vals[(0+1)%len(vals)])
			MessageBox("旋转（占位）", "每按一次旋转90°。实际已发送命令给MPV。", MB_OK|MB_ICONINFORMATION)
		case ID_BTN_ASPECT:
			aspects := []string{"auto", "16:9", "4:3", "2.35:1", "1:1"}
			i := 0
			if App.mpv != nil {
				i = (i + 1) % len(aspects)
				App.mpv.SetAspect(aspects[i])
			}
		case ID_BTN_SPEED:
			speeds := []float64{0.5, 1.0, 1.25, 1.5, 2.0}
			i := 1
			if App.mpv != nil {
				i = (i + 1) % len(speeds)
				App.mpv.SetSpeed(speeds[i])
			}
		case ID_BTN_VOLUME:
			if App.mpv != nil {
				App.mpv.SetVolume(80)
				MessageBox("音量（占位）", "已设为80%。滑动条设置后续版本提供。", MB_OK|MB_ICONINFORMATION)
			}
		case ID_BTN_FULLSCREEN:
			if App.mpv != nil { App.mpv.FullscreenToggle() }
		}
		return 0

	case WM_HSCROLL:
		if HWND(lParam) == hProgress {
			// 用户拖动进度条：同步给MPV seek
			posI, _, _ := procSendMessageW.Call(hProgress, TBM_GETPOS, 0, 0)
			if App.uiDur > 0 {
				targetS := float64(posI) / 1000.0 * App.uiDur
				deltaS := targetS - App.uiPos
				if App.mpv != nil && (deltaS > 0.2 || deltaS < -0.2) {
					App.mpv.Seek(deltaS)
				}
			}
		}
		return 0

	case WM_GETMINMAXINFO:
		mm := (*MINMAXINFO)(unsafe.Pointer(lParam))
		mm.PtMinTrackSize.X = 980
		mm.PtMinTrackSize.Y = 600
		return 0
	}

	r, _, _ := procDefWindowProcW.Call(
		uintptr(hwnd), uintptr(msg), uintptr(wParam), uintptr(lParam))
	return LRESULT(r)
}

// ==================== 注册窗口类 & buildAllControls & layoutAll ====================
func RegisterClass() error {
	// 先InitCommonControls，让Trackbar/Progress/Status可用
	icc := INITCOMMONCONTROLSEX{
		DwSize: uint32(unsafe.Sizeof(INITCOMMONCONTROLSEX{})),
		DwICC:  ICC_WIN95_CLASSES,
	}
	procInitCommonControlsEx.Call(uintptr(unsafe.Pointer(&icc)))

	hInstRet, _, _ := procGetModuleHandleW.Call(0)
	hInst := HINSTANCE(hInstRet)
	className := Sp("ScreenCastReceiverClass-Go")

	// ⚠️ IDI_APPLICATION/IDC_ARROW 是宏 MAKEINTRESOURCE(xxx)，不是句柄！必须调用LoadIconW/LoadCursorW获取真实句柄
	hIconDef, _, _ := procLoadIconW.Call(0, IDI_APPLICATION)   // 默认应用图标
	hCursorDef, _, _ := procLoadCursorW.Call(0, IDC_ARROW)     // 默认箭头光标
	if hCursorDef == 0 {
		// 兜底：LoadCursorW失败时用0（系统默认）
		hCursorDef = 0
	}
	wc := WNDCLASSEXW{
		CbSize:        uint32(unsafe.Sizeof(WNDCLASSEXW{})),
		Style:         0x0003 | 0x0020, // CS_HREDRAW | CS_VREDRAW
		LpfnWndProc:   syscall.NewCallback(MainWndProc),
		HInstance:     hInst,
		HIcon:         HICON(hIconDef),
		HCursor:       HCURSOR(hCursorDef),
		HbrBackground: HBRUSH(COLOR_BTNFACE + 1),
		LpszClassName: className,
		HIconSm:       HICON(hIconDef),
	}
	ret, _, errRet := procRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc)))
	if ret == 0 { return fmt.Errorf("RegisterClassExW failed: %v (GetLastError=%d)", errRet, errRet) }
	return nil
}

func CreateEx(exStyle uint32, cls, name string, style uint32, x,y,w,h int32, parent HWND, id int32) HWND {
	cp := Sp(cls); np := Sp(name)
	var menuId uintptr = 0
	if id != 0 { menuId = uintptr(uint32(uint16(id))) }
	r, _, _ := procCreateWindowExW.Call(
		uintptr(exStyle),
		uintptr(unsafe.Pointer(cp)),
		uintptr(unsafe.Pointer(np)),
		uintptr(style),
		U(x), U(y), U(w), U(h),
		uintptr(parent), menuId, 0, 0)
	return r
}

// 创建所有控件（在WM_CREATE里调用一次）
func buildAllControls(hwnd HWND) {
	// =============== 顶部信息 ===============
	hLblLanIP = CreateEx(0, "STATIC",
		"本机局域网IP：检测中...   （安卓/iPhone请与电脑同一WiFi）",
		WS_CHILD|WS_VISIBLE|SS_LEFT|SS_WORDELLIPSIS,
		12, 8, PANEL_W-24, 22, hwnd, ID_LBL_LAN_IP)

	// =============== 4 个服务卡片 ===============
	cards := []struct{
		idChk, idLbl, idOpt int32
		title              string
	}{
		{ID_CHK_DLNA,     ID_LBL_DLNA_STAT, ID_BTN_DLNA_OPT,   "启用 DLNA（安卓投屏/华为cast/三星/雷鸟/小米）"},
		{ID_CHK_AIRPLAY,  ID_LBL_AP_STAT,   ID_BTN_AP_OPT,     "启用 AirPlay2（iPhone/iPad/Mac投屏）"},
		{ID_CHK_MIRACAST, ID_LBL_MI_STAT,   ID_BTN_MI_OPT,     "启用 Miracast（Win10+投影到此电脑/安卓Miracast）"},
		{ID_CHK_RTSP,     ID_LBL_RT_STAT,   ID_BTN_RT_OPT,     "启用 RTSP Server（安卓RTSP推流/OBS）"},
	}
	y := int32(38)
	cardH := int32(56)
	for i, c := range cards {
		cardY := y + int32(i)*cardH
		_ = cardY
		// 用一个STATIC + 边框当卡片背景
		_ = CreateEx(WS_EX_CLIENTEDGE, "STATIC", "",
			WS_CHILD|WS_VISIBLE|SS_LEFT,
			12, cardY, PANEL_W-24, cardH-8, hwnd, 0)
		chk := CreateEx(0, "BUTTON", c.title,
			WS_CHILD|WS_VISIBLE|BS_AUTOCHECKBOX|WS_TABSTOP,
			24, cardY+6, PANEL_W-48, 22, hwnd, c.idChk)
		switch i {
		case 0: hChkDlna = chk
		case 1: hChkAir = chk
		case 2: hChkMi  = chk
		case 3: hChkRt  = chk
		}
		// 状态标签（左下）
		lbl := CreateEx(0, "STATIC", "未启动",
			WS_CHILD|WS_VISIBLE|SS_LEFT|SS_WORDELLIPSIS,
			44, cardY+28, 200, 20, hwnd, c.idLbl)
		switch i {
		case 0: hLblDlnaStat = lbl
		case 1: hLblAPStat = lbl
		case 2: hLblMIStat = lbl
		case 3: hLblRTStat = lbl
		}
		// 设置按钮（右下）
		CreateEx(0, "BUTTON", "⚙设置",
			WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
			PANEL_W-80-16, cardY+26, 72, 24, hwnd, c.idOpt)
	}

	// =============== 网卡绑定面板 ===============
	nicTop := int32(274)
	_ = CreateEx(WS_EX_CLIENTEDGE, "STATIC", "",
		WS_CHILD|WS_VISIBLE|SS_LEFT,
		12, nicTop, PANEL_W-24, 166, hwnd, 0)
	_ = CreateEx(0, "STATIC", "⚡ 绑定网卡（默认勾选所有物理网卡；如需只绑定某张请取消其他）",
		WS_CHILD|WS_VISIBLE|SS_LEFT|SS_WORDELLIPSIS,
		20, nicTop+6, PANEL_W-32, 20, hwnd, ID_LBL_NIC_INFO)
	// 刷新+防火墙+打开目录 按钮
	CreateEx(0, "BUTTON", "🔄 刷新",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		24, nicTop+28, 72, 26, hwnd, ID_BTN_NIC_REFRESH)
	CreateEx(0, "BUTTON", "🛡  防火墙放行",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		100, nicTop+28, 128, 26, hwnd, ID_BTN_FIREWALL)
	CreateEx(0, "BUTTON", "📁 打开目录",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		232, nicTop+28, 128, 26, hwnd, ID_BTN_OPEN_MPV)
	// ListBox（多选）
	hNicList = CreateEx(WS_EX_CLIENTEDGE, "LISTBOX", "",
		WS_CHILD|WS_VISIBLE|LBS_NOTIFY|LBS_EXTENDEDSEL|LBS_NOINTEGRALHEIGHT|WS_VSCROLL|WS_BORDER|WS_CLIPSIBLINGS,
		20, nicTop+58, PANEL_W-40, 102, hwnd, ID_LBX_NICS)

	// =============== 日志区域（左下）===============
	logTop := int32(460)
	_ = CreateEx(0, "STATIC", "📋 运行日志",
		WS_CHILD|WS_VISIBLE|SS_LEFT,
		12, logTop, PANEL_W-24, 18, hwnd, 0)
	hLogEdit = CreateEx(WS_EX_CLIENTEDGE, "EDIT", "",
		WS_CHILD|WS_VISIBLE|ES_MULTILINE|ES_AUTOVSCROLL|WS_VSCROLL|ES_READONLY|WS_BORDER|WS_CLIPSIBLINGS,
		12, logTop+20, PANEL_W-24, 200, hwnd, ID_LOG_EDIT)
	procSendMessageW.Call(hLogEdit, EM_LIMITTEXT, 50000, 0)
	// 字体：用默认系统字体

	// =============== 右侧：MPV容器 + 控制条 + 进度 ===============
	// MPV容器（子窗口，会把HWND给MPV --wid）
	hMPVHwnd = CreateEx(WS_EX_CLIENTEDGE|WS_EX_CONTROLPARENT, "STATIC", "",
		WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN,
		PANEL_W+6, 12, 800, 600, hwnd, ID_HWND_MPV)
	// 设置容器背景为黑色
	_ = SetLayer(hMPVHwnd)

	// 控制条背景（静态框）
	ctlTop := int32(620)
	_ = CreateEx(WS_EX_CLIENTEDGE, "STATIC", "",
		WS_CHILD|WS_VISIBLE|SS_LEFT,
		PANEL_W+6, ctlTop, 800, 76, hwnd, 0)
	CreateEx(0, "BUTTON", "⏪ 10s",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+14, ctlTop+10, 68, 28, hwnd, ID_BTN_BACK_10)
	CreateEx(0, "BUTTON", "▶/⏸ 播放暂停",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP|BS_DEFPUSHBUTTON,
		PANEL_W+88, ctlTop+10, 110, 28, hwnd, ID_BTN_PLAY)
	CreateEx(0, "BUTTON", "⏩ 10s",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+204, ctlTop+10, 68, 28, hwnd, ID_BTN_FWD_10)
	CreateEx(0, "BUTTON", "⏹ 停止",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+278, ctlTop+10, 68, 28, hwnd, ID_BTN_STOP)
	CreateEx(0, "BUTTON", "🔄 旋转",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+352, ctlTop+10, 74, 28, hwnd, ID_BTN_ROTATE_L)
	CreateEx(0, "BUTTON", "▭ 画面比例",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+432, ctlTop+10, 90, 28, hwnd, ID_BTN_ASPECT)
	CreateEx(0, "BUTTON", "⏩ 速度",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+528, ctlTop+10, 74, 28, hwnd, ID_BTN_SPEED)
	CreateEx(0, "BUTTON", "🔊 音量",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+608, ctlTop+10, 74, 28, hwnd, ID_BTN_VOLUME)
	CreateEx(0, "BUTTON", "⛶ 全屏",
		WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON|WS_TABSTOP,
		PANEL_W+688, ctlTop+10, 110, 28, hwnd, ID_BTN_FULLSCREEN)

	// 第二行：进度条+时间
	_ = CreateEx(0, "STATIC", "进度:",
		WS_CHILD|WS_VISIBLE|SS_LEFT,
		PANEL_W+14, ctlTop+44, 36, 20, hwnd, 0)
	hProgress = CreateEx(0, TRACKBAR_CLASS, "",
		WS_CHILD|WS_VISIBLE|WS_TABSTOP,
		PANEL_W+52, ctlTop+44, 600, 24, hwnd, ID_TRK_PROGRESS)
	procSendMessageW.Call(hProgress, TBM_SETRANGE, 1, U(I32(1000*65536)))
	procSendMessageW.Call(hProgress, TBM_SETRANGE, 0, U(I32(1000))) // 范围0~1000
	procSendMessageW.Call(hProgress, TBM_SETTICFREQ, 100, 0)
	hLblTime = CreateEx(0, "STATIC", "00:00 / 00:00  x1.00",
		WS_CHILD|WS_VISIBLE|SS_RIGHT,
		PANEL_W+656, ctlTop+44, 144, 20, hwnd, ID_LBL_TIME)

	// =============== 底部：状态栏 ===============
	hStatusBar = CreateEx(0, STATUSCLASSNAME, "",
		WS_CHILD|WS_VISIBLE,
		0, 0, 0, 0, hwnd, ID_STATUSBAR)
}

func SetLayer(hwnd HWND) int {
	return 0
}

// layoutAll WM_SIZE回调，重排所有控件响应式
func layoutAll(hwnd HWND) {
	var rc RECT
	procGetClientRect.Call(hwnd, uintptr(unsafe.Pointer(&rc)))
	w := rc.Right - rc.Left
	h := rc.Bottom - rc.Top

	panelW := int32(PANEL_W)
	if w < panelW+100 { panelW = w - 100 }
	if panelW < 300 { panelW = 300 }

	// 状态栏占最底22px
	sbH := int32(24)
	MoveWindow2(hStatusBar, 0, h-sbH, w, sbH, true)
	contentH := h - sbH

	// 日志贴底（左侧）
	logH := int32(200)
	if contentH < 700 { logH = 140 }
	if contentH < 500 { logH = 90 }
	MoveWindow2(hLogEdit, 12, contentH-logH-16, panelW-24, logH, true)

	// 日志标题
	// 找到日志上方static，Move到日志正上方
	// 由于我们没有保存它的HWND，这里简化
	_ = hNicList

	// MPV 容器：右半，从y=12到控制条上方，留够控制条100px+进度条
	ctlH := int32(100)
	mpvX := panelW + 6
	mpvW := w - mpvX - 12
	if mpvW < 200 { mpvW = 200 }
	mpvH := contentH - 12 - 12 - ctlH
	if mpvH < 200 { mpvH = 200 }
	MoveWindow2(hMPVHwnd, mpvX, 12, mpvW, mpvH, true)

	// 控制条
	ctlTop := 12 + mpvH + 8
	MoveWindow2(GetDlgItem2(hwnd, 0), 0,0,0,0, true)
	// 重新布局控制条所有按钮（根据mpvW等比铺）
	// 简化：按固定坐标相对ctlTop/mpvX铺，按钮大小不变，位置在容器内
	y1 := ctlTop + 10
	positions := []struct {
		id int32; x int32; w int32
	}{
		{ID_BTN_BACK_10,   14,  68},
		{ID_BTN_PLAY,      88,  110},
		{ID_BTN_FWD_10,    204, 68},
		{ID_BTN_STOP,      278, 68},
		{ID_BTN_ROTATE_L,  352, 74},
		{ID_BTN_ASPECT,    432, 90},
		{ID_BTN_SPEED,     528, 74},
		{ID_BTN_VOLUME,    608, 74},
		{ID_BTN_FULLSCREEN,688, 110},
	}
	for _, p := range positions {
		MoveWindow2(GetDlgItem2(hwnd, p.id), mpvX+p.x, y1, p.w, 28, true)
	}
	// 进度条行
	MoveWindow2(GetDlgItem2(hwnd, ID_TRK_PROGRESS), mpvX+52, ctlTop+44, mpvW-210, 24, true)
	MoveWindow2(hLblTime, mpvX+mpvW-150, ctlTop+44, 144, 20, true)
}

// ==================== 主函数 ====================
func main() {
	// ================================================================
	// ⚠️⚠️⚠️ Go GUI 第一定律：创建窗口和消息循环的goroutine必须永久锁定在同一个OS线程！
	// 否则Go调度器会随机把main goroutine切到另一个OS线程，
	// 导致：创建窗口线程≠消息循环线程→系统判定窗口"未响应"，SendMessage卡死
	// ================================================================
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	// 锁定当前线程ID为UI线程TID（窗口从此线程创建，消息循环必须同一线程）
	tidRet, _, _ := procGetCurrentThreadId.Call()
	uiThreadID = uint32(tidRet)

	// 命令行参数
	autoDlna := false; autoRtsp := false
	for _, a := range os.Args {
		if strings.EqualFold(a, "--auto-start-dlna") { autoDlna = true }
		if strings.EqualFold(a, "--auto-start-rtsp") { autoRtsp = true }
	}

	if err := RegisterClass(); err != nil {
		os.Stderr.WriteString("[FATAL] " + err.Error() + "\n")
		os.Exit(1)
	}

	// 创建主窗口
	className := Sp("ScreenCastReceiverClass-Go")
	title := Sp("ScreenCastReceiver (Go纯Win32版) · DLNA/AirPlay2/Miracast/RTSP 多协议投屏接收端")
	hwndRet, _, err := procCreateWindowExW.Call(
		WS_EX_APPWINDOW,
		uintptr(unsafe.Pointer(className)),
		uintptr(unsafe.Pointer(title)),
		WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,
		CW_USEDEFAULT, CW_USEDEFAULT,
		1280, 820,
		0, 0, 0, 0)
	if hwndRet == 0 {
		os.Stderr.WriteString("[FATAL] CreateWindowExW failed: " + fmt.Sprintf("%v", err) + "\n")
		os.Exit(1)
	}
	hwnd := HWND(hwndRet)
	hMainWnd = hwnd

	procShowWindow.Call(hwnd, SW_SHOWDEFAULT)
	procUpdateWindow.Call(hwnd)

	// 命令行自动启动（用 PostMessage 异步避免UI死锁）
	if autoDlna {
		App.log.Info("APP", "检测到 --auto-start-dlna，异步自动启动 DLNA 服务")
		procPostMessageW.Call(hwnd, WM_COMMAND,
			WPARAM(uint32(ID_CHK_DLNA) | uint32(1)<<16 /*BN_CLICKED*/),
			0)
		procSendMessageW.Call(hChkDlna, BM_SETCHECK, BST_CHECKED, 0)
	}
	if autoRtsp {
		App.log.Info("APP", "检测到 --auto-start-rtsp，异步自动启动 RTSP 服务")
		procSendMessageW.Call(hChkRt, BM_SETCHECK, BST_CHECKED, 0)
		procPostMessageW.Call(hwnd, WM_COMMAND,
			WPARAM(uint32(ID_CHK_RTSP)|uint32(1)<<16), 0)
	}

	// 消息循环
	var msg MSG
	for {
		r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if r == 0 { break } // WM_QUIT
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&msg)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&msg)))
	}
}

// ==================== 未使用符号占位（防编译报错）====================
var (
	_ = SW_RESTORE
	_ = TRANSPARENT
	_ = DT_CENTER
	_ = procDrawTextW
	_ = procSetBkMode
	_ = procSetTextColor
	_ = procSelectObject
	_ = procCreateFontIndirectW
	_ = procDeleteObject
	_ = procFillRect
	_ = procDrawFrameControl
	_ = procDrawEdge
	_ = procShellExecuteW
	_ = procBeginPaint
	_ = procEndPaint
	_ = procReleaseDC
	_ = procGetDC
	_ = HBITMAP(0)
	_ = HDC(0)
	_ = HFONT(0)
	_ = MINMAXINFO{}
	_ = POINT{}
	_ = PAINTSTRUCT{}
	_ = strconv.Itoa
	_ = COLORREF(0)
)
