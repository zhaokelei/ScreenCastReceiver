//go:build windows
// +build windows

package player

import (
	"bufio"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
)

// 播放会话抢占方式
const (
	PriorityAskAlways = "ask"     // 每次都问
	PriorityAskAuto   = "confirm" // 空闲直接播，忙则问（默认）
	PriorityForceNew  = "force"   // 强制抢过来
	PriorityRejectNew = "reject"  // 已有会话就拒绝
)

// Session 一次MPV播放会话
type Session struct {
	ID         string
	Kind       models.ServiceKind
	SourceName string
	SourceIP   string
	URI        string
	Title      string
	StartAt    time.Time

	// 状态（IPC读出来的）
	PositionS  float64
	DurationS  float64
	FPSDisplay float64
	BitrateK   float64
	Speed      float64
	Paused     bool
}

// Manager MPV进程+IPC管理器（全局单例）
type Manager struct {
	mu       sync.Mutex
	log      *logger.RingLogger
	cfg      *models.AppConfig

	cmd      *exec.Cmd
	hwnd     uintptr // 嵌入父窗口句柄
	pipeName string
	conn     net.Conn
	reader   *bufio.Reader
	reqID    int64
	wg       sync.WaitGroup
	stop     chan struct{}

	currentSession *Session
	pendingURI     string // 抢占待确认URL

	// 事件订阅
	OnStateChange   func()
	OnNeedConfirm   func(newSession *Session) bool // 返回true=用户同意抢占
	OnStatsUpdate   func(fps, bitrateK float64, posS, durS float64, speed float64, paused bool)
}

// NewManager 创建管理器
func NewManager(cfg *models.AppConfig, log *logger.RingLogger) *Manager {
	if log == nil { log = logger.Default }
	return &Manager{
		cfg:      cfg,
		log:      log,
		stop:     make(chan struct{}),
		reqID:    1,
	}
}

// CurrentSession 当前会话快照（线程安全）
func (m *Manager) CurrentSession() *Session {
	m.mu.Lock(); defer m.mu.Unlock()
	if m.currentSession == nil { return nil }
	cp := *m.currentSession
	return &cp
}

// resolveMPVPath 解析MPV可执行文件路径
func (m *Manager) resolveMPVPath() (string, error) {
	if m.cfg.MPVPath != "" {
		if _, err := os.Stat(m.cfg.MPVPath); err == nil { return m.cfg.MPVPath, nil }
	}
	exe, _ := os.Executable()
	dir := filepath.Dir(exe)
	// 依次尝试 mpv/mpv.exe / mpv.exe / 相对同级
	candidates := []string{
		filepath.Join(dir, "mpv", "mpv.exe"),
		filepath.Join(dir, "mpv.exe"),
		`C:\Program Files\mpv\mpv.exe`,
		`C:\Program Files (x86)\mpv\mpv.exe`,
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c, nil
		}
	}
	return "", fmt.Errorf("未找到 mpv.exe，请到 https://mpv.io/ 下载并放到程序目录 mpv\\mpv.exe；\n已尝试路径: %v", candidates)
}

// StartMPV 启动MPV子进程并绑定到指定HWND（父窗口），等待IPC就绪
func (m *Manager) StartMPV(parentHWND uintptr) error {
	m.mu.Lock()
	if m.cmd != nil && m.cmd.Process != nil {
		// 已在运行
		m.hwnd = parentHWND
		m.mu.Unlock()
		return nil
	}
	if parentHWND == 0 {
		m.mu.Unlock()
		return fmt.Errorf("播放窗口尚未创建完成（请确认播放画面区域已显示）")
	}
	mpvExe, err := m.resolveMPVPath()
	if err != nil {
		m.mu.Unlock()
		return err
	}
	// 生成独立命名管道
	id := make([]byte, 8)
	rand.Read(id)
	m.pipeName = `\\.\pipe\screencast-mpv-` + hex.EncodeToString(id)
	m.hwnd = parentHWND

	// ========== 构造启动参数 ==========
	args := []string{
		// 嵌入父窗口
		fmt.Sprintf("--wid=%d", parentHWND),
		// 隐藏标题栏/控件
		"--no-osc",
		"--no-border",
		"--no-taskbar-progress",
		// IPC
		fmt.Sprintf("--input-ipc-server=%s", m.pipeName),
		// 解码兼容性（硬解优先，失败自动软解）
		"--hwdec=auto-safe",
		"--vo=direct3d,angle,libmpv,win32shm,auto",
		// 画质优化
		"--scale=ewa_lanczossharp",
		"--cscale=ewa_lanczossoft",
		"--video-sync=display-resample",
		// 日志
		"--msg-level=all=warn",
		// 结束行为：不要退出，等下一个URL
		"--keep-open=yes",
		"--idle=yes",
		// 初始播放速率/画面比例
		fmt.Sprintf("--speed=%.2f", m.cfg.DefaultSpeed),
		fmt.Sprintf("--video-aspect-override=%s", m.cfg.DefaultAspect),
		// 输入锁定（避免误操作覆盖父窗口事件）
		"--input-cursor=no",
		"--cursor-autohide=no",
	}
	m.log.Info("MPV", "启动 %s (wid=0x%X, pipe=%s)", mpvExe, parentHWND, m.pipeName)
	m.cmd = exec.Command(mpvExe, args...)
	// 无控制台 + 与主程序亲和
	m.cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000, // CREATE_NO_WINDOW
	}
	// 不转发stdout（MPV已经有IPC，stdout会有警告）
	if err := m.cmd.Start(); err != nil {
		m.cmd = nil
		m.mu.Unlock()
		return fmt.Errorf("MPV 启动失败: %v", err)
	}
	// 进程结束清理
	m.wg.Add(1)
	go func() {
		defer m.wg.Done()
		state, _ := m.cmd.Process.Wait()
		m.mu.Lock()
		if m.conn != nil { m.conn.Close(); m.conn = nil }
		m.log.Info("MPV", "进程已退出 (code=%v)", state.ExitCode())
		m.cmd = nil
		m.currentSession = nil
		m.mu.Unlock()
		if m.OnStateChange != nil { m.OnStateChange() }
	}()
	m.mu.Unlock()

	// 等待命名管道就绪（最多10秒）
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		_ = net.Dial // 占位（标准库无法Dial Windows命名管道，下方用syscall版dialNamedPipe）
		// Windows Named Pipe Dial（走syscall版）
		c2, err2 := dialNamedPipe(m.pipeName, time.Second)
		if err2 == nil && c2 != nil {
			m.mu.Lock()
			m.conn = c2
			m.reader = bufio.NewReaderSize(c2, 64*1024)
			m.mu.Unlock()
			m.log.Info("MPV", "IPC 连接成功")
			// 启动事件监听循环
			go m.ipcReaderLoop()
			// 初始订阅属性
			m.ObserveProperties()
			if m.OnStateChange != nil { m.OnStateChange() }
			return nil
		}
		time.Sleep(120 * time.Millisecond)
		// 若进程已退出则直接失败
		if m.cmd == nil || m.cmd.Process == nil {
			return fmt.Errorf("MPV 进程已意外退出，请检查mpv.exe完整性（缺少dll？）")
		}
	}
	m.log.Error("MPV", "等待 IPC 命名管道超时（请确认杀毒软件未拦截命名管道）")
	return fmt.Errorf("MPV IPC 连接超时")
}

// StopMPV 强制杀MPV（用户停止服务/退出时调用）
func (m *Manager) StopMPV() {
	m.mu.Lock()
	if m.conn != nil { m.conn.Close(); m.conn = nil }
	if m.cmd != nil && m.cmd.Process != nil {
		pid := m.cmd.Process.Pid
		m.log.Info("MPV", "停止进程 pid=%d", pid)
		m.cmd.Process.Kill()
	}
	m.cmd = nil
	m.mu.Unlock()
	close(m.stop)
	m.wg.Wait()
	m.stop = make(chan struct{})
}

// ============ 核心 IPC：请求/观察 ============

// SendCommand 发送一条MPV JSON命令并等待响应（可选）
func (m *Manager) SendCommand(waitResp bool, args ...interface{}) (interface{}, error) {
	m.mu.Lock()
	if m.conn == nil {
		m.mu.Unlock()
		return nil, fmt.Errorf("MPV 未连接")
	}
	m.reqID++
	id := m.reqID
	m.mu.Unlock()

	req := map[string]interface{}{
		"command": args,
		"request_id": id,
	}
	bs, _ := json.Marshal(req)
	line := string(bs) + "\n"
	m.mu.Lock()
	_, err := m.conn.Write([]byte(line))
	m.mu.Unlock()
	if err != nil { return nil, fmt.Errorf("MPV 写入失败: %v", err) }
	if !waitResp { return nil, nil }
	// 简单读对应ID响应
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		m.mu.Lock()
		if m.reader == nil { m.mu.Unlock(); return nil, fmt.Errorf("无Reader") }
		l, err := m.reader.ReadString('\n')
		m.mu.Unlock()
		if err != nil { return nil, err }
		var resp map[string]interface{}
		json.Unmarshal([]byte(l), &resp)
		if rid, ok := resp["request_id"].(float64); ok && int64(rid) == id {
			if er, ok := resp["error"]; ok && er.(string) != "success" {
				return nil, fmt.Errorf("MPV err=%v", resp)
			}
			return resp["data"], nil
		}
	}
	return nil, fmt.Errorf("MPV 响应超时 command=%v", args)
}

// ObserveProperties 订阅关键属性变化（event push）
func (m *Manager) ObserveProperties() {
	props := []string{
		"time-pos", "duration",
		"estimated-vf-fps", "display-fps", "container-fps",
		"video-bitrate", "audio-bitrate",
		"speed", "pause",
		"video-rotate", "video-aspect-override",
		"media-title", "filename",
	}
	for _, p := range props {
		_, _ = m.SendCommand(true, "observe_property", 1, p)
	}
}

// ipcReaderLoop 异步监听MPV事件（属性变化/EOF）
func (m *Manager) ipcReaderLoop() {
	for {
		m.mu.Lock()
		if m.reader == nil || m.conn == nil { m.mu.Unlock(); return }
		line, err := m.reader.ReadString('\n')
		m.mu.Unlock()
		if err != nil { return }
		var ev map[string]interface{}
		json.Unmarshal([]byte(line), &ev)
		m.applyEvent(ev)
	}
}

// applyEvent 把属性事件同步到currentSession
func (m *Manager) applyEvent(ev map[string]interface{}) {
	e, ok := ev["event"]
	if !ok { return }
	if e.(string) != "property-change" { return }
	name, _ := ev["name"].(string)
	data := ev["data"]
	m.mu.Lock()
	s := m.currentSession
	if s == nil { m.mu.Unlock(); return }
	switch name {
	case "time-pos":
		if v, ok := data.(float64); ok { s.PositionS = v }
	case "duration":
		if v, ok := data.(float64); ok { s.DurationS = v }
	case "display-fps", "container-fps", "estimated-vf-fps":
		if v, ok := data.(float64); ok && v > 1 {
			s.FPSDisplay = v
		}
	case "video-bitrate":
		if v, ok := data.(float64); ok { s.BitrateK = v / 1024.0 }
	case "speed":
		if v, ok := data.(float64); ok { s.Speed = v }
	case "pause":
		if v, ok := data.(bool); ok { s.Paused = v }
	}
	m.mu.Unlock()
	if m.OnStatsUpdate != nil {
		m.OnStatsUpdate(s.FPSDisplay, s.BitrateK, s.PositionS, s.DurationS, s.Speed, s.Paused)
	}
}

// ============ 控制命令 ============

// RequestPlayback 外部调用（DLNA/AirPlay）请求播放URL，会先检查抢占策略
func (m *Manager) RequestPlayback(session *models.ActiveSession, parentHWND uintptr) (error, bool) {
	if m.cfg == nil { m.cfg = models.DefaultAppConfig() }
	// 1) 先确保MPV进程存在
	if err := m.StartMPV(parentHWND); err != nil { return err, false }
	// 2) 抢占策略
	m.mu.Lock()
	existing := m.currentSession
	priority := m.cfg.MPVPriority
	if priority == "" { priority = PriorityAskAuto }
	m.mu.Unlock()

	s := &Session{
		ID:         session.ID,
		Kind:       session.Kind,
		SourceName: session.SourceName,
		SourceIP:   session.SourceIP,
		URI:        session.MediaURI,
		Title:      session.Title,
		StartAt:    time.Now(),
		Speed:      m.cfg.DefaultSpeed,
	}

	confirmNeeded := false
	if existing == nil {
		// 空闲直接播
	} else {
		switch priority {
		case PriorityRejectNew:
			return fmt.Errorf("已有投屏 (%s - %s)，已按策略拒绝新的投屏请求",
				existing.SourceName, existing.Title), false
		case PriorityAskAlways, PriorityAskAuto:
			confirmNeeded = true
			m.pendingURI = s.URI
			// 不立即设置，等用户确认后App再走 ResumePending
			if m.OnNeedConfirm != nil {
				ok := m.OnNeedConfirm(s)
				if !ok {
					return fmt.Errorf("用户拒绝抢占当前投屏"), false
				}
				confirmNeeded = false
			}
		case PriorityForceNew:
			// 继续往下执行替换
		}
	}

	// 3) 实际加载URL
	m.log.Info("MPV", "加载视频: %s (title=%q)", s.URI, s.Title)
	_, err := m.SendCommand(true,
		"loadfile", s.URI, "replace",
		"start=0")
	if err != nil {
		m.log.Warn("MPV", "loadfile失败: %v", err)
		// 备用：用set_property
		_, err2 := m.SendCommand(true, "set_property", "stream-open-filename", s.URI)
		if err2 != nil {
			return fmt.Errorf("loadfile 失败: %v；set_property 也失败: %v", err, err2), confirmNeeded
		}
	}
	// 4) 保存会话
	m.mu.Lock()
	m.currentSession = s
	// 同步到 models.ActiveSession （指针同值时在外部写）
	session.DurationMs = int64(s.DurationS * 1000)
	session.PositionMs = int64(s.PositionS * 1000)
	session.Speed      = s.Speed
	session.Playing    = !s.Paused
	m.mu.Unlock()

	if m.cfg.AutoFullscreen {
		go func() {
			time.Sleep(500 * time.Millisecond)
			m.SendCommand(false, "set_property", "fullscreen", true)
		}()
	}
	if m.OnStateChange != nil { m.OnStateChange() }
	return nil, confirmNeeded
}

// Pause / Resume / Stop / Seek / SetSpeed / SetAspect / SetRotate
func (m *Manager) PlayPauseToggle() { m.SendCommand(false, "cycle", "pause") }
func (m *Manager) Stop()           { m.SendCommand(false, "stop") }
func (m *Manager) Seek(offsetSec float64) {
	m.SendCommand(false, "seek", offsetSec, "relative")
}
func (m *Manager) SetSpeed(x float64) {
	m.SendCommand(false, "set_property", "speed", x)
}
func (m *Manager) SetAspect(aspect string) {
	m.SendCommand(false, "set_property", "video-aspect-override", aspect)
}
func (m *Manager) SetRotate(deg int) {
	m.SendCommand(false, "set_property", "video-rotate", deg)
}
func (m *Manager) SetVolume(pct int) {
	if pct < 0 { pct = 0 }
	if pct > 100 { pct = 100 }
	m.SendCommand(false, "set_property", "volume", pct)
}
func (m *Manager) FullscreenToggle() { m.SendCommand(false, "cycle", "fullscreen") }

// ============ Windows Named Pipe Dial（零外部依赖版）============
var (
	modKernel32    = syscall.NewLazyDLL("kernel32.dll")
	procCreateFile = modKernel32.NewProc("CreateFileW")
	procWaitNamedPipe = modKernel32.NewProc("WaitNamedPipeW")
)
const (
	openExisting   = 3
	genericRead    = 0x80000000
	genericWrite   = 0x40000000
	fileShareRead  = 1
	fileShareWrite = 2
	invalidHandle  = ^uintptr(0)
)

// pipeConn 适配 net.Conn 接口（简化版）
type pipeConn struct {
	handle syscall.Handle
	name   string
}
func (p *pipeConn) Read(b []byte) (int, error) {
	var done uint32
	err := syscall.ReadFile(p.handle, b, &done, nil)
	return int(done), err
}
func (p *pipeConn) Write(b []byte) (int, error) {
	var done uint32
	err := syscall.WriteFile(p.handle, b, &done, nil)
	return int(done), err
}
func (p *pipeConn) Close() error { return syscall.CloseHandle(p.handle) }
func (p *pipeConn) LocalAddr() net.Addr { return &pipeAddr{p.name} }
func (p *pipeConn) RemoteAddr() net.Addr { return &pipeAddr{p.name} }
func (p *pipeConn) SetDeadline(t time.Time) error { return nil }
func (p *pipeConn) SetReadDeadline(t time.Time) error { return nil }
func (p *pipeConn) SetWriteDeadline(t time.Time) error { return nil }
type pipeAddr struct { s string }
func (a *pipeAddr) Network() string { return "pipe" }
func (a *pipeAddr) String() string  { return a.s }

func dialNamedPipe(name string, timeout time.Duration) (net.Conn, error) {
	deadline := time.Now().Add(timeout)
	pnp, _ := syscall.UTF16PtrFromString(name)
	for time.Now().Before(deadline) {
		// WaitNamedPipe：200ms，等待服务端创建完命名管道实例
		ok, _, _ := procWaitNamedPipe.Call(
			uintptr(unsafe.Pointer(pnp)),
			uintptr(200))
		if ok == 0 {
			time.Sleep(80 * time.Millisecond)
			continue
		}
		h, _, err := procCreateFile.Call(
			uintptr(unsafe.Pointer(pnp)),
			genericRead|genericWrite,
			fileShareRead|fileShareWrite,
			0, openExisting, 0, 0)
		if h != invalidHandle {
			return &pipeConn{handle: syscall.Handle(h), name: name}, nil
		}
		_ = err
		time.Sleep(80 * time.Millisecond)
	}
	return nil, fmt.Errorf("命名管道 %s 连接超时", name)
}

// 占位 import 防止编译报未使用
var _ = strings.TrimSpace
var _ = strconv.Itoa
