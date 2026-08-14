pluginManagement {
    repositories {
        // 国内镜像加速
        maven { url = uri("https://maven.aliyun.com/repository/google") }
        maven { url = uri("https://maven.aliyun.com/repository/central") }
        maven { url = uri("https://maven.aliyun.com/repository/gradle-plugin") }
        google()
        mavenCentral()
        gradlePluginPortal()
        // RootEncoder/rtplibrary 依赖从 JitPack 拉取
        maven { url = uri("https://jitpack.io") }
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        // 国内镜像加速
        maven { url = uri("https://maven.aliyun.com/repository/google") }
        maven { url = uri("https://maven.aliyun.com/repository/central") }
        google()
        mavenCentral()
        // 推流库 com.github.pedroSG94.rtmp-rtsp-stream-client-java
        maven { url = uri("https://jitpack.io") }
    }
}

rootProject.name = "ScreenPush"
include(":app")
