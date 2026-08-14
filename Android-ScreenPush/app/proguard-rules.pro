# 本项目 release 默认不混淆（isMinifyEnabled=false）。
# 若后续开启混淆，保留 RTSP 推流库的反射/回调类：
-keep class com.pedro.** { *; }
-keep class com.pedro.rtsp.** { *; }
-keep class com.pedro.encoder.** { *; }
-keep class com.pedro.common.** { *; }
