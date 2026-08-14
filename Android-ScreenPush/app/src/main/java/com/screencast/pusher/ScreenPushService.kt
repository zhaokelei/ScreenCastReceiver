package com.screencast.pusher

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.pedro.rtplibrary.rtsp.RtspDisplay
import com.pedro.rtsp.rtsp.Protocol
import com.pedro.rtsp.utils.ConnectCheckerRtsp

/**
 * 屏幕推流前台服务。
 *
 * 使用成熟开源库 pedroSG94/RootEncoder（rtplibrary 2.2.6）的 [RtspDisplay] 完成
 * RTSP 推流：内置 MediaProjection 录屏 + H.264/AAC 编码 + RTP 传输，支持 TCP/UDP。
 *
 * 由 [MainActivity] 通过 [ACTION_START] 启动：
 * 1. 携带 MediaProjection 授权结果（resultCode/data）与推流配置
 * 2. 本服务创建 RtspDisplay -> setProtocol -> setIntentResult -> prepareVideo/Audio -> startStream
 *
 * 状态通过 [ACTION_STATUS] 广播回传给界面。
 */
class ScreenPushService : Service() {

    companion object {
        private const val TAG = "ScreenPushService"

        const val ACTION_START = "com.screencast.pusher.action.START"
        const val ACTION_STOP = "com.screencast.pusher.action.STOP"
        const val ACTION_STATUS = "com.screencast.pusher.action.STATUS"
        const val EXTRA_STATUS = "status"
        const val EXTRA_MESSAGE = "message"

        const val EXTRA_URL = "url"
        const val EXTRA_WIDTH = "width"
        const val EXTRA_HEIGHT = "height"
        const val EXTRA_FPS = "fps"
        const val EXTRA_BITRATE = "bitrate"
        const val EXTRA_USE_TCP = "use_tcp"
        const val EXTRA_USE_INTERNAL_AUDIO = "use_internal_audio"
        const val EXTRA_RESULT_CODE = "result_code"
        const val EXTRA_RESULT_DATA = "result_data"

        private const val CHANNEL_ID = "screen_push"
        private const val NOTIFY_ID = 1001

        // 状态常量
        const val STATUS_CONNECTING = "connecting"
        const val STATUS_CONNECTED = "connected"
        const val STATUS_FAILED = "failed"
        const val STATUS_DISCONNECTED = "disconnected"
        const val STATUS_STOPPED = "stopped"

        private const val DEFAULT_SAMPLE_RATE = 44100
        private const val AUDIO_BITRATE = 128_000

        /** 是否为前台服务所需的媒体投影类型权限（Android 14+ 必须） */
        private fun foregroundServiceType(): Int =
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
    }

    private var display: RtspDisplay? = null
    private var streaming = false

    private val connectChecker = object : ConnectCheckerRtsp {
        override fun onConnectionStartedRtsp(rtspUrl: String) {
            Log.i(TAG, "RTSP 连接开始: $rtspUrl")
            updateNotification("正在连接 $rtspUrl ...")
            broadcastStatus(STATUS_CONNECTING, "正在连接 $rtspUrl ...")
        }

        override fun onConnectionSuccessRtsp() {
            Log.i(TAG, "RTSP 连接成功")
            streaming = true
            updateNotification("推流中")
            broadcastStatus(STATUS_CONNECTED, "推流中")
        }

        override fun onConnectionFailedRtsp(reason: String) {
            Log.e(TAG, "RTSP 连接失败: $reason")
            updateNotification("连接失败")
            broadcastStatus(STATUS_FAILED, "连接失败: $reason")
        }

        override fun onNewBitrateRtsp(bitrate: Long) {
            Log.d(TAG, "当前码率: ${bitrate / 1000} kbps")
        }

        override fun onDisconnectRtsp() {
            Log.w(TAG, "RTSP 已断开")
            streaming = false
            updateNotification("已断开")
            broadcastStatus(STATUS_DISCONNECTED, "连接已断开")
        }

        override fun onAuthErrorRtsp() {
            Log.e(TAG, "RTSP 认证失败")
            updateNotification("认证失败")
            broadcastStatus(STATUS_FAILED, "RTSP 认证失败，请检查用户名/密码")
        }

        override fun onAuthSuccessRtsp() {
            Log.i(TAG, "RTSP 认证成功")
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> handleStart(intent)
            ACTION_STOP -> handleStop()
        }
        return START_NOT_STICKY
    }

    private fun handleStart(intent: Intent) {
        if (streaming) {
            broadcastStatus(STATUS_CONNECTING, "已经在推流中")
            return
        }
        startAsForeground()

        val url = intent.getStringExtra(EXTRA_URL) ?: return
        val width = intent.getIntExtra(EXTRA_WIDTH, 1280)
        val height = intent.getIntExtra(EXTRA_HEIGHT, 720)
        val fps = intent.getIntExtra(EXTRA_FPS, 30)
        val bitrate = intent.getIntExtra(EXTRA_BITRATE, 4_000_000)
        val useTcp = intent.getBooleanExtra(EXTRA_USE_TCP, true)
        val useInternalAudio = intent.getBooleanExtra(EXTRA_USE_INTERNAL_AUDIO, false)
        val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
        val resultData: Intent? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(EXTRA_RESULT_DATA)
        }

        if (resultData == null) {
            broadcastStatus(STATUS_FAILED, "缺少录屏授权，请重新开始")
            stopSelf()
            return
        }

        try {
            val rtspDisplay = RtspDisplay(applicationContext, true, connectChecker)
            // TCP/UDP 传输协议选择
            rtspDisplay.setProtocol(if (useTcp) Protocol.TCP else Protocol.UDP)
            // 写入 MediaProjection 授权结果，库内部负责创建投影与虚拟显示
            rtspDisplay.setIntentResult(resultCode, resultData)

            // 视频：H.264 + AAC（编码器由库根据宽度高度自动选择 H264/H265，2.2.6 默认 H264）
            val dpi = resources.displayMetrics.densityDpi
            if (!rtspDisplay.prepareVideo(width, height, fps, bitrate, 0, dpi)) {
                broadcastStatus(STATUS_FAILED, "视频编码器初始化失败")
                rtspDisplay.stopStream()
                stopSelf()
                return
            }

            // 音频：Android 10+ 优先系统内录，否则回退麦克风
            val audioOk = if (useInternalAudio && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                rtspDisplay.prepareInternalAudio(AUDIO_BITRATE, DEFAULT_SAMPLE_RATE, true, false, false)
            } else {
                rtspDisplay.prepareAudio(AUDIO_BITRATE, DEFAULT_SAMPLE_RATE, true, false, false)
            }
            if (!audioOk) {
                Log.w(TAG, "音频初始化失败，仅推送视频")
            }

            display = rtspDisplay
            rtspDisplay.startStream(url)
        } catch (e: Exception) {
            Log.e(TAG, "启动推流异常", e)
            broadcastStatus(STATUS_FAILED, "启动推流异常: ${e.message}")
            stopSelf()
        }
    }

    private fun handleStop() {
        Log.i(TAG, "收到停止指令")
        stopStreaming()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun stopStreaming() {
        try {
            display?.stopStream()
        } catch (e: Exception) {
            Log.e(TAG, "停止推流异常", e)
        }
        display = null
        streaming = false
        broadcastStatus(STATUS_STOPPED, "已停止推流")
    }

    override fun onDestroy() {
        Log.i(TAG, "服务销毁，释放推流资源")
        stopStreaming()
        super.onDestroy()
    }

    // ---------- 通知 ----------

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notify_channel_name),
            NotificationManager.IMPORTANCE_LOW
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun buildNotification(text: String): Notification =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_notification)
            .setOngoing(true)
            .setContentIntent(null)
            .build()

    private fun startAsForeground() {
        val notification = buildNotification(getString(R.string.notify_connecting))
        ServiceCompat.startForeground(
            this,
            NOTIFY_ID,
            notification,
            foregroundServiceType()
        )
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFY_ID, buildNotification(text))
    }

    // ---------- 状态广播 ----------

    private fun broadcastStatus(status: String, message: String) {
        val intent = Intent(ACTION_STATUS).apply {
            setPackage(packageName)
            putExtra(EXTRA_STATUS, status)
            putExtra(EXTRA_MESSAGE, message)
        }
        sendBroadcast(intent)
    }
}
