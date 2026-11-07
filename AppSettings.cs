using System.IO;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>Remembers the last-used options in %APPDATA%\ProfileRefresher\settings.json.</summary>
public sealed class AppSettings
{
    public int BrowserIndex { get; set; }
    public int TaskIndex { get; set; }
    public string OrderMode { get; set; } = "taskbar"; // "taskbar", "opened" or "name"

    // Shared
    public double DelaySeconds { get; set; } = 10;
    public double JitterSeconds { get; set; } = 3;
    public int Loops { get; set; } = 3;

    // Combined task: this many refresh passes, then one I'm hosting pass
    public int RefreshesBeforeHosting { get; set; } = 5;

    // Refresh task
    public bool UseTitleFilter { get; set; }
    public string TitleFilter { get; set; } = "";
    public bool AllowKeyFallback { get; set; }

    // "Post I'm hosting" task
    public string TargetStatus { get; set; } = "I'm hosting";
    public string StatusRowLabel { get; set; } = "Hosting Status";
    public string SiteTabMatch { get; set; } = "";
    public string Hotkey { get; set; } = "Alt+Shift+H";
    public double StepTimeoutSeconds { get; set; } = 10;

    // "Chat & reply" task
    public List<string> ReplyMessages { get; set; } = new();
    public bool ReplyDryRun { get; set; } = true;
    public double HostingEveryMinutes { get; set; } = 20;
    public double SendDelaySeconds { get; set; } = 2;
    public double ChatDelaySeconds { get; set; } = 0.5;
    public double RefreshDelaySeconds { get; set; } = 5;

    // Phone notifications (Telegram)
    public bool TelegramEnabled { get; set; }
    /// <summary>Bot token, encrypted for the current Windows user.</summary>
    public string TelegramBotTokenProtected { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public bool TelegramIncludeText { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProfileRefresher", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
