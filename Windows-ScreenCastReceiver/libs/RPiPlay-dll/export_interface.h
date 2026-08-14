/*
 * RPiPlay.dll 导出接口契约（C 头文件）
 * =====================================
 * 与 C# 侧 Native/RPiPlayNative.cs 的 [DllImport] 一一对应。
 * 编译 RPiPlay (FD-/RPiPlay) Windows x64 DLL 时实现以下符号即可。
 *
 * 生命周期约定：
 *   - rpiplay_stop() 必须【阻塞】直到 DLL 内部所有线程（raop/rtsp/mdns）退出，
 *     并释放监听端口后返回；这是 C# 侧防止端口残留占用的前提。
 */

#ifndef RPIPLAY_EXPORT_H
#define RPIPLAY_EXPORT_H

#ifdef __cplusplus
extern "C" {
#endif

/* 回调：视频 H.264 Annex-B 字节流（pts 单位秒；isKeyFrame=1 表示 IDR 帧） */
typedef void (*on_video_sample_t)(const uint8_t *data, int length,
                                  double pts_seconds, int is_keyframe);
/* 回调：音频 AAC/ALAC 原始帧 */
typedef void (*on_audio_sample_t)(const uint8_t *data, int length,
                                  double pts_seconds, int sample_rate,
                                  int channels);
/* 回调：镜像状态 0=结束 1=开始 */
typedef void (*on_mirror_state_t)(int mirroring);
/* 回调：播放控制 0=stop 1=play 2=pause（来自 iOS 控制中心/锁屏） */
typedef void (*on_playback_state_t)(int state);
/* 回调：音量 0.0~1.0 */
typedef void (*on_set_volume_t)(float volume);
/* 回调：日志文本（UTF-8） */
typedef void (*on_text_t)(const char *utf8);

typedef struct RPiPlayCallbacks
{
    on_video_sample_t   OnVideoSample;
    on_audio_sample_t   OnAudioSample;
    on_mirror_state_t   OnMirrorState;
    on_playback_state_t OnPlaybackState;
    on_set_volume_t     OnSetVolume;
    on_text_t           OnText;
    void               *UserData;
} RPiPlayCallbacks;

/* 初始化并注册回调；成功返回 0 */
__declspec(dllexport) int rpiplay_init(const RPiPlayCallbacks *callbacks);

/* 设置设备名称（AirPlay 显示名，UTF-8） */
__declspec(dllexport) int rpiplay_set_name(const char *name);

/* 启动 AirPlay 服务，监听 TCP listenPort；成功返回 0 */
__declspec(dllexport) int rpiplay_start(int listenPort);

/* 停止服务：阻塞等待内部线程全部退出并释放端口后返回；成功返回 0 */
__declspec(dllexport) int rpiplay_stop(void);

/* 释放全部资源（进程退出前调用） */
__declspec(dllexport) int rpiplay_free(void);

#ifdef __cplusplus
}
#endif

#endif /* RPIPLAY_EXPORT_H */
