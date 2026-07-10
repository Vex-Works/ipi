using System.IO;
using System.Text.Json;

namespace Ipi.Desktop.Services;

public sealed class PathSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath => IpiPathService.PathSettingsFile;

    public IpiPathSettings Load() => IpiPathService.LoadPathSettings();

    public void Save(IpiPathSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
    }
}

public sealed record IpiPathSettings(string? AppDataDir, string? LocalAppDataDir)
{
    public static IpiPathSettings Default => new(null, null);

    public IpiPathSettings Normalize() => new(NormalizePath(AppDataDir), NormalizePath(LocalAppDataDir));

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
