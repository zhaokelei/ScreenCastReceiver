//go:build windows
// +build windows

package logger

import (
	"fmt"
	"path/filepath"
	"runtime"
	"sync"
	"time"
)

// 日志级别
const (
	LevelDebug = iota
	LevelInfo
	LevelWarn
	LevelError
	LevelFatal
)

// Entry 单条日志
type Entry struct {
	Time   time.Time
	Level  int
	Module string
	Msg    string
}

var (
	levelText = map[int]string{
		LevelDebug: "Debug",
		LevelInfo:  "Info",
		LevelWarn:  "Warn",
		LevelError: "Error",
		LevelFatal: "Fatal",
	}
)

// RingLogger 带容量上限的环形日志（UI直接读，不锁）
type RingLogger struct {
	mu     sync.RWMutex
	buf    []Entry
	head   int // 下一个写入位置
	count  int
	cap    int
	subs   []func(Entry) // 新日志订阅（UI更新用）
}

// New 创建环形日志（默认容量4096条，够显示万行级别）
func New(cap int) *RingLogger {
	if cap <= 0 { cap = 4096 }
	return &RingLogger{buf: make([]Entry, cap), cap: cap}
}

// Subscribe 订阅新日志（外部GUI用），必须在主循环前调用
func (r *RingLogger) Subscribe(fn func(Entry)) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.subs = append(r.subs, fn)
}

// 内部写入（级别+模块+消息）
func (r *RingLogger) write(level int, module, format string, args ...interface{}) {
	msg := format
	if len(args) > 0 { msg = fmt.Sprintf(format, args...) }
	e := Entry{
		Time:   time.Now(),
		Level:  level,
		Module: module,
		Msg:    msg,
	}
	// 调用方信息（Debug+Fatal才加）
	if level == LevelDebug || level == LevelFatal {
		_, file, line, ok := runtime.Caller(2)
		if ok { e.Msg = fmt.Sprintf("%s (@%s:%d)", e.Msg, filepath.Base(file), line) }
	}

	r.mu.Lock()
	r.buf[r.head] = e
	r.head = (r.head + 1) % r.cap
	if r.count < r.cap { r.count++ }
	subs := make([]func(Entry), len(r.subs))
	copy(subs, r.subs)
	r.mu.Unlock()

	// 通知订阅者（解锁后）
	for _, fn := range subs { fn(e) }
}

// ===== 对外便捷方法 =====
func (r *RingLogger) Debug(module, format string, args ...interface{}) {
	r.write(LevelDebug, module, format, args...)
}
func (r *RingLogger) Info(module, format string, args ...interface{}) {
	r.write(LevelInfo, module, format, args...)
}
func (r *RingLogger) Warn(module, format string, args ...interface{}) {
	r.write(LevelWarn, module, format, args...)
}
func (r *RingLogger) Error(module, format string, args ...interface{}) {
	r.write(LevelError, module, format, args...)
}
func (r *RingLogger) Fatal(module, format string, args ...interface{}) {
	r.write(LevelFatal, module, format, args...)
}

// Latest 读取最近N条（UI初始化用）
func (r *RingLogger) Latest(n int) []Entry {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if n <= 0 { n = 200 }
	if n > r.count { n = r.count }
	out := make([]Entry, n)
	for i := 0; i < n; i++ {
		idx := (r.head - r.count + i) % r.cap
		if idx < 0 { idx += r.cap }
		out[i] = r.buf[idx]
	}
	return out
}

// FormatLine 把Entry格式化为UI显示字符串（带时间戳+级别颜色前导）
func FormatLine(e Entry) string {
	t := e.Time.Format("15:04:05.000")
	l := levelText[e.Level]
	if e.Level == LevelError || e.Level == LevelFatal {
		return fmt.Sprintf("[%s] [%s] [%s] ❌ %s", t, e.Module, l, e.Msg)
	}
	if e.Level == LevelWarn {
		return fmt.Sprintf("[%s] [%s] [%s] ⚠ %s", t, e.Module, l, e.Msg)
	}
	return fmt.Sprintf("[%s] [%s] [%s] %s", t, e.Module, l, e.Msg)
}

// Default 全局默认日志实例（单例）
var Default = New(4096)
