using System.IO;
using System.Text.Json;
using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public static class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BtcTradingBot");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool Exists() => File.Exists(ConfigPath);

    public static BotConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new BotConfig();
        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<BotConfig>(json, JsonOpts) ?? new BotConfig();
        config.TradeUsdt = Math.Round(config.TradeUsdt, 2);
        config.TestBalance = Math.Round(config.TestBalance, 2);
        return config;
    }

    public static void Save(BotConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    public static void Delete()
    {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
    }

    // ═══ 페이퍼 트레이딩 상태 저장 ═══

    private static readonly string PaperStatePath = Path.Combine(ConfigDir, "paper_state.json");

    public static PaperState? LoadPaperState()
    {
        if (!File.Exists(PaperStatePath)) return null;
        try
        {
            var json = File.ReadAllText(PaperStatePath);
            return JsonSerializer.Deserialize<PaperState>(json, JsonOpts);
        }
        catch { return null; }
    }

    public static void SavePaperState(PaperState state)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(state, JsonOpts);
        File.WriteAllText(PaperStatePath, json);
    }

    public static void DeletePaperState()
    {
        try { if (File.Exists(PaperStatePath)) File.Delete(PaperStatePath); }
        catch { /* ignore */ }
    }
}
