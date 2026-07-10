using System.IO;
using System.Text.Json;

namespace Ipi.Desktop.Services;

public sealed record PiRuntimeInfo(
    string AgentDir,
    string CodexSkillDir,
    string? NodePath,
    string? PiCodingAgentRoot,
    string RuntimeMode,
    bool IsBundled,
    bool IsInitialized)
{
    public string SessionsDir => Path.Combine(AgentDir, "sessions");
    public string SettingsPath => Path.Combine(AgentDir, "settings.json");
    public string ModelsPath => Path.Combine(AgentDir, "models.json");
    public string NpmPackagesDir => Path.Combine(AgentDir, "npm");
}

public sealed class PiRuntimeService
{
    public PiRuntimeInfo Resolve()
    {
        var baseDir = AppContext.BaseDirectory;
        var appDataDir = IpiPathService.AppDataDir;
        var localAppDataDir = IpiPathService.LocalAppDataDir;
        var runtimeConfig = LoadRuntimeConfig(appDataDir);

        var ignoreUserProfileRuntime = IsTruthy(Environment.GetEnvironmentVariable("IPI_IGNORE_USER_PROFILE_RUNTIME"));
        var agentCandidates = new List<RuntimeCandidate>
        {
            new("env", Environment.GetEnvironmentVariable("PI_AGENT_DIR"), false),
            new("configured", runtimeConfig.AgentDir, false),
            new("bundled", Path.Combine(baseDir, "runtime", "pi-agent"), true),
            new("appdata", Path.Combine(appDataDir, "runtime", "pi-agent"), false),
            new("localappdata", Path.Combine(localAppDataDir, "runtime", "pi-agent"), false),
        };
        if (!ignoreUserProfileRuntime) agentCandidates.Add(new RuntimeCandidate("userprofile", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"), false));

        var agent = ResolveCandidate(agentCandidates);
        var codexSkillDir = ResolveDirectory("CODEX_SKILLS_DIR", new[]
        {
            runtimeConfig.CodexSkillDir,
            Path.Combine(baseDir, "runtime", "codex-skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills"),
        });

        var nodePath = ResolveNodePath(baseDir, localAppDataDir, runtimeConfig.NodePath);
        var agentPath = agent.Path ?? agentCandidates.First(candidate => !string.IsNullOrWhiteSpace(candidate.Path)).Path!;
        var piCodingAgentRoot = ResolvePiCodingAgentRoot(baseDir, localAppDataDir, agentPath, runtimeConfig.PiCodingAgentRoot);
        var initialized = File.Exists(Path.Combine(agentPath, "settings.json")) || File.Exists(Path.Combine(agentPath, "package.json"));
        return new PiRuntimeInfo(agentPath, codexSkillDir, nodePath, piCodingAgentRoot, agent.Mode, agent.IsBundled, initialized);
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeCandidate ResolveCandidate(IEnumerable<RuntimeCandidate> candidates)
    {
        var first = candidates.First(candidate => !string.IsNullOrWhiteSpace(candidate.Path));
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && Directory.Exists(candidate.Path)) return candidate;
        }
        return first;
    }

    private static string ResolveDirectory(string environmentVariable, IEnumerable<string?> candidates)
    {
        var fromEnv = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;
        var usableCandidates = candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)).ToList();
        foreach (var candidate in usableCandidates)
        {
            if (Directory.Exists(candidate)) return candidate!;
        }
        return usableCandidates.First()!;
    }

    private static string? ResolveNodePath(string baseDir, string localAppDataDir, string? configuredNodePath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("IPI_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        if (!string.IsNullOrWhiteSpace(configuredNodePath) && File.Exists(configuredNodePath)) return configuredNodePath;

        var candidates = new[]
        {
            Path.Combine(baseDir, "runtime", "node", "node.exe"),
            Path.Combine(localAppDataDir, "runtime", "node", "node.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolvePiCodingAgentRoot(string baseDir, string localAppDataDir, string agentDir, string? configuredRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("PI_CODING_AGENT_ROOT");
        var appData = Path.GetDirectoryName(IpiPathService.AppDataDir) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            fromEnv,
            configuredRoot,
            Path.Combine(baseDir, "runtime", "pi", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(localAppDataDir, "runtime", "pi", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(agentDir, "npm", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(agentDir, "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(appData, "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent"),
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            File.Exists(Path.Combine(candidate, "dist", "index.js")));
    }

    private static RuntimeConfig LoadRuntimeConfig(string appDataDir)
    {
        try
        {
            var path = Path.Combine(appDataDir, "runtime.json");
            if (!File.Exists(path)) return RuntimeConfig.Empty;
            var config = JsonSerializer.Deserialize<RuntimeConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return config ?? RuntimeConfig.Empty;
        }
        catch
        {
            return RuntimeConfig.Empty;
        }
    }

    private sealed record RuntimeCandidate(string Mode, string? Path, bool IsBundled);
    private sealed record RuntimeConfig(string? AgentDir, string? CodexSkillDir, string? NodePath, string? PiCodingAgentRoot)
    {
        public static RuntimeConfig Empty { get; } = new(null, null, null, null);
    }
}
