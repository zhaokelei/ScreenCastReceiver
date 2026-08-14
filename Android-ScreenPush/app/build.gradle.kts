plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.screencast.pusher"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.screencast.pusher"
        minSdk = 26          // Android 8.0，兼容绝大多数设备；系统内部音频采集需 Android 10+，低版本自动降级麦克风
        targetSdk = 34
        versionCode = 1
        versionName = "1.0.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.activity:activity-ktx:1.9.0")

    // 成熟开源 RTSP 推流库（pedroSG94/RootEncoder，原 rtmp-rtsp-stream-client-java，2.2.x 最后稳定版）
    // 屏幕推流使用其 RtspDisplay 类（com.pedro.rtplibrary.rtsp.RtspDisplay），
    // 支持 H.264 + AAC 编码，RTSP TCP/UDP 传输，内置 MediaProjection 录屏授权流程
    implementation("com.github.pedroSG94.RootEncoder:rtplibrary:2.2.6")
}
