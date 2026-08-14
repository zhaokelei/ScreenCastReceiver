package com.screencast.pusher

import android.Manifest
import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat

/**
 * 屏幕推流客户端主界面。
 *
 * 流程：
 * 1. 填写 RTSP 地址与推流参数
 * 2. 点击“开始推流”：动态申请权限（麦克风/通知）-> 发起 MediaProjection 录屏授权
 * 3. 授权成功后，将配置与授权结果通过前台服务 [ScreenPushService] 执行 RTSP 推流
 * 4. 通过广播实时显示推流状态
 */
class MainActivity : AppCompatActivity() {

    private lateinit var etUrl: EditText
    private lateinit var spResolution: Spinner
    private lateinit var etFps: EditText
    private lateinit var etBitrate: EditText
    private lateinit var rgProtocol: RadioGroup
    private lateinit var rgAudio: RadioGroup
    private lateinit var btnStart: Button
    private lateinit var btnStop: Button
    private lateinit var tvStatus: TextView

    private val resolutions = arrayOf(
        "480P - 640x480",
        "720P - 1280x720",
        "1080P - 1920x1080"
    )

    private val resolutionMap = mapOf(
        "480P - 640x480" to (640 to 480),
        "720P - 1280x720" to (1280 to 720),
        "1080P - 1920x1080" to (1920 to 1080)
    )

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
            if (grants.values.all { it }) {
                launchMediaProjection()
            } else {
                showToast(getString(R.string.permission_denied))
            }
        }

    private val mediaProjectionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == Activity.RESULT_OK && result.data != null) {
                startPushService(result.resultCode, result.data!!)
            } else {
                tvStatus.text = getString(R.string.status_projection_denied)
                showToast(getString(R.string.status_projection_denied))
            }
        }

    private val statusReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action != ScreenPushService.ACTION_STATUS) return
            val status = intent.getStringExtra(ScreenPushService.EXTRA_STATUS) ?: return
            val message = intent.getStringExtra(ScreenPushService.EXTRA_MESSAGE) ?: ""
            when (status) {
                ScreenPushService.STATUS_CONNECTED -> {
                    tvStatus.text = getString(R.string.status_streaming)
                    btnStart.isEnabled = false
                    btnStop.isEnabled = true
                }
                ScreenPushService.STATUS_CONNECTING -> {
                    tvStatus.text = message
                    btnStart.isEnabled = false
                    btnStop.isEnabled = true
                }
                ScreenPushService.STATUS_FAILED,
                ScreenPushService.STATUS_DISCONNECTED,
                ScreenPushService.STATUS_STOPPED -> {
                    tvStatus.text = message
                    btnStart.isEnabled = true
                    btnStop.isEnabled = false
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        bindViews()
        setupSpinner()
        btnStart.setOnClickListener { onStartClicked() }
        btnStop.setOnClickListener {
            stopService(Intent(this, ScreenPushService::class.java).setAction(ScreenPushService.ACTION_STOP))
        }
    }

    override fun onStart() {
        super.onStart()
        ContextCompat.registerReceiver(
            this,
            statusReceiver,
            IntentFilter(ScreenPushService.ACTION_STATUS),
            ContextCompat.RECEIVER_NOT_EXPORTED
        )
    }

    override fun onStop() {
        super.onStop()
        unregisterReceiver(statusReceiver)
    }

    private fun bindViews() {
        etUrl = findViewById(R.id.et_url)
        spResolution = findViewById(R.id.sp_resolution)
        etFps = findViewById(R.id.et_fps)
        etBitrate = findViewById(R.id.et_bitrate)
        rgProtocol = findViewById(R.id.rg_protocol)
        rgAudio = findViewById(R.id.rg_audio)
        btnStart = findViewById(R.id.btn_start)
        btnStop = findViewById(R.id.btn_stop)
        tvStatus = findViewById(R.id.tv_status)
    }

    private fun setupSpinner() {
        spResolution.adapter = ArrayAdapter(
            this,
            android.R.layout.simple_spinner_dropdown_item,
            resolutions
        )
    }

    // ---------- 开始推流 ----------

    private fun onStartClicked() {
        val url = etUrl.text.toString().trim()
        if (url.isEmpty()) {
            showToast(getString(R.string.error_url_empty))
            return
        }
        requestNeededPermissions()
    }

    private fun requestNeededPermissions() {
        val permissions = mutableListOf<String>()
        val useInternalAudio = rgAudio.checkedRadioButtonId == R.id.rb_internal_audio
        // 麦克风采集需要录音权限；系统内录（Android 10+）不需要
        if (!useInternalAudio || Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            permissions.add(Manifest.permission.RECORD_AUDIO)
        }
        // Android 13+ 需要通知权限以展示前台服务通知
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            permissions.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        if (permissions.isEmpty()) {
            launchMediaProjection()
            return
        }
        val missing = permissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) {
            launchMediaProjection()
        } else {
            permissionLauncher.launch(missing.toTypedArray())
        }
    }

    private fun launchMediaProjection() {
        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        mediaProjectionLauncher.launch(manager.createScreenCaptureIntent())
    }

    private fun startPushService(resultCode: Int, resultData: Intent) {
        val resolution = resolutionMap[spResolution.selectedItem.toString()] ?: (1280 to 720)
        val fps = etFps.text.toString().toIntOrNull() ?: 30
        val bitrateMb = etBitrate.text.toString().toDoubleOrNull() ?: 4.0
        val bitrate = (bitrateMb * 1_000_000).toInt().coerceIn(500_000, 20_000_000)
        val useTcp = rgProtocol.checkedRadioButtonId == R.id.rb_tcp
        val useInternalAudio = rgAudio.checkedRadioButtonId == R.id.rb_internal_audio

        val intent = Intent(this, ScreenPushService::class.java).apply {
            action = ScreenPushService.ACTION_START
            putExtra(ScreenPushService.EXTRA_URL, etUrl.text.toString().trim())
            putExtra(ScreenPushService.EXTRA_WIDTH, resolution.first)
            putExtra(ScreenPushService.EXTRA_HEIGHT, resolution.second)
            putExtra(ScreenPushService.EXTRA_FPS, fps)
            putExtra(ScreenPushService.EXTRA_BITRATE, bitrate)
            putExtra(ScreenPushService.EXTRA_USE_TCP, useTcp)
            putExtra(ScreenPushService.EXTRA_USE_INTERNAL_AUDIO, useInternalAudio)
            putExtra(ScreenPushService.EXTRA_RESULT_CODE, resultCode)
            putExtra(ScreenPushService.EXTRA_RESULT_DATA, resultData)
        }
        ContextCompat.startForegroundService(this, intent)
        tvStatus.text = getString(R.string.status_connecting)
    }

    private fun showToast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }
}
