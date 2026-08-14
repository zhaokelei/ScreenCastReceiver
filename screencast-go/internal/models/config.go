//go:build windows
// +build windows

package models

import "time"

// ServiceKind 服务类型（用string方便序列化）
type ServiceKind string

const (
	SvcDlna     ServiceKind = "DLNA"
	SvcAirPlay  ServiceKind = "AirPlay"
	SvcMiracast ServiceKind = "Miracast"
	SvcRtsp     ServiceKind = "RTSP"
)

// ServiceStatus 服务启停状态
type ServiceStatus int

const (
	StatusStopped  ServiceStatus = iota
	StatusStarting
	StatusRunning
	StatusStopping
	StatusFailed
)

func (s ServiceStatus) String() string {
	switch s {
	case StatusStopped:  return "未启动"
	case StatusStarting: return "启动中"
	case StatusRunning:  return "运行中"
	case StatusStopping: return "停止中"
	case StatusFailed:   return "启动失败"
	}
	return "未知"
}

// ServiceStateChangedArgs 状态变更事件
type ServiceStateChangedArgs struct {
	Kind     ServiceKind
	Status   ServiceStatus
	Detail   string
	Port     int
}

// ServiceConfig 单个服务的可持久化配置
type ServiceConfig struct {
	Enabled       bool   `json:"enabled"`
	AutoStart     bool   `json:"auto_start"`
	Port          int    `json:"port"`       // 0=自动
	DeviceName    string `json:"device_name"`// 显示名（SSDP通告）
}

// NICInfo 网卡信息（物理/虚拟区分）
type NICInfo struct {
	Index       int      `json:"index"`
	Name        string   `json:"name"`
	Description string   `json:"description"`
	IsPhysical  bool     `json:"is_physical"` // 物理网卡 vs 虚拟网卡(WSL/VPN/VMware)
	MAC         string   `json:"mac"`
	IPv4List    []string `json:"ipv4"`       // 一张网卡可多IP（多子网/别名）
	IPv6List    []string `json:"ipv6"`
	MTU         int      `json:"mtu"`
	OperUp      bool     `json:"oper_up"`
}

// AppConfig 全局配置（JSON持久化到程序目录 config.json）
type AppConfig struct {
	// 4个服务
	DLNA     ServiceConfig `json:"dlna"`
	AirPlay  ServiceConfig `json:"airplay"`
	Miracast ServiceConfig `json:"miracast"`
	RTSP     ServiceConfig `json:"rtsp"`

	// 网卡绑定
	BindNICIndex   []int    `json:"bind_nic_index"`   // 勾选的网卡Index，空=全部物理网卡
	BindOnlyIPv4   bool     `json:"bind_only_ipv4"`   // 只绑定IPv4（默认true）
	BindCustomIPs  []string `json:"bind_custom_ips"`  // 手动补充绑定IP（可选）

	// 播放
	MPVPath        string   `json:"mpv_path"`         // 空 = 程序目录/mpv/mpv.exe
	MPVPriority    string   `json:"mpv_priority"`     // 抢占策略
	AutoFullscreen bool     `json:"auto_fullscreen"`  // 投屏自动全屏
	DefaultAspect  string   `json:"default_aspect"`   // 默认画面比例
	DefaultSpeed   float64  `json:"default_speed"`    // 默认播放速率

	// UI
	WindowX        int      `json:"window_x"`
	WindowY        int      `json:"window_y"`
	WindowW        int      `json:"window_w"`
	WindowH        int      `json:"window_h"`
}

// DefaultAppConfig 出厂默认值
func DefaultAppConfig() *AppConfig {
	return &AppConfig{
		DLNA:     ServiceConfig{Enabled: false, Port: 0,   DeviceName: "我的影院-客厅"},
		AirPlay:  ServiceConfig{Enabled: false, Port: 7000,DeviceName: "我的影院-客厅"},
		Miracast: ServiceConfig{Enabled: false, Port: 7236,DeviceName: "我的影院-客厅"},
		RTSP:     ServiceConfig{Enabled: false, Port: 8554,DeviceName: "ScreenCastReceiver-RTSP"},
		BindOnlyIPv4:   true,
		MPVPriority:    "confirm", // confirm=抢占前询问用户
		AutoFullscreen: false,
		DefaultAspect:  "auto",
		DefaultSpeed:   1.0,
		WindowW:        1280,
		WindowH:        820,
	}
}

// ActiveSession 当前投屏会话（统一结构，DLNA/AirPlay都映射进来）
type ActiveSession struct {
	ID         string      `json:"id"`
	Kind       ServiceKind `json:"kind"`
	SourceName string      `json:"source_name"`  // 来源设备名：安卓xxx-手机
	SourceIP   string      `json:"source_ip"`
	MediaURI   string      `json:"media_uri"`    // 视频URL
	Title      string      `json:"title"`        // 视频标题（可选）
	StartAt    time.Time   `json:"start_at"`
	// 播放状态
	DurationMs int64   `json:"duration_ms"`
	PositionMs int64   `json:"position_ms"`
	Speed      float64 `json:"speed"`
	Playing    bool    `json:"playing"`
}

// Stats 全局统计（FPS/网络速率/内存等）
type Stats struct {
	MPVFPS       float64
	MPVBitrateK  float64 // kbps
	NetRxKBs     float64 // KB/s
	NetTxKBs     float64
	WorkingSetMB float64
}
