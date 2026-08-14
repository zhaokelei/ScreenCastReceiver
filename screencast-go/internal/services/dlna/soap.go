//go:build windows
// +build windows

package dlna

import (
	"encoding/xml"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"

	"screencast-go/internal/models"
)

// ==================== SOAP Envelope 通用结构 ====================
type soapEnvelope struct {
	XMLName xml.Name `xml:"Envelope"`
	Xmlns   string   `xml:"xmlns:s,attr"`
	Enc     string   `xml:"s:encodingStyle,attr"`
	Body    soapBody `xml:"Body"`
}
type soapBody struct {
	Inner string `xml:",innerxml"`
}

// parseSoapAction 保留接口兼容（空实现，实际用extractXMLElement）
func parseSoapAction(body []byte, dst interface{}) {
	_ = body
	_ = dst
}

// extractXMLElement 从XML字符串中提取指定标签的文本内容
// 兼容带命名空间前缀：<u:CurrentURI>、<CurrentURI>、<s:CurrentURI> 等
func extractXMLElement(raw, tagName string) string {
	// 找结束标签 </xxx:tagName> 或 </tagName>
	// 搜索策略：在raw中找 "</" 开头且包含 tagName 且 ">" 结尾的片段
	closeIdx := -1
	searchEnd := tagName + ">"
	for i := 0; i < len(raw)-1; i++ {
		if raw[i] == '<' && raw[i+1] == '/' {
			gt := strings.IndexByte(raw[i:], '>')
			if gt > 0 && strings.Contains(raw[i:i+gt], tagName) {
				closeIdx = i
				_ = searchEnd // 保留用于未来优化
				break
			}
		}
	}
	if closeIdx < 0 { return "" }

	// 从closeIdx往前找开始标签 <xxx:tagName ...> 或 <tagName ...>
	before := raw[:closeIdx]
	openContentStart := -1
	for i := len(before) - 1; i >= 0; i-- {
		if before[i] == '<' && i+1 < len(before) && before[i+1] != '/' {
			gt := strings.IndexByte(before[i:], '>')
			if gt > 0 && strings.Contains(before[i:i+gt], tagName) {
				openContentStart = i + gt + 1
				break
			}
		}
	}
	if openContentStart < 0 || openContentStart > closeIdx { return "" }

	content := raw[openContentStart:closeIdx]
	return strings.TrimSpace(decodeXMLEntities(content))
}

func decodeXMLEntities(s string) string {
	s = strings.ReplaceAll(s, "&lt;", "<")
	s = strings.ReplaceAll(s, "&gt;", ">")
	s = strings.ReplaceAll(s, "&quot;", "\"")
	s = strings.ReplaceAll(s, "&apos;", "'")
	s = strings.ReplaceAll(s, "&amp;", "&")
	return s
}

// ============ AVTransport 动作请求解析 ============
type setAvTransportURI struct {
	XMLName        xml.Name `xml:"SetAVTransportURI"`
	InstanceID     string   `xml:"InstanceID"`
	CurrentURI     string   `xml:"CurrentURI"`
	CurrentURIMeta string   `xml:"CurrentURIMetaData"`
}
type play struct {
	XMLName    xml.Name `xml:"Play"`
	InstanceID string   `xml:"InstanceID"`
	Speed      string   `xml:"Speed"`
}
type stop struct {
	XMLName    xml.Name `xml:"Stop"`
	InstanceID string   `xml:"InstanceID"`
}
type pause struct {
	XMLName    xml.Name `xml:"Pause"`
	InstanceID string   `xml:"InstanceID"`
}
type seek struct {
	XMLName    xml.Name `xml:"Seek"`
	InstanceID string   `xml:"InstanceID"`
	Unit       string   `xml:"Unit"`
	Target     string   `xml:"Target"`
}
type getMediaInfo struct {
	XMLName    xml.Name `xml:"GetMediaInfo"`
	InstanceID string   `xml:"InstanceID"`
}
type getTransportInfo struct {
	XMLName    xml.Name `xml:"GetTransportInfo"`
	InstanceID string   `xml:"InstanceID"`
}
type getPositionInfo struct {
	XMLName    xml.Name `xml:"GetPositionInfo"`
	InstanceID string   `xml:"InstanceID"`
}

// ============ AVTransport 响应 ============
func soapActionResponse(action, serviceType, bodyInner string) []byte {
	return []byte(fmt.Sprintf(
		`<?xml version="1.0" encoding="utf-8"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
<s:Body><u:%sResponse xmlns:u="%s">%s</u:%sResponse></s:Body>
</s:Envelope>`+"\r\n", action, serviceType, bodyInner, action))
}

// ============ 内部状态：Instance0 ============
type instance0 struct {
	mu         sync.Mutex
	URI        string
	URIMeta    string
	Transport  string // STOPPED/PLAYING/TRANSITIONING/PAUSED_PLAYBACK
	TargetMs   int64  // Seek目标，单位ms
	PositionMs int64  // 当前播放位置
	DurationMs int64  // 总时长
	Speed      string
	Title      string
	SourceAddr string
	SourceUA   string
}

var inst0 instance0

// UpdatePlayback 从MPV播放器同步实际播放状态到DLNA实例
// posMs=当前播放位置(ms)，durMs=总时长(ms)，paused=是否暂停，playing=是否正在播放
func (d *DMR) UpdatePlayback(posMs, durMs int64, paused, playing bool) {
	inst0.mu.Lock()
	defer inst0.mu.Unlock()
	inst0.PositionMs = posMs
	inst0.DurationMs = durMs
	if playing {
		if paused {
			inst0.Transport = "PAUSED_PLAYBACK"
		} else {
			inst0.Transport = "PLAYING"
		}
	} else if inst0.Transport != "STOPPED" {
		// MPV停止了但DLNA端还以为在播放→同步为STOPPED
		inst0.Transport = "STOPPED"
	}
}

func writeSoapOK(w http.ResponseWriter, action, serviceType string, bodyInner string) {
	w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
	w.Header().Set("EXT", "")
	w.WriteHeader(200)
	w.Write(soapActionResponse(action, serviceType, bodyInner))
}

func writeSoapError(w http.ResponseWriter, code int, desc string) {
	msg := fmt.Sprintf(`<?xml version="1.0" encoding="utf-8"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>
<detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>%d</errorCode><errorDescription>%s</errorDescription></UPnPError></detail>
</s:Fault></s:Body></s:Envelope>`, code, desc)
	w.Header().Set("Content-Type", `text/xml; charset="utf-8"`)
	w.WriteHeader(500)
	w.Write([]byte(msg))
}

// formatDLNATime ms -> "H:MM:SS.fff" or "0:00:00.000"
func formatDLNATime(ms int64) string {
	if ms < 0 { ms = 0 }
	h := ms / 3600000
	ms -= h * 3600000
	m := ms / 60000
	ms -= m * 60000
	s := ms / 1000
	f := ms % 1000
	return fmt.Sprintf("%d:%02d:%02d.%03d", h, m, s, f)
}
func parseDLNATime(v string) int64 {
	v = strings.TrimSpace(v)
	var h, m, s, f int64
	if strings.Contains(v, ".") {
		fmt.Sscanf(v, "%d:%d:%d.%d", &h, &m, &s, &f)
	} else {
		fmt.Sscanf(v, "%d:%d:%d", &h, &m, &s)
	}
	return h*3600000 + m*60000 + s*1000 + f
}

// ===== AVTransport 处理 =====
func (d *DMR) handleAVTransport(w http.ResponseWriter, r *http.Request) {
	soapAction := r.Header.Get("SOAPACTION")
	ua := r.Header.Get("User-Agent")
	remoteIP := r.RemoteAddr
	if i := strings.LastIndex(remoteIP, ":"); i >= 0 { remoteIP = remoteIP[:i] }

	// 读 body
	body, err := io.ReadAll(io.LimitReader(r.Body, 1024*1024))
	if err != nil { writeSoapError(w, 402, "body 读取失败"); return }
	r.Body.Close()

	d.log.Info("DLNA", `SOAP %s from %s, body=%d字节 (UA=%s)`, soapAction, remoteIP, len(body), ua)
	_ = d
	_ = ua
	serviceType := "urn:schemas-upnp-org:service:AVTransport:1"

	inst0.mu.Lock()
	defer inst0.mu.Unlock()

	// 分发动作
	rawBody := string(body)
	switch {
	case strings.Contains(soapAction, "SetAVTransportURI"):
		// ⚠️ 不用Go的xml.Unmarshal（对SOAP命名空间支持差），直接字符串提取
		currentURI := extractXMLElement(rawBody, "CurrentURI")
		currentMeta := extractXMLElement(rawBody, "CurrentURIMetaData")
		instanceID := extractXMLElement(rawBody, "InstanceID")
		// 调试：打印原始body前500字符（排查解析失败时用）
		preview := rawBody
		if len(preview) > 500 { preview = preview[:500] + "..." }
		d.log.Debug("DLNA", "SetAVTransportURI raw body(%d字节): %s", len(body), preview)
		d.log.Info("DLNA", "解析结果: InstanceID=%s, CurrentURI长度=%d, Meta长度=%d",
			instanceID, len(currentURI), len(currentMeta))

		d.avTransportURI = currentURI
		d.avTransportURIMeta = currentMeta
		inst0.URI = currentURI
		inst0.URIMeta = currentMeta
		inst0.Transport = "TRANSITIONING"
		inst0.Speed = "1"
		inst0.PositionMs = 0
		inst0.SourceAddr = remoteIP
		inst0.SourceUA = ua
		inst0.Title = extractTitleFromDIDL(currentMeta)
		d.log.Info("DLNA", "收到投屏视频链接: `%s`", currentURI)
		// 创建会话并回调播放器（非阻塞，仅通知UI+创建session）
		d.mu.Lock()
		session := &models.ActiveSession{
			ID:         fmt.Sprintf("dlna-%d", time.Now().Unix()),
			Kind:       models.SvcDlna,
			SourceName: remoteIP,
			SourceIP:   remoteIP,
			MediaURI:   currentURI,
			Title:      inst0.Title,
			StartAt:    time.Now(),
			Playing:    false,
		}
		d.currentInstanceState = session
		d.mu.Unlock()
		if d.OnRequestPlay != nil {
			go func() {
				// UI线程中调用请求MPV HWND
				err, _ := d.OnRequestPlay(session, 0)
				if err != nil {
					d.log.Error("DLNA", "创建 MPV 会话失败（请确认 mpv 播放器已放入程序目录 mpv\\mpv.exe）: %v", err)
					d.log.Error("DLNA", "MPV 加载失败（可能是 DRM/加密流）: %v", err)
				} else {
					d.log.Info("DLNA", "MPV 已加载视频，待 Play 命令开始播放")
				}
			}()
		}
		if d.OnNewSession != nil { go d.OnNewSession(session) }
		writeSoapOK(w, "SetAVTransportURI", serviceType, "")

	case strings.Contains(soapAction, "Play"):
		speed := extractXMLElement(rawBody, "Speed")
		inst0.Transport = "PLAYING"
		if speed != "" { inst0.Speed = speed }
		d.log.Info("DLNA", "SOAP \"Play\" from %s (Speed=%s)", remoteIP, inst0.Speed)
		writeSoapOK(w, "Play", serviceType, "")

	case strings.Contains(soapAction, "Stop"):
		inst0.Transport = "STOPPED"
		d.log.Info("DLNA", "SOAP \"Stop\" from %s", remoteIP)
		writeSoapOK(w, "Stop", serviceType, "")

	case strings.Contains(soapAction, "Pause"):
		inst0.Transport = "PAUSED_PLAYBACK"
		d.log.Info("DLNA", "SOAP \"Pause\" from %s", remoteIP)
		writeSoapOK(w, "Pause", serviceType, "")

	case strings.Contains(soapAction, "Seek"):
		unit := extractXMLElement(rawBody, "Unit")
		target := extractXMLElement(rawBody, "Target")
		tMs := parseDLNATime(target)
		inst0.TargetMs = tMs
		d.log.Info("DLNA", "SOAP \"Seek\" (%s=%s) from %s = %d ms", unit, target, remoteIP, tMs)
		writeSoapOK(w, "Seek", serviceType, "")

	case strings.Contains(soapAction, "GetMediaInfo"):
		var req getMediaInfo
		parseSoapAction(body, &req)
		_ = req
		d.log.Info("DLNA", "SOAP \"GetMediaInfo\" from %s, body=%d字节", remoteIP, len(body))
		body := fmt.Sprintf(`<NrTracks>1</NrTracks>
<MediaDuration>%s</MediaDuration>
<CurrentURI>%s</CurrentURI>
<CurrentURIMetaData>%s</CurrentURIMetaData>
<NextURI></NextURI>
<NextURIMetaData></NextURIMetaData>
<PlayMedium>NONE</PlayMedium>
<RecordMedium>NONE</RecordMedium>
<WriteStatus>NOT_WRITABLE</WriteStatus>`,
			formatDLNATime(inst0.DurationMs),
			xmlEscape(inst0.URI),
			xmlEscape(inst0.URIMeta))
		writeSoapOK(w, "GetMediaInfo", serviceType, body)

	case strings.Contains(soapAction, "GetTransportInfo"):
		var req getTransportInfo
		parseSoapAction(body, &req)
		_ = req
		body := fmt.Sprintf(`<CurrentTransportState>%s</CurrentTransportState>
<CurrentTransportStatus>OK</CurrentTransportStatus>
<CurrentSpeed>%s</CurrentSpeed>`, inst0.Transport, inst0.Speed)
		writeSoapOK(w, "GetTransportInfo", serviceType, body)

	case strings.Contains(soapAction, "GetPositionInfo"):
		var req getPositionInfo
		parseSoapAction(body, &req)
		_ = req
		// 无媒体时Track=0，有媒体时Track=1
		track := 0
		if inst0.URI != "" { track = 1 }
		body := fmt.Sprintf(`<Track>%d</Track>
<TrackDuration>%s</TrackDuration>
<TrackMetaData>%s</TrackMetaData>
<TrackURI>%s</TrackURI>
<RelTime>%s</RelTime>
<AbsTime>%s</AbsTime>
<RelCount>2147483647</RelCount>
<AbsCount>2147483647</AbsCount>`,
			track,
			formatDLNATime(inst0.DurationMs),
			xmlEscape(inst0.URIMeta),
			xmlEscape(inst0.URI),
			formatDLNATime(inst0.PositionMs),
			formatDLNATime(inst0.PositionMs))
		writeSoapOK(w, "GetPositionInfo", serviceType, body)

	case strings.Contains(soapAction, "GetTransportSettings"):
		writeSoapOK(w, "GetTransportSettings", serviceType,
			`<PlayMode>NORMAL</PlayMode><RecQualityMode>INVALID</RecQualityMode>`)

	case strings.Contains(soapAction, "GetCurrentTransportActions"):
		writeSoapOK(w, "GetCurrentTransportActions", serviceType,
			`<Actions>Play,Stop,Pause,Seek</Actions>`)

	case strings.Contains(soapAction, "SetNextAVTransportURI"):
		writeSoapOK(w, "SetNextAVTransportURI", serviceType, "")
	case strings.Contains(soapAction, "GetDeviceCapabilities"):
		writeSoapOK(w, "GetDeviceCapabilities", serviceType,
			`<PlayMedia>NONE</PlayMedia><RecMedia>NONE</RecMedia><RecQualityModes>NONE</RecQualityModes>`)

	default:
		d.log.Warn("DLNA", "未处理的 AVTransport SOAP: %s", soapAction)
		writeSoapError(w, 401, "Invalid Action")
	}
}

// ============ ConnectionManager 最简响应（安卓投屏App必查）============
func (d *DMR) handleConnectionManager(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(io.LimitReader(r.Body, 128*1024))
	r.Body.Close()
	soapAction := r.Header.Get("SOAPACTION")
	_ = body
	st := "urn:schemas-upnp-org:service:ConnectionManager:1"
	switch {
	case strings.Contains(soapAction, "GetProtocolInfo"):
		writeSoapOK(w, "GetProtocolInfo", st,
			`<Source>http-get:*:video/*:*
http-get:*:audio/*:*
http-get:*:application/vnd.apple.mpegurl:*
http-get:*:application/x-mpegurl:*
http-get:*:application/dash+xml:*
rtsp-rtp-udp:*:video/*:*
rtsp-rtp-udp:*:audio/*:*
</Source><Sink>http-get:*:video/*:*
http-get:*:audio/*:*
http-get:*:application/vnd.apple.mpegurl:*
http-get:*:application/x-mpegurl:*
http-get:*:application/dash+xml:*
rtsp-rtp-udp:*:video/*:*
rtsp-rtp-udp:*:audio/*:*
</Sink>`)
	case strings.Contains(soapAction, "GetCurrentConnectionIDs"):
		writeSoapOK(w, "GetCurrentConnectionIDs", st, `<ConnectionIDs>0</ConnectionIDs>`)
	case strings.Contains(soapAction, "GetCurrentConnectionInfo"):
		writeSoapOK(w, "GetCurrentConnectionInfo", st,
			`<RcsID>0</RcsID><AVTransportID>0</AVTransportID>
<ProtocolInfo>http-get:*:video/mpegts:*</ProtocolInfo>
<PeerConnectionManager>/</PeerConnectionManager>
<PeerConnectionID>-1</PeerConnectionID>
<Direction>Input</Direction><Status>OK</Status>`)
	case strings.Contains(soapAction, "PrepareForConnection"):
		writeSoapOK(w, "PrepareForConnection", st,
			`<ConnectionID>0</ConnectionID><AVTransportID>0</AVTransportID><RcsID>0</RcsID>`)
	case strings.Contains(soapAction, "ConnectionComplete"):
		writeSoapOK(w, "ConnectionComplete", st, "")
	default:
		d.log.Warn("DLNA", "未处理 ConnectionManager SOAP: %s", soapAction)
		writeSoapError(w, 401, "Invalid Action")
	}
}

// ============ RenderingControl 最简响应（音量控制占位）============
func (d *DMR) handleRenderingControl(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(io.LimitReader(r.Body, 128*1024))
	r.Body.Close()
	soapAction := r.Header.Get("SOAPACTION")
	_ = body
	st := "urn:schemas-upnp-org:service:RenderingControl:1"
	switch {
	case strings.Contains(soapAction, "GetVolume"):
		writeSoapOK(w, "GetVolume", st, `<CurrentVolume>80</CurrentVolume>`)
	case strings.Contains(soapAction, "GetMute"):
		writeSoapOK(w, "GetMute", st, `<CurrentMute>0</CurrentMute>`)
	case strings.Contains(soapAction, "SetVolume"):
		writeSoapOK(w, "SetVolume", st, "")
	case strings.Contains(soapAction, "SetMute"):
		writeSoapOK(w, "SetMute", st, "")
	case strings.Contains(soapAction, "ListPresets"):
		writeSoapOK(w, "ListPresets", st, `<CurrentPresetNameList>FactoryDefaults</CurrentPresetNameList>`)
	case strings.Contains(soapAction, "SelectPreset"):
		writeSoapOK(w, "SelectPreset", st, "")
	default:
		d.log.Warn("DLNA", "未处理 RenderingControl SOAP: %s", soapAction)
		writeSoapError(w, 401, "Invalid Action")
	}
}

// extractTitleFromDIDL 解析DIDL-Lite里的dc:title
func extractTitleFromDIDL(meta string) string {
	if meta == "" { return "" }
	if i := strings.Index(meta, "<dc:title>"); i >= 0 {
		j := strings.Index(meta[i:], "</dc:title>")
		if j > 0 { return meta[i+9 : i+j] }
	}
	if i := strings.Index(meta, "<title>"); i >= 0 {
		j := strings.Index(meta[i:], "</title>")
		if j > 0 { return meta[i+7 : i+j] }
	}
	return ""
}
