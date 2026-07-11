using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Ipi.Desktop.Services;

public sealed class AppearanceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public AppearanceSettingsService()
    {
        var dir = IpiPathService.AppDataDir;
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "appearance.json");
    }

    public AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return AppearanceSettings.Default;
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppearanceSettings>(json, JsonOptions);
            using var document = JsonDocument.Parse(json);
            if (settings is not null && !document.RootElement.TryGetProperty(nameof(AppearanceSettings.NotificationSoundsEnabled), out _))
            {
                settings = settings with { NotificationSoundsEnabled = true };
            }
            return settings?.Normalize() ?? AppearanceSettings.Default;
        }
        catch
        {
            return AppearanceSettings.Default;
        }
    }

    public void Save(AppearanceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        WriteTextAtomically(_settingsPath, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path has no parent directory: {path}");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}

public sealed record AppearanceSettings(
    string Language,
    string Mode,
    string Theme,
    double WindowTransparency,
    bool NotificationSoundsEnabled = true)
{
    public static AppearanceSettings Default => new("zh-CN", "light", "ipi", 0, true);

    public AppearanceSettings Normalize()
    {
        var language = string.IsNullOrWhiteSpace(Language) ? "zh-CN" : Language;
        var mode = Mode is "light" or "dark" or "system" ? Mode : "light";
        var theme = string.IsNullOrWhiteSpace(Theme) ? "ipi" : Theme;
        if (theme.Equals("nous", StringComparison.OrdinalIgnoreCase)) theme = "ipi";
        if (theme.Equals("next", StringComparison.OrdinalIgnoreCase)) theme = "broadsheet";
        if (theme.Equals("mews", StringComparison.OrdinalIgnoreCase)) theme = "candy-block";
        theme = theme is "ipi" or "broadsheet" or "candy-block" ? theme : "ipi";
        var transparency = Math.Clamp(WindowTransparency, 0, 70);
        return this with { Language = language, Mode = mode, Theme = theme, WindowTransparency = transparency };
    }

    public string EffectiveMode()
    {
        var normalized = Normalize();
        if (normalized.Mode != "system") return normalized.Mode;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int appsUseLightTheme && appsUseLightTheme == 0 ? "dark" : "light";
        }
        catch
        {
            return "light";
        }
    }
}
