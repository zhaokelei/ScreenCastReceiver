//go:build windows
// +build windows

package airplay

import (
	"fmt"
	"screencast-go/internal/logger"
	"screencast-go/internal/models"
	"sync"
)

// Placeholder AirPlay2占位实现（后续可用 github.com/openairplay 或 RAOP 协议补齐）
type Placeholder struct {
	mu     sync.Mutex
	running bool
	port   int
	Log    *logger.RingLogger
	Kind   models.ServiceKind
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
	p.Log.Warn("AirPlay", "AirPlay2 占位提示：下一版本将基于 RAOP/AP2 协议实现 iPhone 投屏。\n当前已保留UI与接口，勾选后仅做状态标记。")
	p.mu.Lock()
	p.running = true
	p.port = 7000
	p.mu.Unlock()
	return fmt.Errorf("AirPlay2 暂未实现（占位服务），请使用 DLNA 或 RTSP 通道投屏")
}

func (p *Placeholder) Stop() error {
	p.mu.Lock()
	p.running = false
	p.port = 0
	p.mu.Unlock()
	return nil
}
