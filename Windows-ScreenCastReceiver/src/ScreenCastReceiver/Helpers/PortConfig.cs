using System.IO;
using System.Text.Json;

namespace ScreenCastReceiver.Helpers;

public static class PortConfig
{
    public static int AirPlayTcp { get; private set; } = 5000;
    public static int MiracastUdp { get; private set; } = 7250;
    public static int RtspTcp { get; private set; } = 8554;
    public static int WebRtcTcp { get; private set; } = 8555;
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
                    AirPlayTcp = cfg.AirPlayTcp;
                    MiracastUdp = cfg.MiracastUdp;
                    RtspTcp = cfg.RtspTcp;
                    WebRtcTcp = cfg.WebRtcTcp;
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
                AirPlayTcp = AirPlayTcp,
                MiracastUdp = MiracastUdp,
                RtspTcp = RtspTcp,
                WebRtcTcp = WebRtcTcp,
                DlnaHttp = DlnaHttp,
                DlnaSsdp = DlnaSsdp
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private class PortConfigData
    {
        public int AirPlayTcp { get; set; } = 5000;
        public int MiracastUdp { get; set; } = 7250;
        public int RtspTcp { get; set; } = 8554;
        public int WebRtcTcp { get; set; } = 8555;
        public int DlnaHttp { get; set; } = 49152;
        public int DlnaSsdp { get; set; } = 1900;
    }
}
