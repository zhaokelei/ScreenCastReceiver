//go:build windows
// +build windows

package dlna

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
	"screencast-go/internal/network"
)

const (
	ssdpMulticastIPv4 = "239.255.255.250:1900"
	ssdpMaxTTL        = 2
)

// RequestPlayback 请求播放器播放（外部播放器接口，DLNA不直接依赖MPV包）
type RequestPlaybackFn func(session *models.ActiveSession, hwndHint uintptr) (err error, confirmRequired bool)

// DMR DLNA DMR 服务（DMS控制端 + DMR渲染端）
type DMR struct {
	mu        sync.Mutex
	cfg       *models.AppConfig
	bindIPs   []string
	httpPort  int
	udn       string // 设备UUID
	friendly  string // 友好设备名
	log       *logger.RingLogger
	stateCh   chan models.ServiceStateChangedArgs

	http      *http.Server
	httpLn    net.Listener
	ssdpConns []*net.UDPConn
	ssdpStop  chan struct{}
	ssdpWG    sync.WaitGroup

	// 投屏会话（每个InstanceID=0维持一个）
	avTransportURI       string
	avTransportURIMeta   string
	currentInstanceState *models.ActiveSession

	// 状态事件回调（服务→UI）
	OnStateChange    func(models.ServiceStateChangedArgs)
	OnRequestPlay    RequestPlaybackFn  // 播放器接口
	OnNewSession     func(*models.ActiveSession)
}

// NewDMR 构造DLNA服务
func NewDMR(cfg *models.AppConfig, log *logger.RingLogger) *DMR {
	if log == nil { log = logger.Default }
	uuid := make([]byte, 16)
	rand.Read(uuid)
	uuid[6] = (uuid[6] & 0x0F) | 0x40
	uuid[8] = (uuid[8] & 0x3F) | 0x80
	udn := "uuid:" + hex.EncodeToString(uuid[:4]) + "-" +
		hex.EncodeToString(uuid[4:6]) + "-" +
		hex.EncodeToString(uuid[6:8]) + "-" +
		hex.EncodeToString(uuid[8:10]) + "-" +
		hex.EncodeToString(uuid[10:16])

	friendly := cfg.DLNA.DeviceName
	if friendly == "" { friendly = "我的影院-客厅" }

	return &DMR{
		cfg:      cfg,
		udn:      udn,
		friendly: friendly,
		log:      log,
		httpPort: cfg.DLNA.Port,
		stateCh:  make(chan models.ServiceStateChangedArgs, 8),
		ssdpStop: make(chan struct{}),
	}
}

// Status 读取当前服务状态
func (d *DMR) Status() models.ServiceStatus {
	d.mu.Lock(); defer d.mu.Unlock()
	if d.httpLn == nil { return models.StatusStopped }
	return models.StatusRunning
}

// ListenPort 当前HTTP监听端口（实际占用）
func (d *DMR) ListenPort() int {
	d.mu.Lock(); defer d.mu.Unlock()
	if d.httpLn == nil { return 0 }
	return d.httpLn.Addr().(*net.TCPAddr).Port
}

// ===== SSDP 部分 =====

// ssdpListenOneIP 给一个绑定IP启一个UDP MEMBERSHIP
func (d *DMR) ssdpListenOneIP(bindIP string) error {
	gaddr, err := net.ResolveUDPAddr("udp4", ssdpMulticastIPv4)
	if err != nil { return err }
	iface, err := d.ifaceForIP(bindIP)
	var conn *net.UDPConn
	if iface != nil {
		conn, err = net.ListenMulticastUDP("udp4", iface, gaddr)
	} else {
		conn, err = net.ListenMulticastUDP("udp4", nil, gaddr)
	}
	if err != nil { return fmt.Errorf("UDP %s: %w", bindIP, err) }
	if conn == nil { return fmt.Errorf("UDP conn nil for %s", bindIP) }
	d.ssdpConns = append(d.ssdpConns, conn)

	d.ssdpWG.Add(1)
	go d.ssdpReadLoop(conn, bindIP)
	return nil
}

// ifaceForIP 根据绑定IP找对应网卡（用于多网卡multicast membership正确）
func (d *DMR) ifaceForIP(ip string) (*net.Interface, error) {
	ifaces, err := net.Interfaces()
	if err != nil { return nil, err }
	for i := range ifaces {
		itf := ifaces[i]
		addrs, _ := itf.Addrs()
		for _, a := range addrs {
			var n *net.IPNet
			switch v := a.(type) {
			case *net.IPNet: n = v
			default: continue
			}
			if n.IP.String() == ip { return &itf, nil }
		}
	}
	return nil, fmt.Errorf("未找到IP=%s的网卡", ip)
}

// ssdpReadLoop 处理M-SEARCH请求，回NOTIFY
func (d *DMR) ssdpReadLoop(conn *net.UDPConn, myIP string) {
	defer d.ssdpWG.Done()
	buf := make([]byte, 9000)
	_ = conn.SetReadDeadline(time.Time{})
	httpPort := d.ListenPort()
	d.log.Debug("DLNA", "SSDP listen started on %s (UDP 1900)", myIP)
	for {
		select {
		case <-d.ssdpStop: return
		default:
		}
		_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
		n, raddr, err := conn.ReadFromUDP(buf)
		if err != nil {
			if netErr, ok := err.(net.Error); ok && netErr.Timeout() { continue }
			return
		}
		req := string(buf[:n])
		if !strings.HasPrefix(req, "M-SEARCH") { continue }
		stRaw := extractHeader(req, "ST")
		st := strings.ToLower(stRaw)
		mx := extractHeader(req, "MX") // 大部分手机MX=1~3秒
		d.log.Debug("DLNA", "→ 收到 M-SEARCH from %s: ST=%q, MX=%s", raddr.IP.String(), stRaw, mx)
		if st == "" {
			d.log.Warn("DLNA", "收到 M-SEARCH 但 ST 头解析为空，忽略")
			continue
		}

		// 根据ST决定回复什么
		devices := []string{}
		if st == "ssdp:all" || st == "upnp:rootdevice" || strings.Contains(st, "mediarenderer") ||
			strings.Contains(st, "avtransport") || strings.Contains(st, "renderingcontrol") ||
			strings.Contains(st, "connectionmanager") || strings.Contains(st, "schemas-upnp-org:device:") ||
			strings.Contains(st, "schemas-upnp-org:service:") {
			devices = []string{
				"upnp:rootdevice",
				d.udn,
				"urn:schemas-upnp-org:device:MediaRenderer:1",
				"urn:schemas-upnp-org:service:AVTransport:1",
				"urn:schemas-upnp-org:service:RenderingControl:1",
				"urn:schemas-upnp-org:service:ConnectionManager:1",
			}
		}
		if len(devices) == 0 {
			d.log.Debug("DLNA", "ST=%q 不匹配MediaRenderer，跳过响应（from %s）", stRaw, raddr.IP.String())
			continue
		}
		replyCount := 0
		for _, dev := range devices {
			searchTargetArg := dev
			if st == "ssdp:all" { searchTargetArg = "ssdp:all" }
			resp := d.ssdpOKResponse(myIP, httpPort, dev, searchTargetArg)
			nw, werr := conn.WriteToUDP([]byte(resp), raddr)
			if werr == nil && nw > 0 { replyCount++ }
			time.Sleep(25 * time.Millisecond) // 防UDP burst丢包
		}
		d.log.Info("DLNA", "← 响应 M-SEARCH (ST=%s) from %s → 已回复 %d 条 SSDP OK",
			stRaw, raddr.IP.String(), replyCount)
	}
}

// ssdpOKResponse 生成一个SSDP 200 OK回复
func (d *DMR) ssdpOKResponse(bindIP string, httpPort int, usn, searchTarget string) string {
	desc := fmt.Sprintf("http://%s:%d/description.xml", bindIP, httpPort)
	return fmt.Sprintf(
		"HTTP/1.1 200 OK\r\n"+
		"CACHE-CONTROL: max-age=1800\r\n"+
		"DATE: %s\r\n"+
		"EXT:\r\n"+
		"LOCATION: %s\r\n"+
		"SERVER: Windows/10.0 UPnP/1.0 ScreenCastReceiver-Go/1.0\r\n"+
		"ST: %s\r\n"+
		"USN: %s::%s\r\n"+
		"CONTENT-LENGTH: 0\r\n\r\n",
		time.Now().UTC().Format(time.RFC1123),
		desc,
		searchTarget,
		d.udn, usn)
}

// extractHeader 解析HTTP首行头：按":"分割（首行不算），key大小写不敏感
func extractHeader(msg, key string) string {
	k := strings.ToLower(key)
	lines := strings.Split(msg, "\r\n")
	if len(lines) == 1 { lines = strings.Split(msg, "\n") } // 兼容仅LF的情况
	for i, l := range lines {
		if i == 0 { continue } // 跳过 M-SEARCH/NOTIFY/HTTP 首行
		l = strings.TrimSpace(l)
		if l == "" { continue }
		idx := strings.Index(l, ":")
		if idx < 0 { continue }
		hk := strings.ToLower(strings.TrimSpace(l[:idx]))
		if hk == k {
			return strings.TrimSpace(l[idx+1:])
		}
	}
	return ""
}

// ssdpNOTIFYLoop 周期性发NOTIFY（alive）
func (d *DMR) ssdpNOTIFYLoop(bindIP string) {
	d.ssdpWG.Add(1)
	defer d.ssdpWG.Done()
	udpAddr, _ := net.ResolveUDPAddr("udp4", ssdpMulticastIPv4)
	localAddr, _ := net.ResolveUDPAddr("udp4", bindIP+":0")
	conn, err := net.DialUDP("udp4", localAddr, udpAddr)
	if err != nil {
		d.log.Warn("DLNA", "NOTIFY UDP %s dial 失败: %v", bindIP, err)
		return
	}
	defer conn.Close()
	httpPort := d.ListenPort()
	notify := func() {
		count := 0
		for _, dev := range []string{
			"upnp:rootdevice", d.udn,
			"urn:schemas-upnp-org:device:MediaRenderer:1",
			"urn:schemas-upnp-org:service:AVTransport:1",
			"urn:schemas-upnp-org:service:RenderingControl:1",
			"urn:schemas-upnp-org:service:ConnectionManager:1",
		} {
			msg := fmt.Sprintf(
				"NOTIFY * HTTP/1.1\r\n"+
					"HOST: 239.255.255.250:1900\r\n"+
					"CACHE-CONTROL: max-age=1800\r\n"+
					"LOCATION: http://%s:%d/description.xml\r\n"+
					"NT: %s\r\n"+
					"NTS: ssdp:alive\r\n"+
					"SERVER: Windows/10.0 UPnP/1.0 ScreenCastReceiver-Go/1.0\r\n"+
					"USN: %s::%s\r\n"+
					"BOOTID.UPNP.ORG: 1\r\n"+
					"CONFIGID.UPNP.ORG: 1\r\n"+
					"CONTENT-LENGTH: 0\r\n\r\n",
				bindIP, httpPort, dev, d.udn, dev)
			n, werr := conn.Write([]byte(msg))
			if werr == nil && n > 0 { count++ }
			time.Sleep(40 * time.Millisecond)
		}
		d.log.Debug("DLNA", "SSDP NOTIFY sent via %s (%d条)", bindIP, count)
	}
	// 启动时连发3次（1秒间隔），让手机快速发现（否则要等下一个Ticker）
	for i := 0; i < 3; i++ { notify(); time.Sleep(1 * time.Second) }
	// ⚠️ 规范推荐 NOTIFY 间隔 < max-age/5（1800/5=360秒），手机投屏APP推荐 30~60秒发一次
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-d.ssdpStop: return
		case <-t.C: notify()
		}
	}
}

// Start 启动DLNA服务（HTTP+SSDP）
func (d *DMR) Start(bindIPs []string) error {
	d.mu.Lock()
	if d.httpLn != nil { d.mu.Unlock(); return fmt.Errorf("DLNA 已在运行") }
	d.bindIPs = bindIPs
	if len(d.bindIPs) == 0 || (len(d.bindIPs) == 1 && d.bindIPs[0] == "0.0.0.0") {
		// 兜底：自动枚举所有物理网卡IPv4（如果传了空/0.0.0.0）
		d.bindIPs = resolvePhysicalIPv4s()
		d.log.Warn("DLNA", "绑定IP为空，自动选中 %d 个物理网卡: %v", len(d.bindIPs), d.bindIPs)
	}
	d.mu.Unlock()

	if d.OnStateChange != nil {
		d.OnStateChange(models.ServiceStateChangedArgs{Kind: models.SvcDlna, Status: models.StatusStarting})
	}
	// 1) 启动HTTP（先在0.0.0.0监听固定/自动端口，所有绑定IP都能访问）
	addr := fmt.Sprintf("0.0.0.0:%d", d.httpPort)
	ln, err := net.Listen("tcp4", addr)
	if err != nil {
		if d.OnStateChange != nil {
			d.OnStateChange(models.ServiceStateChangedArgs{Kind: models.SvcDlna, Status: models.StatusFailed, Detail: err.Error()})
		}
		return fmt.Errorf("DLNA HTTP 监听 %s 失败: %w", addr, err)
	}
	d.httpLn = ln
	d.http = &http.Server{Handler: d, ReadHeaderTimeout: 8 * time.Second}
	go d.http.Serve(ln)
	realPort := d.ListenPort()
	d.log.Info("DLNA", "HTTP 控制服务器已启动, 端口=%d (所有网卡0.0.0.0可访问)", realPort)

	// 2) SSDP：每个绑定IP启一个组播监听 + 一个NOTIFY发送
	ssdpListenOK := 0
	ssdpNotifyOK := 0
	for _, ip := range d.bindIPs {
		if ip == "0.0.0.0" { continue }
		if err := d.ssdpListenOneIP(ip); err != nil {
			d.log.Warn("DLNA", "SSDP 监听 %s 失败(跳过): %v", ip, err)
			continue
		}
		ssdpListenOK++
		go d.ssdpNOTIFYLoop(ip)
		ssdpNotifyOK++
	}
	d.log.Info("DLNA", "SSDP 初始化完成：绑定IP=%d 个, 组播监听成功=%d, NOTIFY宣告启动=%d, UDP端口=1900",
		len(d.bindIPs), ssdpListenOK, ssdpNotifyOK)

	if ssdpListenOK == 0 {
		d.log.Warn("DLNA", "⚠️ SSDP 组播监听全部失败！手机无法搜到设备，请检查：①网卡是否有IPv4 ②Windows防火墙是否阻止UDP 1900 ③是否有VPN/虚拟网卡冲突")
	} else {
		d.log.Info("DLNA", "SSDP 已就绪，安卓投屏 APP 可搜索到设备 %q（启动后3秒内连发3次NOTIFY）", d.friendly)
	}

	if d.OnStateChange != nil {
		d.OnStateChange(models.ServiceStateChangedArgs{
			Kind: models.SvcDlna, Status: models.StatusRunning,
			Detail: fmt.Sprintf("设备名=%s | 绑定IP=%d | SSDP监听=%d", d.friendly, len(d.bindIPs), ssdpListenOK), Port: realPort})
	}
	return nil
}

// resolvePhysicalIPv4s 兜底枚举：当配置绑定IP为空时，自动选所有OperUp的物理网卡IPv4
// 复用 network.EnumNICs 的 IsPhysical 判断逻辑，避免重复造轮子导致判断不一致
func resolvePhysicalIPv4s() []string {
	out := make([]string, 0, 4)
	nics, err := network.EnumNICs(nil) // 传nil日志→用默认
	if err != nil {
		// 兜底失败：最后再直接扫net.Interfaces（只要Up+非Loopback+有IPv4就加）
		ifaces, err2 := net.Interfaces()
		if err2 != nil { return nil }
		for _, itf := range ifaces {
			if itf.Flags&net.FlagUp == 0 || itf.Flags&net.FlagLoopback != 0 { continue }
			addrs, _ := itf.Addrs()
			for _, a := range addrs {
				if n, ok := a.(*net.IPNet); ok && n.IP.To4() != nil && !n.IP.IsLoopback() && !n.IP.IsLinkLocalUnicast() {
					out = append(out, n.IP.String())
				}
			}
		}
		return out
	}
	for _, n := range nics {
		if n.IsPhysical && n.OperUp && len(n.IPv4List) > 0 {
			out = append(out, n.IPv4List...)
		}
	}
	return out
}

// Stop 停止DLNA服务
func (d *DMR) Stop() error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.OnStateChange != nil {
		d.OnStateChange(models.ServiceStateChangedArgs{Kind: models.SvcDlna, Status: models.StatusStopping})
	}
	if d.http != nil {
		d.http.Close()
		d.http = nil
	}
	if d.httpLn != nil {
		d.httpLn.Close()
		d.httpLn = nil
	}
	close(d.ssdpStop)
	d.ssdpWG.Wait()
	for _, c := range d.ssdpConns {
		c.Close()
	}
	d.ssdpConns = nil
	d.ssdpStop = make(chan struct{})
	if d.OnStateChange != nil {
		d.OnStateChange(models.ServiceStateChangedArgs{Kind: models.SvcDlna, Status: models.StatusStopped})
	}
	d.log.Info("DLNA", "服务已停止")
	return nil
}

// ===== HTTP 路由（描述 + SCPD + SOAP 控制）=====

func (d *DMR) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	ua := r.Header.Get("User-Agent")
	switch r.URL.Path {
	case "/description.xml":
		d.log.Info("DLNA", "GET /description.xml from %s (UA=%s)", r.RemoteAddr, ua)
		d.writeDescriptionXML(w)
	case "/scpd/AVTransport.xml":
		d.log.Info("DLNA", "GET /scpd/AVTransport.xml from %s (UA=%s)", r.RemoteAddr, ua)
		w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
		w.WriteHeader(200)
		w.Write([]byte(avTransportSCPD))
	case "/scpd/ConnectionManager.xml":
		d.log.Info("DLNA", "GET /scpd/ConnectionManager.xml from %s (UA=%s)", r.RemoteAddr, ua)
		w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
		w.WriteHeader(200)
		w.Write([]byte(connectionManagerSCPD))
	case "/scpd/RenderingControl.xml":
		d.log.Info("DLNA", "GET /scpd/RenderingControl.xml from %s (UA=%s)", r.RemoteAddr, ua)
		w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
		w.WriteHeader(200)
		w.Write([]byte(renderingControlSCPD))
	case "/control/AVTransport":
		d.handleAVTransport(w, r)
	case "/control/ConnectionManager":
		d.handleConnectionManager(w, r)
	case "/control/RenderingControl":
		d.handleRenderingControl(w, r)
	default:
		http.NotFound(w, r)
	}
}

// writeDescriptionXML 设备描述XML
func (d *DMR) writeDescriptionXML(w http.ResponseWriter) {
	realPort := d.ListenPort()
	host := ""
	// 挑一个绑定IP（不是0.0.0.0）
	for _, ip := range d.bindIPs { if ip != "0.0.0.0" { host = fmt.Sprintf("http://%s:%d", ip, realPort); break } }
	if host == "" {
		host = fmt.Sprintf("http://127.0.0.1:%d", realPort)
	}
	xmls := fmt.Sprintf(descriptionTpl,
		xmlEscape(d.friendly),
		xmlEscape(host+"/icons/fav.png"),
		d.udn,
		host,
		host,
		host,
		host)
	w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
	w.Header().Set("Server", "Windows/10.0 UPnP/1.0 ScreenCastReceiver/1.0")
	w.WriteHeader(200)
	w.Write([]byte(xmls))
}

func xmlEscape(s string) string {
	s = strings.ReplaceAll(s, "&", "&amp;")
	s = strings.ReplaceAll(s, "<", "&lt;")
	s = strings.ReplaceAll(s, ">", "&gt;")
	s = strings.ReplaceAll(s, "\"", "&quot;")
	s = strings.ReplaceAll(s, "'", "&apos;")
	return s
}

// ===== 启动HTTP时如果cfg给的端口=0，由net.Listen自动分配，OK。=====
// ===== 其余SOAP控制逻辑在 soap.go =====

// 外部取路径
func exeDir() string {
	if p, err := os.Executable(); err == nil {
		return strings.ReplaceAll(filepathDir(p), "\\", "/")
	}
	return "."
}
func filepathDir(p string) string {
	for i := len(p) - 1; i >= 0; i-- {
		if p[i] == '/' || p[i] == '\\' { return p[:i] }
	}
	return p
}

// 占位
var _ = url.Values{}
