//go:build cgo

/*
 * go2rtc.dll 桥接源码（go build -buildmode=c-shared）
 * =====================================================
 * 用途：把 go2rtc 编译成 Windows x64 DLL，供 C# P/Invoke 调用。
 *
 * 编译（在 go2rtc 仓库中创建 cmd/bridge 目录，放入本文件）：
 *   CGO_ENABLED=1 GOOS=windows GOARCH=amd64 go build -buildmode=c-shared -o go2rtc.dll
 *
 * 说明：go2rtc 内部 API（core.Start 等）随版本可能微调，请按所克隆版本调整。
 * 要求：go2rtc_stop() 必须阻塞到内部所有协程/端口退出后再返回。
 */

package main

/*
#include <stdlib.h>
#include <stdint.h>
*/
import "C"

import (
	"context"
	"encoding/json"
	"unsafe"

	"github.com/AlexxIT/go2rtc/core" // 按 go2rtc 版本调整导入路径
)

var (
	ctx    context.Context
	cancel context.CancelFunc
	running bool
)

//export go2rtc_start
func go2rtc_start(configJson *C.char) C.int32 {
	if running {
		return -1 // 已启动
	}
	cfg := C.GoString(configJson)
	// 校验 JSON
	var raw map[string]interface{}
	if err := json.Unmarshal([]byte(cfg), &raw); err != nil {
		return -2
	}
	ctx, cancel = context.WithCancel(context.Background())
	if err := core.Start(ctx, cfg); err != nil {
		return -3
	}
	running = true
	return 0
}

//export go2rtc_stop
func go2rtc_stop() {
	if !running {
		return
	}
	cancel()                 // 触发 go2rtc 内部优雅退出
	running = false
	// core.Start 退出后所有监听端口已释放（阻塞等待其协程结束）
}

//export go2rtc_is_running
func go2rtc_is_running() C.int32 {
	if running {
		return 1
	}
	return 0
}

//export go2rtc_get_version
func go2rtc_get_version() *C.char {
	return C.CString("go2rtc-bridge-1.0")
}

// 保持 c-shared 模式需要 main 函数（空实现即可）
func main() {
	_ = unsafe.Sizeof(0)
}
