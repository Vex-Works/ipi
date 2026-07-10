using System.IO;
using System.Text.Json;

namespace Ipi.Desktop.Services;

public static class IpiPathService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string PathSettingsFile => Path.Combine(AppContext.BaseDirectory, "ipi-paths.json");
    public static string DefaultAppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ipi");
    public static string DefaultLocalAppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ipi");

    public static string AppDataDir => ResolveDirectoryOverride("IPI_APPDATA_DIR", DefaultAppDataDir, LoadPathSettings().AppDataDir);
    public static string LocalAppDataDir => ResolveDirectoryOverride("IPI_LOCALAPPDATA_DIR", DefaultLocalAppDataDir, LoadPathSettings().LocalAppDataDir);

    public static IpiPathSettings LoadPathSettings()
    {
        try
        {
            if (!File.Exists(PathSettingsFile)) return IpiPathSettings.Default;
            var settings = JsonSerializer.Deserialize<IpiPathSettings>(File.ReadAllText(PathSettingsFile), JsonOptions);
            return settings?.Normalize() ?? IpiPathSettings.Default;
        }
        catch
        {
            return IpiPathSettings.Default;
        }
    }

    private static string ResolveDirectoryOverride(string environmentVariable, string fallbackPath, string? settingsOverride)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value)) return Normalize(value);
        if (!string.IsNullOrWhiteSpace(settingsOverride)) return Normalize(settingsOverride);
        return Normalize(fallbackPath);
    }

    public static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
