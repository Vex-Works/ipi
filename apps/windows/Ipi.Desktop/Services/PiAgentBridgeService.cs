using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Ipi.Desktop.Models;

namespace Ipi.Desktop.Services;

public sealed record PiBridgeEvent(string Kind, string Label, string Detail, string EventType, string? SessionId = null, string? SessionFile = null);

public sealed record PiToolApprovalRequest(string ApprovalId, string ToolName, string Summary, string Detail);

public sealed record PiToolApprovalDecision(bool Approved, string Reason = "");

public sealed record PiBridgeRunResult(string? SessionId, string? SessionFile, string? FinalText);

public sealed class PiAgentBridgeService
{
    public async Task<PiBridgeRunResult> RunPromptAsync(
        string cwd,
        string agentDir,
        string message,
        Action<PiBridgeEvent> onEvent,
        string? sessionFile = null,
        string? thinkingLevel = null,
        IReadOnlyList<string>? tools = null,
        string? noTools = null,
        Func<PiToolApprovalRequest, Task<PiToolApprovalDecision>>? approveTool = null,
        string approvalMode = "default",
        IReadOnlyDictionary<string, string>? approvalRules = null,
        string? branchFromEntryId = null,
        string command = "prompt",
        string? compactInstructions = null,
        string? provider = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("ipi agent bridge was not copied to the output directory", bridgePath);

        var psi = new ProcessStartInfo
        {
            FileName = ResolveNodeCommand(),
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(bridgePath);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        var result = new PiBridgeRunResult(null, null, null);
        var stdinLock = new SemaphoreSlim(1, 1);

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            HandleBridgeLine(e.Data, onEvent, ref result, approveTool, process, stdinLock);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) stderr.AppendLine(e.Data);
        };

        if (!process.Start()) throw new InvalidOperationException("failed to start node bridge");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            cwd,
            agentDir,
            command,
            message,
            sessionFile,
            thinkingLevel,
            tools,
            noTools,
            approvalMode,
            approvalRules,
            branchFromEntryId,
            compactInstructions,
            provider,
            model,
        }));

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "agent bridge failed" : detail);
        }

        return result;
    }

    public Task<PiBridgeRunResult> CompactAsync(
        string cwd,
        string agentDir,
        string sessionFile,
        Action<PiBridgeEvent> onEvent,
        string? thinkingLevel = null,
        CancellationToken cancellationToken = default)
        => RunPromptAsync(cwd, agentDir, "", onEvent, sessionFile, thinkingLevel, null, null, null, "default", null, null, "compact", null, null, null, cancellationToken);

    public async Task<IReadOnlyList<PiModelOptionRecord>> ListModelsAsync(string cwd, string agentDir, CancellationToken cancellationToken = default)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("ipi agent bridge was not copied to the output directory", bridgePath);
        var workingDirectory = Directory.Exists(cwd) ? cwd : AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = ResolveNodeCommand(),
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

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("failed to start node bridge");
        using var cancellationRegistration = cancellationToken.Register(() =>
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
            command = "models",
            message = "",
        }));
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "agent bridge failed" : detail);
        }

        var models = new List<PiModelOptionRecord>();
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (GetString(root, "type") != "models" || !root.TryGetProperty("models", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var provider = GetString(item, "provider");
                var model = GetString(item, "model");
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) continue;
                var displayName = GetString(item, "displayName");
                var source = GetString(item, "source");
                var providerDisplayName = GetString(item, "providerDisplayName");
                var configured = !item.TryGetProperty("isConfigured", out var configuredElement) || configuredElement.ValueKind != JsonValueKind.False;
                models.Add(new PiModelOptionRecord(provider, model, string.IsNullOrWhiteSpace(displayName) ? model : displayName, string.IsNullOrWhiteSpace(source) ? "Pi registry" : source, configured, providerDisplayName));
            }
        }
        return models;
    }

    public async Task<IReadOnlyList<PiProviderCatalogRecord>> ListProviderCatalogAsync(string cwd, string agentDir, CancellationToken cancellationToken = default)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("ipi agent bridge was not copied to the output directory", bridgePath);
        var workingDirectory = Directory.Exists(cwd) ? cwd : AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = ResolveNodeCommand(),
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

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("failed to start node bridge");
        using var cancellationRegistration = cancellationToken.Register(() =>
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
            command = "provider_catalog",
            message = "",
        }));
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "agent bridge failed" : detail);
        }

        var providers = new List<PiProviderCatalogRecord>();
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (GetString(root, "type") != "provider_catalog" || !root.TryGetProperty("providers", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var provider = GetString(item, "provider");
                if (string.IsNullOrWhiteSpace(provider)) continue;
                var name = GetString(item, "displayName");
                var api = GetString(item, "api");
                var baseUrl = GetString(item, "baseUrl");
                var count = item.TryGetProperty("modelCount", out var countElement) && countElement.TryGetInt32(out var c) ? c : 0;
                var configured = item.TryGetProperty("isConfigured", out var configuredElement) && configuredElement.ValueKind == JsonValueKind.True;
                providers.Add(new PiProviderCatalogRecord(provider, string.IsNullOrWhiteSpace(name) ? provider : name, api, baseUrl, count, configured));
            }
        }
        return providers;
    }

    private static string ResolveNodeCommand() => new PiRuntimeService().Resolve().NodePath ?? "node";

    private static void HandleBridgeLine(
        string line,
        Action<PiBridgeEvent> onEvent,
        ref PiBridgeRunResult result,
        Func<PiToolApprovalRequest, Task<PiToolApprovalDecision>>? approveTool,
        Process process,
        SemaphoreSlim stdinLock)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = GetString(root, "type");
            switch (type)
            {
                case "ready":
                    result = result with
                    {
                        SessionId = GetString(root, "sessionId"),
                        SessionFile = GetString(root, "sessionFile"),
                    };
                    onEvent(new PiBridgeEvent("state", "session ready", ShortSession(result.SessionId), "ready", result.SessionId, result.SessionFile));
                    break;
                case "approval_request":
                    _ = RespondToApprovalAsync(root, approveTool, process, stdinLock);
                    break;
                case "event":
                    onEvent(new PiBridgeEvent(
                        GetString(root, "kind") is { Length: > 0 } kind ? kind : "state",
                        GetString(root, "label") is { Length: > 0 } label ? label : GetString(root, "eventType"),
                        GetString(root, "detail"),
                        GetString(root, "eventType")));
                    break;
                case "done":
                    result = result with
                    {
                        SessionId = GetString(root, "sessionId"),
                        SessionFile = GetString(root, "sessionFile"),
                        FinalText = GetString(root, "finalText"),
                    };
                    break;
                case "error":
                    onEvent(new PiBridgeEvent("error", "agent bridge error", GetString(root, "message"), "error"));
                    break;
            }
        }
        catch (Exception ex)
        {
            onEvent(new PiBridgeEvent("error", "bridge parse error", ex.Message, "parse"));
        }
    }

    private static async Task RespondToApprovalAsync(
        JsonElement root,
        Func<PiToolApprovalRequest, Task<PiToolApprovalDecision>>? approveTool,
        Process process,
        SemaphoreSlim stdinLock)
    {
        var approvalId = GetString(root, "approvalId");
        if (string.IsNullOrWhiteSpace(approvalId) || process.HasExited) return;

        var decision = approveTool is null
            ? new PiToolApprovalDecision(true)
            : await approveTool(new PiToolApprovalRequest(
                approvalId,
                GetString(root, "toolName"),
                GetString(root, "summary"),
                GetString(root, "detail")));

        await stdinLock.WaitAsync();
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "approval_response",
                    approvalId,
                    approved = decision.Approved,
                    reason = decision.Reason,
                }));
                await process.StandardInput.FlushAsync();
            }
        }
        catch { }
        finally
        {
            stdinLock.Release();
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return "";
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : prop.ToString();
    }

    private static string ShortSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "";
        return sessionId.Length <= 12 ? sessionId : sessionId[..12];
    }
}
