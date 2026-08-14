using System;
using System.IO;
using System.Text.Json;

namespace ScreenCastReceiver.Helpers;

/// <summary>
/// 用户设置持久化（settings.json，位于程序目录）。
/// - 自定义 DLNA 设备名
/// - 自定义 DLNA 端口（0 = 自动）
/// 修改后即时保存，下次启动自动读取。
/// </summary>
public static class AppSettings
{
    /// <summary>默认 DLNA 设备名。</summary>
    public const string DefaultDeviceName = "Xiaolei DLAN";

    /// <summary>DLNA 设备名。</summary>
    public static string DeviceName { get; set; } = DefaultDeviceName;

    /// <summary>DLNA 端口（0 = 自动分配）。</summary>
    public static int DlnaPort { get; set; }

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>启动时读取设置（文件不存在或解析失败时使用默认值）。</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;

            if (!string.IsNullOrWhiteSpace(data.DeviceName))
                DeviceName = data.DeviceName.Trim();
            if (data.DlnaPort >= 0 && data.DlnaPort <= 65535)
                DlnaPort = data.DlnaPort;
        }
        catch (Exception)
        {
            // 配置损坏时静默回退默认值，不影响程序启动
        }
    }

    /// <summary>保存设置到 settings.json。</summary>
    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new SettingsData { DeviceName = DeviceName, DlnaPort = DlnaPort },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception)
        {
            // 写配置失败不影响主流程（仅下次启动丢失记忆）
        }
    }

    private sealed class SettingsData
    {
        public string DeviceName { get; set; } = DefaultDeviceName;
        public int DlnaPort { get; set; }
    }
}
