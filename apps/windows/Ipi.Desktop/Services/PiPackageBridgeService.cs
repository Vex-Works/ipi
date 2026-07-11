using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Ipi.Desktop.Models;

namespace Ipi.Desktop.Services;

public sealed class PiPackageBridgeService
{
    public Task<IReadOnlyList<PluginPackageRecord>> ListPackagesAsync(string cwd, string agentDir, CancellationToken cancellationToken = default)
        => RunAsync(cwd, agentDir, "list", null, "global", null, cancellationToken);

    public Task<IReadOnlyList<PluginPackageRecord>> InstallAsync(string cwd, string agentDir, string source, string scope, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        => RunAsync(cwd, agentDir, "install", source, scope, onProgress, cancellationToken);

    public Task<IReadOnlyList<PluginPackageRecord>> RemoveAsync(string cwd, string agentDir, string source, string scope, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        => RunAsync(cwd, agentDir, "remove", source, scope, onProgress, cancellationToken);

    public Task<IReadOnlyList<PluginPackageRecord>> UpdateAsync(string cwd, string agentDir, string? source, string scope, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        => RunAsync(cwd, agentDir, "update", source, scope, onProgress, cancellationToken);

    public Task<IReadOnlyList<PluginPackageRecord>> SetEnabledAsync(string cwd, string agentDir, string source, string scope, bool enabled, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
        => RunAsync(cwd, agentDir, enabled ? "enable" : "disable", source, scope, onProgress, cancellationToken);

    private async Task<IReadOnlyList<PluginPackageRecord>> RunAsync(
        string cwd,
        string agentDir,
        string action,
        string? source,
        string scope,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "package-bridge.mjs");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("ipi package bridge was not copied to the output directory", bridgePath);
        var workingDirectory = Directory.Exists(cwd) ? cwd : AppContext.BaseDirectory;
        var runtime = ResolveBridgeRuntime();
        var psi = new ProcessStartInfo
        {
            FileName = runtime.NodeCommand,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(bridgePath);
        ConfigureBridgeEnvironment(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("failed to start node package bridge");
        var timeout = action == "list" ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        using var cancellationRegistration = combinedCancellation.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            cwd = workingDirectory,
            agentDir,
            piCodingAgentRoot = runtime.PiCodingAgentRoot,
            action,
            source,
            scope = string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) ? "project" : "global",
        }));
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(combinedCancellation.Token);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw new TimeoutException($"package bridge {action} timed out after {timeout.TotalMinutes:0} minutes and its process tree was stopped");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var packages = new List<PluginPackageRecord>();
        var bridgeError = "";

        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = GetString(root, "type");
            if (type == "progress")
            {
                if (root.TryGetProperty("event", out var ev))
                {
                    var message = GetString(ev, "message");
                    var eventSource = GetString(ev, "source");
                    if (!string.IsNullOrWhiteSpace(message)) onProgress?.Invoke(message);
                    else if (!string.IsNullOrWhiteSpace(eventSource)) onProgress?.Invoke(eventSource);
                }
                continue;
            }
            if (type == "error")
            {
                bridgeError = GetString(root, "message");
                continue;
            }
            if (type != "packages" || !root.TryGetProperty("packages", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray()) packages.Add(ParsePackage(item));
        }

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(bridgeError) ? stderr.Trim() : bridgeError;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "package bridge failed" : detail);
        }

        return packages;
    }

    private static (string NodeCommand, string PiCodingAgentRoot) ResolveBridgeRuntime()
    {
        var runtime = new PiRuntimeService().Resolve();
        if (string.IsNullOrWhiteSpace(runtime.PiCodingAgentRoot))
        {
            throw new InvalidOperationException("Pi coding agent runtime was not found");
        }

        var piCodingAgentRoot = Path.GetFullPath(runtime.PiCodingAgentRoot);
        var entryPoint = Path.Combine(piCodingAgentRoot, "dist", "index.js");
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException("Pi coding agent runtime entry point was not found", entryPoint);
        }

        return (runtime.NodePath ?? "node", piCodingAgentRoot);
    }

    private static void ConfigureBridgeEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["IPI_APPDATA_DIR"] = IpiPathService.AppDataDir;
        psi.Environment["IPI_LOCALAPPDATA_DIR"] = IpiPathService.LocalAppDataDir;
    }

    private static PluginPackageRecord ParsePackage(JsonElement item)
    {
        var source = GetString(item, "source");
        var scope = GetString(item, "scope");
        var status = GetString(item, "status");
        var disabled = status.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || item.TryGetProperty("disabled", out var disabledElement) && disabledElement.ValueKind == JsonValueKind.True;
        var packageName = GetString(item, "packageName");
        var version = GetString(item, "version");
        var installedPath = GetString(item, "installedPath");
        var resources = new List<PluginResourceRecord>();
        if (item.TryGetProperty("resources", out var resourceItems) && resourceItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in resourceItems.EnumerateArray())
            {
                var kind = GetString(resource, "kind");
                var name = GetString(resource, "name");
                var relative = GetString(resource, "relativePath");
                var path = string.IsNullOrWhiteSpace(relative) ? GetString(resource, "path") : relative;
                if (!string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(name)) resources.Add(new PluginResourceRecord(kind, name, path));
            }
        }
        return new PluginPackageRecord(source, string.IsNullOrWhiteSpace(scope) ? "global" : scope, status, disabled, string.IsNullOrWhiteSpace(packageName) ? source : packageName, version, installedPath, resources);
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }
}
