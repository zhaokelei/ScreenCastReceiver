/*
 * Miracast.dll 导出接口契约（C 头文件）
 * =====================================
 * 与 C# 侧 Native/MiracastNative.cs 的 [DllImport] 一一对应。
 * 基于开源 Miracast-Windows 接收端源码编译 x64 DLL 时实现以下符号即可。
 *
 * 生命周期约定：
 *   - miracast_stop() 必须【阻塞】直到 DLL 内部所有线程退出，
 *     并释放 UDP RTP 接收端口后返回；这是 C# 侧防止端口残留占用的前提。
 */

#ifndef MIRACAST_EXPORT_H
#define MIRACAST_EXPORT_H

#ifdef __cplusplus
extern "C" {
#endif

/* 回调：MPEG-TS 封装流（H264+AAC），C# 侧原样转发给 MPV 渲染 */
typedef void (*on_ts_packet_t)(const uint8_t *data, int length);
/* 回调：连接状态 0=断开 1=已连接 2=媒体流启动 */
typedef void (*on_state_t)(int state);
/* 回调：日志文本（UTF-8） */
typedef void (*on_text_t)(const char *utf8);

typedef struct MiracastCallbacks
{
    on_ts_packet_t OnTsPacket;
    on_state_t     OnState;
    on_text_t      OnText;
    void          *UserData;
} MiracastCallbacks;

/* 初始化并注册回调；成功返回 0 */
__declspec(dllexport) int miracast_init(const MiracastCallbacks *callbacks);

/* 启动接收，监听 UDP udpRtpPort（Wi-Fi Display 规范默认 7250）；成功返回 0 */
__declspec(dllexport) int miracast_start(int udpRtpPort);

/* 停止服务：阻塞等待内部线程全部退出并释放端口后返回；成功返回 0 */
__declspec(dllexport) int miracast_stop(void);

/* 释放全部资源（进程退出前调用） */
__declspec(dllexport) int miracast_free(void);

#ifdef __cplusplus
}
#endif

#endif /* MIRACAST_EXPORT_H */
