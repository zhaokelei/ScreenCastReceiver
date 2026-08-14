using System.IO;
using System.Text.Json;

namespace ScreenCastReceiver.Helpers;

public static class PortConfig
{
    public static int DlnaHttp { get; private set; } = 49152;
    public static int DlnaSsdp { get; private set; } = 1900;

    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "ports.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<PortConfigData>(json);
                if (cfg != null)
                {
                    DlnaHttp = cfg.DlnaHttp;
                    DlnaSsdp = cfg.DlnaSsdp;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            var cfg = new PortConfigData
            {
                DlnaHttp = DlnaHttp,
                DlnaSsdp = DlnaSsdp
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private class PortConfigData
    {
        public int DlnaHttp { get; set; } = 49152;
        public int DlnaSsdp { get; set; } = 1900;
    }
}
