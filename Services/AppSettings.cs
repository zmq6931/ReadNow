using System.IO;
using System.Text.Json;

namespace ReadNow.Services;

/// <summary>
/// 用户设置：持久化到 %APPDATA%\ReadNow\settings.json。
/// 目前只有朗读倍速，后续可加默认语音、热键等。
/// </summary>
public static class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReadNow");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>朗读倍速（0.5 ~ 4.0）。</summary>
    public static double Speed { get; set; } = 1.0;

    /// <summary>用户选定的朗读声音 Id；空为自动（按语言优先女声）。</summary>
    public static string VoiceId { get; set; } = "";

    static AppSettings() => Load();

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;
            if (data.Speed is >= 0.5 and <= 4.0) Speed = data.Speed;
            VoiceId = data.VoiceId ?? "";
        }
        catch { /* 设置损坏时用默认值 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(new SettingsData { Speed = Speed, VoiceId = VoiceId });
            File.WriteAllText(FilePath, json);
        }
        catch { /* 保存失败不影响使用 */ }
    }

    private sealed class SettingsData
    {
        public double Speed { get; set; } = 1.0;
        public string? VoiceId { get; set; }
    }
}
