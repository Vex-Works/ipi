using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ipi.Desktop.Services;

public sealed record RuntimeBootstrapInspection(
    string AgentDir,
    string NodeCommand,
    string NodeDetail,
    bool HasCompatibleNode,
    string PiCodingAgentRoot,
    bool HasPiCodingAgent,
    bool HasAgentDirectory,
    bool HasSettings,
    bool HasModels,
    bool HasSessions,
    bool HasPackageDirectory,
    bool IsReady,
    IReadOnlyList<RuntimeBootstrapAction> RequiredActions);

public sealed record RuntimeBootstrapAction(string Label, string Detail);

public sealed class RuntimeBootstrapService
{
    public const string PiPackageSpec = "@earendil-works/pi-coding-agent@latest";
    public const string PiPackageName = "@earendil-works/pi-coding-agent";
    public const string PiPackageUrl = "https://www.npmjs.com/package/@earendil-works/pi-coding-agent";
    public const string NodeVersion = "v22.19.0";
    public const string NodeArchiveName = "node-v22.19.0-win-x64.zip";
    public const string NodeArchiveFolder = "node-v22.19.0-win-x64";
    public const string NodeDownloadUrl = "https://nodejs.org/dist/v22.19.0/node-v22.19.0-win-x64.zip";
    public const string NodeShasumsUrl = "https://nodejs.org/dist/v22.19.0/SHASUMS256.txt";

    private static readonly Version MinimumNodeVersion = new(22, 19, 0);
    private readonly string _appDataDir = IpiPathService.AppDataDir;
    private readonly string _localAppDataDir = IpiPathService.LocalAppDataDir;

    public string ManagedAgentDir => Path.Combine(_appDataDir, "agent");
    public string ManagedRuntimeDir => Path.Combine(_localAppDataDir, "runtime");
    public string ManagedNodeDir => Path.Combine(ManagedRuntimeDir, "node");
    public string ManagedNodeExe => Path.Combine(ManagedNodeDir, "node.exe");
    public string ManagedNpmCmd => Path.Combine(ManagedNodeDir, "npm.cmd");
    public string ManagedPiRuntimeDir => Path.Combine(ManagedRuntimeDir, "pi");
    public string ManagedPiCodingAgentRoot => Path.Combine(ManagedPiRuntimeDir, "node_modules", "@earendil-works", "pi-coding-agent");
    public string RuntimeConfigPath => Path.Combine(_appDataDir, "runtime.json");
    public string LogPath => Path.Combine(_appDataDir, "logs", "setup-runtime.log");

    public RuntimeBootstrapInspection Inspect()
    {
        var runtime = new PiRuntimeService().Resolve();
        var node = ResolveBestNode(runtime.NodePath);
        var piRoot = ResolvePiRoot(runtime);
        var agentDir = ResolveAgentDir(runtime);
        var settings = Path.Combine(agentDir, "settings.json");
        var models = Path.Combine(agentDir, "models.json");
        var sessions = Path.Combine(agentDir, "sessions");
        var packages = Path.Combine(agentDir, "npm");

        var hasPi = !string.IsNullOrWhiteSpace(piRoot) && File.Exists(Path.Combine(piRoot, "dist", "index.js"));
        var hasAgent = Directory.Exists(agentDir);
        var hasSettings = File.Exists(settings);
        var hasModels = File.Exists(models);
        var hasSessions = Directory.Exists(sessions);
        var hasPackages = Directory.Exists(packages);
        var actions = new List<RuntimeBootstrapAction>();
        if (!node.IsCompatible)
        {
            actions.Add(new RuntimeBootstrapAction(
                "Download portable Node.js",
                $"{NodeDownloadUrl} → {ManagedNodeDir}"));
        }
        if (!hasPi)
        {
            actions.Add(new RuntimeBootstrapAction(
                "Install upstream Pi package",
                $"npm install --ignore-scripts {PiPackageSpec} in {ManagedPiRuntimeDir}"));
        }
        if (!hasAgent || !hasSettings || !hasModels || !hasSessions || !hasPackages)
        {
            actions.Add(new RuntimeBootstrapAction(
                "Initialize ipi local agent data",
                $"{agentDir} with settings.json, models.json, sessions, skills, npm"));
        }
        if (!IsRuntimeConfigCurrent(agentDir, node.Command, hasPi ? piRoot : ManagedPiCodingAgentRoot))
        {
            actions.Add(new RuntimeBootstrapAction(
                "Write ipi runtime config",
                RuntimeConfigPath));
        }

        return new RuntimeBootstrapInspection(
            agentDir,
            node.Command,
            node.Detail,
            node.IsCompatible,
            hasPi ? piRoot : ManagedPiCodingAgentRoot,
            hasPi,
            hasAgent,
            hasSettings,
            hasModels,
            hasSessions,
            hasPackages,
            node.IsCompatible && hasPi && hasAgent && hasSettings && hasModels && hasSessions && hasPackages,
            actions);
    }

    public async Task<RuntimeBootstrapInspection> BootstrapAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        await File.AppendAllTextAsync(LogPath, $"[{DateTimeOffset.Now:O}] ipi runtime setup started{Environment.NewLine}", cancellationToken);
        void Log(string message)
        {
            progress?.Report(message);
            try
            {
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Setup logging must not fail the setup itself.
            }
        }

        var inspection = Inspect();
        Log("Resolved setup plan.");
        foreach (var action in inspection.RequiredActions) Log($"Plan: {action.Label} · {action.Detail}");

        var nodeCommand = inspection.HasCompatibleNode ? inspection.NodeCommand : ManagedNodeExe;
        if (!inspection.HasCompatibleNode)
        {
            Log($"Downloading Node.js {NodeVersion} from {NodeDownloadUrl}");
            await InstallManagedNodeAsync(Log, cancellationToken);
            var node = CheckNode(ManagedNodeExe);
            if (!node.IsCompatible) throw new InvalidOperationException($"Managed Node.js is not compatible: {node.Detail}");
            nodeCommand = ManagedNodeExe;
            Log($"Node.js ready: {node.Detail}");
        }
        else
        {
            Log($"Node.js ready: {inspection.NodeDetail}");
        }

        if (!inspection.HasPiCodingAgent)
        {
            Log($"Installing upstream Pi package from npm: {PiPackageSpec}");
            await InstallPiPackageAsync(nodeCommand, Log, cancellationToken);
            if (!File.Exists(Path.Combine(ManagedPiCodingAgentRoot, "dist", "index.js")))
            {
                throw new InvalidOperationException($"Pi package install completed but dist/index.js was not found under {ManagedPiCodingAgentRoot}");
            }
            Log($"Pi package ready: {ManagedPiCodingAgentRoot}");
        }
        else
        {
            Log($"Pi package ready: {inspection.PiCodingAgentRoot}");
        }

        var refreshed = Inspect();
        var piRoot = refreshed.HasPiCodingAgent ? refreshed.PiCodingAgentRoot : ManagedPiCodingAgentRoot;
        InitializeAgentFiles(refreshed.AgentDir, Log);
        WriteRuntimeConfig(refreshed.AgentDir, File.Exists(ManagedNodeExe) ? ManagedNodeExe : nodeCommand, piRoot, Log);

        var final = Inspect();
        if (!final.IsReady)
        {
            var missing = string.Join("; ", final.RequiredActions.Select(action => action.Label));
            throw new InvalidOperationException($"Runtime setup did not finish cleanly: {missing}");
        }
        Log("ipi runtime setup finished.");
        return final;
    }

    private string ResolveAgentDir(PiRuntimeInfo runtime)
    {
        if (Directory.Exists(runtime.AgentDir) || File.Exists(runtime.SettingsPath) || File.Exists(runtime.ModelsPath)) return runtime.AgentDir;
        return ManagedAgentDir;
    }

    private string ResolvePiRoot(PiRuntimeInfo runtime)
    {
        var appData = Path.GetDirectoryName(IpiPathService.AppDataDir) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            runtime.PiCodingAgentRoot,
            ManagedPiCodingAgentRoot,
            Path.Combine(runtime.AgentDir, "npm", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(runtime.AgentDir, "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent"),
            Path.Combine(appData, "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent"),
        };
        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, "dist", "index.js"))) ?? "";
    }

    private NodeCheck ResolveBestNode(string? configuredNode)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredNode)) candidates.Add(configuredNode);
        if (File.Exists(ManagedNodeExe)) candidates.Add(ManagedNodeExe);
        candidates.Add("node");

        NodeCheck? firstResult = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var check = CheckNode(candidate);
            firstResult ??= check;
            if (check.IsCompatible) return check;
        }
        return firstResult ?? new NodeCheck("node", "missing", false);
    }

    private static NodeCheck CheckNode(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null) return new NodeCheck(command, $"{command} · unable to start", false);
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new NodeCheck(command, $"{command} · version check timed out", false);
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0) return new NodeCheck(command, $"{command} · {error}", false);
            var match = Regex.Match(output, "v?(\\d+)\\.(\\d+)\\.(\\d+)");
            if (!match.Success) return new NodeCheck(command, $"{command} · {output}", false);
            var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            return new NodeCheck(command, $"{command} · {output}", version >= MinimumNodeVersion);
        }
        catch (Exception ex)
        {
            return new NodeCheck(command, $"{command} · {ex.Message}", false);
        }
    }

    private async Task InstallManagedNodeAsync(Action<string> log, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManagedRuntimeDir);
        var downloadDir = Path.Combine(ManagedRuntimeDir, "downloads");
        var extractDir = Path.Combine(ManagedRuntimeDir, "extract-node");
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, NodeArchiveName);
        var shasumsPath = Path.Combine(downloadDir, "SHASUMS256.txt");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await DownloadAsync(http, NodeDownloadUrl, zipPath, cancellationToken);
        await DownloadAsync(http, NodeShasumsUrl, shasumsPath, cancellationToken);
        VerifyNodeArchiveSha256(zipPath, shasumsPath);
        log("Node.js archive checksum verified.");

        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        var extractedRoot = Path.Combine(extractDir, NodeArchiveFolder);
        if (!File.Exists(Path.Combine(extractedRoot, "node.exe"))) throw new InvalidOperationException("Downloaded Node.js archive did not contain node.exe.");

        if (Directory.Exists(ManagedNodeDir)) Directory.Delete(ManagedNodeDir, recursive: true);
        Directory.Move(extractedRoot, ManagedNodeDir);
        try { Directory.Delete(extractDir, recursive: true); } catch { }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void VerifyNodeArchiveSha256(string zipPath, string shasumsPath)
    {
        var expectedLine = File.ReadLines(shasumsPath).FirstOrDefault(line => line.EndsWith($"  {NodeArchiveName}", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(expectedLine)) throw new InvalidOperationException($"Could not find {NodeArchiveName} in Node.js SHASUMS256.txt.");
        var expected = expectedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Node.js archive checksum verification failed.");
    }

    private async Task InstallPiPackageAsync(string nodeCommand, Action<string> log, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManagedPiRuntimeDir);
        var packageJsonPath = Path.Combine(ManagedPiRuntimeDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            var packageJson = new JsonObject
            {
                ["name"] = "ipi-managed-pi-runtime",
                ["private"] = true,
                ["description"] = "ipi-managed upstream Pi runtime. Safe to delete from ipi diagnostics.",
            };
            File.WriteAllText(packageJsonPath, packageJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        var npmCommand = ResolveNpmCommand(nodeCommand);
        var psi = new ProcessStartInfo
        {
            FileName = npmCommand,
            WorkingDirectory = ManagedPiRuntimeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--ignore-scripts");
        psi.ArgumentList.Add("--no-audit");
        psi.ArgumentList.Add("--no-fund");
        psi.ArgumentList.Add(PiPackageSpec);
        psi.Environment["npm_config_ignore_scripts"] = "true";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("Unable to start npm install for Pi runtime.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"npm install failed with exit code {process.ExitCode}");
    }

    private string ResolveNpmCommand(string nodeCommand)
    {
        if (Path.IsPathFullyQualified(nodeCommand))
        {
            var npm = Path.Combine(Path.GetDirectoryName(nodeCommand) ?? "", OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
            if (File.Exists(npm)) return npm;
        }

        var command = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var resolved = FindExecutableOnPath(command);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        return command;
    }

    private static string? FindExecutableOnPath(string executableName)
    {
        if (Path.IsPathFullyQualified(executableName) && File.Exists(executableName)) return executableName;
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    private void InitializeAgentFiles(string agentDir, Action<string> log)
    {
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(Path.Combine(agentDir, "sessions"));
        Directory.CreateDirectory(Path.Combine(agentDir, "skills"));
        Directory.CreateDirectory(Path.Combine(agentDir, "npm"));
        Directory.CreateDirectory(Path.Combine(agentDir, "npm", "node_modules"));

        var modelsPath = Path.Combine(agentDir, "models.json");
        if (!File.Exists(modelsPath) || string.IsNullOrWhiteSpace(File.ReadAllText(modelsPath)))
        {
            File.WriteAllText(modelsPath, new JsonObject { ["providers"] = new JsonObject() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log($"Created {modelsPath}");
        }

        var settingsPath = Path.Combine(agentDir, "settings.json");
        if (!File.Exists(settingsPath) || string.IsNullOrWhiteSpace(File.ReadAllText(settingsPath)))
        {
            var settings = new JsonObject
            {
                ["defaultProvider"] = "",
                ["defaultModel"] = "",
                ["defaultThinkingLevel"] = "medium",
                ["packages"] = new JsonArray(),
            };
            File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log($"Created {settingsPath}");
        }
    }

    private void WriteRuntimeConfig(string agentDir, string nodePath, string piCodingAgentRoot, Action<string> log)
    {
        Directory.CreateDirectory(_appDataDir);
        JsonObject root;
        try
        {
            root = File.Exists(RuntimeConfigPath)
                ? JsonNode.Parse(File.ReadAllText(RuntimeConfigPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["agentDir"] = agentDir;
        root["nodePath"] = nodePath;
        root["piCodingAgentRoot"] = piCodingAgentRoot;
        if (!root.ContainsKey("codexSkillDir")) root["codexSkillDir"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills");
        File.WriteAllText(RuntimeConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        log($"Wrote {RuntimeConfigPath}");
    }

    private bool IsRuntimeConfigCurrent(string agentDir, string nodeCommand, string piCodingAgentRoot)
    {
        try
        {
            if (!File.Exists(RuntimeConfigPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(RuntimeConfigPath));
            var root = doc.RootElement;
            return SamePath(ReadString(root, "agentDir"), agentDir)
                   && (nodeCommand.Equals("node", StringComparison.OrdinalIgnoreCase) || SamePath(ReadString(root, "nodePath"), nodeCommand))
                   && SamePath(ReadString(root, "piCodingAgentRoot"), piCodingAgentRoot);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)), Path.GetFullPath(Environment.ExpandEnvironmentVariables(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record NodeCheck(string Command, string Detail, bool IsCompatible);
}
