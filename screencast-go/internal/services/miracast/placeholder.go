//go:build windows
// +build windows

package miracast

import (
	"fmt"
	"sync"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
)

// Placeholder Miracast占位实现（后续可用 WinRT Casting API 或 MSS）
type Placeholder struct {
	mu      sync.Mutex
	running bool
	port    int
	Log     *logger.RingLogger
	Kind    models.ServiceKind
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
	p.Log.Warn("Miracast", "Miracast 占位提示：Windows 10 内置「投影到此电脑」其实就是 Miracast Sink，\n可在【设置→系统→投影到此电脑】启用，本APP已预留接口后续联动。")
	p.mu.Lock()
	p.running = true
	p.port = 7236
	p.mu.Unlock()
	return fmt.Errorf("Miracast 暂未实现（占位服务），请先使用系统内置「投影到此电脑」")
}
func (p *Placeholder) Stop() error {
	p.mu.Lock(); defer p.mu.Unlock()
	p.running = false
	p.port = 0
	return nil
}
