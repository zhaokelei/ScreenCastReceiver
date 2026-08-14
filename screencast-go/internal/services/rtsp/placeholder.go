//go:build windows
// +build windows

package rtsp

import (
	"fmt"
	"net"
	"strings"
	"sync"
	"time"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
)

// Placeholder RTSP占位服务（提供OPTIONS/DESCRIBE/SETUP/PLAY最小响应 + RTSP端口监听；
// 后续可用 github.com/aler9/rtsp-simple-server 做真正的RTSP媒体源分发；
// 这里最小实现保证：端口占用OK、可响应安卓RTSP客户端的OPTIONS，方便用户用OBS/安卓RTSP推流）
type Placeholder struct {
	mu      sync.Mutex
	running bool
	port    int
	ln      net.Listener
	stopCh  chan struct{}
	wg      sync.WaitGroup

	Log  *logger.RingLogger
	Kind models.ServiceKind
}

func (p *Placeholder) Status() models.ServiceStatus {
	p.mu.Lock(); defer p.mu.Unlock()
	if p.running { return models.StatusRunning }
	return models.StatusStopped
}
func (p *Placeholder) ListenPort() int {
	p.mu.Lock(); defer p.mu.Unlock()
	return p.port
}

func (p *Placeholder) Start() error {
	if p.Log == nil { p.Log = logger.Default }
	port := 8554
	addr := fmt.Sprintf("0.0.0.0:%d", port)
	ln, err := net.Listen("tcp4", addr)
	if err != nil {
		p.Log.Error("RTSP", "监听 %s 失败: %v", addr, err)
		return fmt.Errorf("RTSP 端口 %d 被占用: %w", port, err)
	}
	p.mu.Lock()
	p.ln = ln
	p.running = true
	p.port = ln.Addr().(*net.TCPAddr).Port
	p.stopCh = make(chan struct{})
	p.mu.Unlock()

	p.Log.Info("RTSP", "RTSP Server 启动，监听端口=%d （占位，接受OPTIONS/DESCRIBE）", p.port)
	p.Log.Info("RTSP", "安卓/OBS推流地址：rtsp://<你的电脑IP>:%d/live.sdp", p.port)

	p.wg.Add(1)
	go func() {
		defer p.wg.Done()
		for {
			p.mu.Lock()
			l := p.ln
			p.mu.Unlock()
			if l == nil { return }
			_ = l.(*net.TCPListener).SetDeadline(time.Now().Add(500 * time.Millisecond))
			c, err := l.Accept()
			if err != nil {
				select {
				case <-p.stopCh: return
				default: continue
				}
			}
			go p.handleClient(c)
		}
	}()
	return nil
}

func (p *Placeholder) handleClient(c net.Conn) {
	defer c.Close()
	c.SetReadDeadline(time.Now().Add(10 * time.Second))
	buf := make([]byte, 4096)
	n, _ := c.Read(buf)
	if n == 0 { return }
	req := string(buf[:n])
	// 读取第一行
	lines := splitLines(req)
	if len(lines) == 0 { return }
	method := firstField(lines[0])
	p.Log.Info("RTSP", "%s from %s: %s", method, c.RemoteAddr(),
		firstLineShort(lines[0]))

	var resp string
	switch strings.ToUpper(method) {
	case "OPTIONS":
		resp = fmt.Sprintf(
			"RTSP/1.0 200 OK\r\n"+
			"CSeq: %s\r\n"+
			"Public: OPTIONS, DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE, ANNOUNCE\r\n"+
			"\r\n", getCSeq(lines))
	case "DESCRIBE":
		// 响应一个最简SDP（告诉客户端我们有音视频流）
		sdp := "v=0\r\n" +
			"o=- 0 0 IN IP4 127.0.0.1\r\n" +
			"s=ScreenCastReceiver-Go RTSP Placeholder\r\n" +
			"c=IN IP4 0.0.0.0\r\n" +
			"t=0 0\r\n" +
			"m=video 0 RTP/AVP 96\r\n" +
			"a=rtpmap:96 H264/90000\r\n" +
			"m=audio 0 RTP/AVP 97\r\n" +
			"a=rtpmap:97 MPEG4-GENERIC/44100/2\r\n"
		resp = fmt.Sprintf(
			"RTSP/1.0 200 OK\r\n"+
			"CSeq: %s\r\n"+
			"Content-Type: application/sdp\r\n"+
			"Content-Length: %d\r\n"+
			"\r\n%s", getCSeq(lines), len(sdp), sdp)
	case "SETUP", "PLAY", "ANNOUNCE", "PAUSE", "TEARDOWN":
		resp = fmt.Sprintf(
			"RTSP/1.0 200 OK\r\n"+
			"CSeq: %s\r\n"+
			"\r\n", getCSeq(lines))
	default:
		resp = fmt.Sprintf(
			"RTSP/1.0 405 Method Not Allowed\r\n"+
			"CSeq: %s\r\n"+
			"\r\n", getCSeq(lines))
	}
	c.SetWriteDeadline(time.Now().Add(5 * time.Second))
	c.Write([]byte(resp))
}

func splitLines(s string) []string   { return strings.Split(s, "\n") }
func firstField(s string) string    { f := strings.Fields(s); if len(f) > 0 { return f[0] }; return "" }
func firstLineShort(s string) string {
	s = strings.TrimSpace(s)
	if len(s) > 160 { return s[:160] + "..." }
	return s
}
func getCSeq(lines []string) string {
	k := "cseq:"
	for _, l := range lines {
		ll := strings.ToLower(strings.TrimSpace(l))
		if strings.HasPrefix(ll, k) {
			return strings.TrimSpace(strings.TrimLeft(ll, k))
		}
	}
	return "1"
}

func (p *Placeholder) Stop() error {
	p.mu.Lock()
	select {
	case <-p.stopCh:
	default: close(p.stopCh)
	}
	if p.ln != nil { p.ln.Close(); p.ln = nil }
	p.running = false
	p.port = 0
	p.mu.Unlock()
	p.wg.Wait()
	p.Log.Info("RTSP", "服务已停止")
	return nil
}

// 引用 strings（already imported）
var _ = fmt.Sprintf
