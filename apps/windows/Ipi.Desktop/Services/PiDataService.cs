using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ipi.Desktop.Models;

namespace Ipi.Desktop.Services;

public sealed class PiDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _appDataDir = IpiPathService.AppDataDir;

    public PiRuntimeInfo RuntimeInfo { get; } = new PiRuntimeService().Resolve();
    public string AgentDir => RuntimeInfo.AgentDir;
    public string CodexSkillDir => RuntimeInfo.CodexSkillDir;
    public string SessionsDir => RuntimeInfo.SessionsDir;
    public string SettingsPath => RuntimeInfo.SettingsPath;
    public string ModelsPath => RuntimeInfo.ModelsPath;

    public IReadOnlyList<PiSessionRecord> ListSessions(int take = 80)
    {
        if (!Directory.Exists(SessionsDir)) return Array.Empty<PiSessionRecord>();

        var files = Directory.GetFiles(SessionsDir, "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(take);

        var result = new List<PiSessionRecord>();
        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = new StreamReader(stream);
                var firstLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine)) continue;

                using var headerDoc = JsonDocument.Parse(firstLine);
                var header = headerDoc.RootElement;
                if (!TryGetString(header, "id", out var id)) id = Path.GetFileNameWithoutExtension(file);
                if (!TryGetString(header, "cwd", out var cwd)) cwd = "";
                if (string.IsNullOrWhiteSpace(cwd)) cwd = DecodeSessionCwdFromFolder(file);
                if (!TryGetString(header, "timestamp", out var timestamp)) timestamp = File.GetCreationTime(file).ToString("O");
                TryGetString(header, "parentSession", out var parentSession);

                var count = 0;
                var firstMessage = "(no messages)";
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Contains("\"type\":\"message\"")) continue;
                    count++;
                    if (firstMessage == "(no messages)")
                    {
                        var msg = ParseMessageLine(line);
                        if (msg is { Role: "user" } && !string.IsNullOrWhiteSpace(msg.Text)) firstMessage = msg.Text;
                    }
                }

                result.Add(new PiSessionRecord(
                    id,
                    file,
                    cwd,
                    MakeSessionTitle(firstMessage, cwd),
                    DateTime.TryParse(timestamp, out var created) ? created : File.GetCreationTime(file),
                    File.GetLastWriteTime(file),
                    count,
                    firstMessage,
                    parentSession
                ));
            }
            catch
            {
                // Skip malformed or migrating session files.
            }
        }
        return result;
    }

    public IReadOnlyList<PiTimelineRecord> ReadSessionTimeline(string filePath, int take = 800, bool includeToolResults = true)
    {
        if (!File.Exists(filePath)) return Array.Empty<PiTimelineRecord>();

        var unlimited = take <= 0;
        var toolResults = includeToolResults ? ReadToolResults(filePath) : new Dictionary<string, ToolResultInfo>();
        var result = new Queue<PiTimelineRecord>();
        void AddRecord(PiTimelineRecord record)
        {
            result.Enqueue(record);
            if (!unlimited)
            {
                while (result.Count > take) result.Dequeue();
            }
        }

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!includeToolResults && !LooksLikeTimelineLine(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var entryId = TryGetString(root, "id", out var eid) ? eid : "";
                var parentId = TryGetString(root, "parentId", out var pid) ? pid : null;
                if (TryGetString(root, "type", out var type) && type == "compaction")
                {
                    var summary = TryGetString(root, "summary", out var s) ? s : "Conversation compacted";
                    AddRecord(new PiTimelineRecord("compaction", summary, "", "system", ReadEntryTimestamp(root), entryId, parentId));
                    continue;
                }
                if (!root.TryGetProperty("message", out var message)) continue;
                var role = TryGetString(message, "role", out var r) ? r : "unknown";
                var ts = ReadEntryTimestamp(root) ?? ReadEntryTimestamp(message);
                if (role == "user")
                {
                    var contentParts = ExtractMessageContent(message, entryId);
                    if (!string.IsNullOrWhiteSpace(contentParts.Text) || contentParts.Attachments.Count > 0)
                    {
                        AddRecord(new PiTimelineRecord("user", contentParts.Text, "", "message", ts, entryId, parentId, contentParts.Attachments));
                    }
                    continue;
                }
                if (role == "assistant")
                {
                    var model = TryGetString(message, "model", out var m) ? m : "assistant";
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (!TryGetString(block, "type", out var blockType)) continue;
                            if (blockType == "text" && TryGetString(block, "text", out var text) && !string.IsNullOrWhiteSpace(text))
                            {
                                AddRecord(new PiTimelineRecord("assistant", text, "", "message", ts, entryId, parentId));
                            }
                            else if (blockType == "thinking")
                            {
                                var thinking = TryGetString(block, "thinking", out var t) && !string.IsNullOrWhiteSpace(t) ? t : "Reasoning hidden by provider; timing/usage is shown on the following model turn.";
                                AddRecord(new PiTimelineRecord("thinking", "Thinking", thinking, "thinking", ts, entryId, parentId));
                            }
                            else if (blockType == "toolCall" && includeToolResults)
                            {
                                var toolName = TryGetString(block, "name", out var name) ? name : TryGetString(block, "toolName", out var tn) ? tn : "tool";
                                var callId = TryGetString(block, "id", out var id) ? id : TryGetString(block, "toolCallId", out var tcid) ? tcid : "";
                                var args = block.TryGetProperty("arguments", out var arguments) ? arguments.ToString() : block.TryGetProperty("input", out var input) ? input.ToString() : "{}";
                                var preview = BuildToolPreview(toolName, args);
                                var detail = args;
                                if (!string.IsNullOrWhiteSpace(callId) && toolResults.TryGetValue(callId, out var tr))
                                {
                                    var duration = ts.HasValue && tr.Timestamp.HasValue ? Math.Max(0, (int)Math.Round((tr.Timestamp.Value - ts.Value).TotalSeconds)) : 0;
                                    detail = TrimForPreview($"arguments\n{args}\n\nresult{(duration > 0 ? $" · {duration}s" : "")}\n{tr.Text}", ToolResultPreviewLimit);
                                }
                                AddRecord(new PiTimelineRecord(toolName, preview, detail, "tool", ts, entryId, parentId));
                            }
                        }
                    }
                    var usage = FormatUsage(message);
                    if (!string.IsNullOrWhiteSpace(usage)) AddRecord(new PiTimelineRecord(model, usage, UsageDetail(message), "usage", ts, entryId, parentId));
                }
            }
            catch { }
        }
        return result.ToList();
    }

    public IReadOnlyList<PiTimelineMarkerRecord> ReadSessionUserMarkers(string filePath, int take = 5000)
    {
        if (!File.Exists(filePath)) return Array.Empty<PiTimelineMarkerRecord>();
        var markers = new List<PiTimelineMarkerRecord>();
        var timelineIndex = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var entryId = TryGetString(root, "id", out var eid) ? eid : "";
                var parentId = TryGetString(root, "parentId", out var pid) ? pid : null;
                if (TryGetString(root, "type", out var type) && type == "compaction")
                {
                    timelineIndex++;
                    continue;
                }
                if (!root.TryGetProperty("message", out var message)) continue;
                var role = TryGetString(message, "role", out var r) ? r : "unknown";
                if (role == "user")
                {
                    var contentParts = ExtractMessageContent(message, entryId);
                    if (!string.IsNullOrWhiteSpace(contentParts.Text) || contentParts.Attachments.Count > 0)
                    {
                        markers.Add(new PiTimelineMarkerRecord(timelineIndex, contentParts.Text, entryId, parentId, ReadEntryTimestamp(root) ?? ReadEntryTimestamp(message)));
                        timelineIndex++;
                    }
                    if (markers.Count >= take) break;
                }
                else if (role == "assistant")
                {
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (!TryGetString(block, "type", out var blockType)) continue;
                            if (blockType is "text" or "thinking" or "toolCall") timelineIndex++;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(FormatUsage(message))) timelineIndex++;
                }
            }
            catch { }
        }
        return markers;
    }

    public IReadOnlyList<PiTimelineRecord> ReadSessionTimelineWindow(string filePath, int centerTimelineIndex, int before = 80, int after = 180, bool includeToolResults = true)
    {
        if (!File.Exists(filePath)) return Array.Empty<PiTimelineRecord>();

        var startIndex = Math.Max(0, centerTimelineIndex - Math.Max(0, before));
        var endIndex = centerTimelineIndex + Math.Max(0, after);
        var timelineIndex = 0;
        var window = new List<TimelineWindowItem>();
        var toolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddWindowRecord(PiTimelineRecord record, string? toolCallId = null)
        {
            if (timelineIndex >= startIndex && timelineIndex <= endIndex)
            {
                window.Add(new TimelineWindowItem(record, toolCallId));
                if (!string.IsNullOrWhiteSpace(toolCallId)) toolCallIds.Add(toolCallId);
            }
            timelineIndex++;
        }

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var entryId = TryGetString(root, "id", out var eid) ? eid : "";
                var parentId = TryGetString(root, "parentId", out var pid) ? pid : null;
                if (TryGetString(root, "type", out var type) && type == "compaction")
                {
                    if (timelineIndex > endIndex) break;
                    var summary = TryGetString(root, "summary", out var s) ? s : "Conversation compacted";
                    AddWindowRecord(new PiTimelineRecord("compaction", summary, "", "system", ReadEntryTimestamp(root), entryId, parentId));
                    continue;
                }

                if (!root.TryGetProperty("message", out var message)) continue;
                var role = TryGetString(message, "role", out var r) ? r : "unknown";
                var ts = ReadEntryTimestamp(root) ?? ReadEntryTimestamp(message);
                if (role == "user")
                {
                    if (timelineIndex > endIndex) break;
                    var contentParts = ExtractMessageContent(message, entryId);
                    if (!string.IsNullOrWhiteSpace(contentParts.Text) || contentParts.Attachments.Count > 0)
                    {
                        AddWindowRecord(new PiTimelineRecord("user", contentParts.Text, "", "message", ts, entryId, parentId, contentParts.Attachments));
                    }
                    continue;
                }

                if (role != "assistant") continue;
                var model = TryGetString(message, "model", out var m) ? m : "assistant";
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!TryGetString(block, "type", out var blockType)) continue;
                        if (timelineIndex > endIndex) break;
                        if (blockType == "text" && TryGetString(block, "text", out var text) && !string.IsNullOrWhiteSpace(text))
                        {
                            AddWindowRecord(new PiTimelineRecord("assistant", text, "", "message", ts, entryId, parentId));
                        }
                        else if (blockType == "thinking")
                        {
                            var thinking = TryGetString(block, "thinking", out var t) && !string.IsNullOrWhiteSpace(t) ? t : "Reasoning hidden by provider; timing/usage is shown on the following model turn.";
                            AddWindowRecord(new PiTimelineRecord("thinking", "Thinking", thinking, "thinking", ts, entryId, parentId));
                        }
                        else if (blockType == "toolCall" && includeToolResults)
                        {
                            var toolName = TryGetString(block, "name", out var name) ? name : TryGetString(block, "toolName", out var tn) ? tn : "tool";
                            var callId = TryGetString(block, "id", out var id) ? id : TryGetString(block, "toolCallId", out var tcid) ? tcid : "";
                            var args = block.TryGetProperty("arguments", out var arguments) ? arguments.ToString() : block.TryGetProperty("input", out var input) ? input.ToString() : "{}";
                            AddWindowRecord(new PiTimelineRecord(toolName, BuildToolPreview(toolName, args), args, "tool", ts, entryId, parentId), callId);
                        }
                    }
                }
                if (timelineIndex > endIndex) break;
                var usage = FormatUsage(message);
                if (!string.IsNullOrWhiteSpace(usage)) AddWindowRecord(new PiTimelineRecord(model, usage, UsageDetail(message), "usage", ts, entryId, parentId));
            }
            catch { }
        }

        if (includeToolResults && toolCallIds.Count > 0)
        {
            var results = ReadSelectedToolResults(filePath, toolCallIds);
            for (var i = 0; i < window.Count; i++)
            {
                var item = window[i];
                if (string.IsNullOrWhiteSpace(item.ToolCallId) || !results.TryGetValue(item.ToolCallId, out var tr)) continue;
                var duration = item.Record.Timestamp.HasValue && tr.Timestamp.HasValue ? Math.Max(0, (int)Math.Round((tr.Timestamp.Value - item.Record.Timestamp.Value).TotalSeconds)) : 0;
                var detail = TrimForPreview($"arguments\n{item.Record.Detail}\n\nresult{(duration > 0 ? $" · {duration}s" : "")}\n{tr.Text}", ToolResultPreviewLimit);
                window[i] = item with { Record = item.Record with { Detail = detail } };
            }
        }

        return window.Select(item => item.Record).ToList();
    }

    public SessionUsageSummary ReadSessionUsageSummary(string filePath)
    {
        if (!File.Exists(filePath)) return new SessionUsageSummary(0, 0, 0, 0, 0, 0);
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0;
        double costTotal = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"usage\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message)) continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                if (TryGetNumber(usage, "input", out var i)) input += i;
                if (TryGetNumber(usage, "output", out var o)) output += o;
                if (TryGetNumber(usage, "cacheRead", out var cr)) cacheRead += cr;
                if (TryGetNumber(usage, "cacheWrite", out var cw)) cacheWrite += cw;
                if (TryGetNumber(usage, "totalTokens", out var tt)) totalTokens = tt;
                if (usage.TryGetProperty("cost", out var cost) && TryGetDouble(cost, "total", out var ct)) costTotal += ct;
            }
            catch { }
        }
        return new SessionUsageSummary(input, output, cacheRead, cacheWrite, totalTokens, costTotal);
    }

    public SessionInspectionSummary ReadSessionInspectionSummary(string filePath)
    {
        if (!File.Exists(filePath)) return new SessionInspectionSummary(filePath, "", 0, 0, 0, 0, 0, new SessionUsageSummary(0, 0, 0, 0, 0, 0));

        var id = Path.GetFileNameWithoutExtension(filePath);
        var user = 0;
        var assistant = 0;
        var toolCalls = 0;
        var toolResults = 0;
        var isFirstLine = true;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (isFirstLine)
                {
                    isFirstLine = false;
                    if (TryGetString(root, "id", out var headerId) && !string.IsNullOrWhiteSpace(headerId)) id = headerId;
                }

                if (!root.TryGetProperty("message", out var message)) continue;
                var role = TryGetString(message, "role", out var r) ? r : "";
                if (role == "user") user++;
                else if (role == "assistant")
                {
                    assistant++;
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (TryGetString(block, "type", out var type) && type == "toolCall") toolCalls++;
                        }
                    }
                }
                else if (role == "toolResult") toolResults++;
            }
            catch { }
        }

        return new SessionInspectionSummary(filePath, id, user, assistant, toolCalls, toolResults, user + assistant + toolResults, ReadSessionUsageSummary(filePath));
    }

    public IReadOnlyList<PiMessageRecord> ReadSessionMessages(string filePath, int take = 400)
    {
        if (!File.Exists(filePath)) return Array.Empty<PiMessageRecord>();
        var result = new List<PiMessageRecord>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (result.Count >= take) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("\"type\":\"message\""))
            {
                var msg = ParseMessageLine(line);
                if (msg is not null) result.Add(msg);
            }
            else if (line.Contains("\"type\":\"compaction\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var id = TryGetString(root, "id", out var entryId) ? entryId : Guid.NewGuid().ToString("N");
                    var summary = TryGetString(root, "summary", out var s) ? s : "Conversation compacted";
                    result.Add(new PiMessageRecord("compaction", summary, id, ReadEntryTimestamp(root), "compaction"));
                }
                catch { }
            }
        }
        return result;
    }

    public IReadOnlyList<SkillSourceRecord> ListSkillSources()
    {
        var settings = LoadSkillSettings();
        return BuildSkillSources(settings).ToList();
    }

    public IReadOnlyList<SkillRecord> ListSkills(int take = 200)
    {
        var settings = LoadSkillSettings();
        var disabledSkills = new HashSet<string>(settings.DisabledSkills.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var result = new List<SkillRecord>();
        foreach (var source in BuildSkillSources(settings))
        {
            if (!Directory.Exists(source.Path)) continue;
            foreach (var file in Directory.GetFiles(source.Path, "SKILL.md", SearchOption.AllDirectories).Take(take))
            {
                var (name, description) = ReadSkillFrontmatter(file);
                var normalizedFile = NormalizePath(file);
                result.Add(new SkillRecord(name, description, file, source.Label, source.IsEnabled && !disabledSkills.Contains(normalizedFile), source.Path, source.IsEnabled));
            }
        }
        return result
            .OrderBy(s => s.SourceEnabled ? 0 : 1)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.IsEnabled ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    public bool AddSkillSource(string path)
    {
        var normalized = NormalizeSkillSourcePath(path);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized)) return false;
        try
        {
            if (!Directory.EnumerateFiles(normalized, "SKILL.md", SearchOption.AllDirectories).Any()) return false;
        }
        catch
        {
            return false;
        }
        var settings = LoadSkillSettings();
        if (settings.ExtraSources.Any(source => PathsEqual(source.Path, normalized))) return false;
        if (BuildBuiltInSkillSources(settings).Any(source => PathsEqual(source.Path, normalized))) return false;
        if (BuildPackageSkillSources(settings).Any(source => PathsEqual(source.Path, normalized))) return false;
        settings.ExtraSources.Add(new SkillSourceSetting(DefaultSkillSourceLabel(normalized), normalized, true));
        SaveSkillSettings(settings);
        return true;
    }

    public bool SetSkillSourceEnabled(string path, bool enabled)
    {
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var settings = LoadSkillSettings();
        var key = BuiltInSkillSourceKey(path);
        if (!string.IsNullOrWhiteSpace(key))
        {
            settings.DisabledBuiltInSources.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!enabled) settings.DisabledBuiltInSources.Add(key);
            SaveSkillSettings(settings);
            return true;
        }

        var source = settings.ExtraSources.FirstOrDefault(item => PathsEqual(item.Path, path));
        if (source is not null)
        {
            source.Enabled = enabled;
            SaveSkillSettings(settings);
            return true;
        }

        var pathKey = SkillSourcePathKey(path);
        settings.DisabledBuiltInSources.RemoveAll(item => item.Equals(pathKey, StringComparison.OrdinalIgnoreCase));
        if (!enabled) settings.DisabledBuiltInSources.Add(pathKey);
        SaveSkillSettings(settings);
        return true;
    }

    public bool SetSkillEnabled(string path, bool enabled)
    {
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var settings = LoadSkillSettings();
        settings.DisabledSkills.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (!enabled) settings.DisabledSkills.Add(path);
        SaveSkillSettings(settings);
        return true;
    }

    private IEnumerable<SkillSourceRecord> BuildSkillSources(SkillSettings settings)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in BuildBuiltInSkillSources(settings))
        {
            if (emitted.Add(NormalizePath(source.Path))) yield return source;
        }
        foreach (var source in BuildPackageSkillSources(settings))
        {
            if (emitted.Add(NormalizePath(source.Path))) yield return source;
        }
        foreach (var source in settings.ExtraSources)
        {
            var path = NormalizeSkillSourcePath(source.Path);
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (emitted.Add(NormalizePath(path))) yield return new SkillSourceRecord(string.IsNullOrWhiteSpace(source.Label) ? DefaultSkillSourceLabel(path) : source.Label, path, source.Enabled, false);
        }
    }

    private IEnumerable<SkillSourceRecord> BuildPackageSkillSources(SkillSettings settings)
    {
        var disabled = new HashSet<string>(settings.DisabledBuiltInSources, StringComparer.OrdinalIgnoreCase);
        foreach (var package in ListPackages())
        {
            if (package.Disabled || string.IsNullOrWhiteSpace(package.InstalledPath) || !Directory.Exists(package.InstalledPath)) continue;
            var packageJson = Path.Combine(package.InstalledPath, "package.json");
            if (!File.Exists(packageJson)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!doc.RootElement.TryGetProperty("pi", out var pi) || pi.ValueKind != JsonValueKind.Object) continue;
            if (!pi.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in skills.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var relative = item.GetString() ?? "";
                var absolute = Path.GetFullPath(Path.Combine(package.InstalledPath, relative));
                var sourcePath = ResolveSkillSourceRoot(absolute);
                if (string.IsNullOrWhiteSpace(sourcePath)) continue;
                var key = SkillSourcePathKey(sourcePath);
                yield return new SkillSourceRecord($"package:{package.PackageName}", sourcePath, !disabled.Contains(key), true);
            }
        }
    }

    private IEnumerable<SkillSourceRecord> BuildBuiltInSkillSources(SkillSettings settings)
    {
        var disabled = new HashSet<string>(settings.DisabledBuiltInSources, StringComparer.OrdinalIgnoreCase);
        var piPath = Path.Combine(AgentDir, "skills");
        yield return new SkillSourceRecord("pi", piPath, !disabled.Contains("pi"), true);
        if (!PathsEqual(CodexSkillDir, piPath)) yield return new SkillSourceRecord("codex", CodexSkillDir, !disabled.Contains("codex"), true);
    }

    private string BuiltInSkillSourceKey(string path)
    {
        var normalized = NormalizePath(path);
        if (PathsEqual(normalized, Path.Combine(AgentDir, "skills"))) return "pi";
        if (PathsEqual(normalized, CodexSkillDir)) return "codex";
        return "";
    }

    private static string SkillSourcePathKey(string path) => "path:" + NormalizePath(path);

    private SkillSettings LoadSkillSettings()
    {
        try
        {
            var path = SkillSettingsPath;
            if (!File.Exists(path)) return new SkillSettings();
            return JsonSerializer.Deserialize<SkillSettings>(File.ReadAllText(path), JsonOptions) ?? new SkillSettings();
        }
        catch
        {
            return new SkillSettings();
        }
    }

    private void SaveSkillSettings(SkillSettings settings)
    {
        Directory.CreateDirectory(_appDataDir);
        File.WriteAllText(SkillSettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private string SkillSettingsPath => Path.Combine(_appDataDir, "skills.json");

    private static string NormalizeSkillSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim().Trim('"');
        var skillsChild = Path.Combine(path, "skills");
        if (Directory.Exists(skillsChild)) return NormalizePath(skillsChild);
        return NormalizePath(path);
    }

    private static string ResolveSkillSourceRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, "SKILL.md"))) return NormalizePath(path);
            try
            {
                return Directory.EnumerateFiles(path, "SKILL.md", SearchOption.AllDirectories).Any() ? NormalizePath(path) : "";
            }
            catch
            {
                return "";
            }
        }
        if (File.Exists(path) && Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizePath(Path.GetDirectoryName(path) ?? "");
        }
        return "";
    }

    private static string DefaultSkillSourceLabel(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Path.GetFileName(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "");
        return string.IsNullOrWhiteSpace(parent) || leaf.Equals("skills", StringComparison.OrdinalIgnoreCase) ? $"extra:{parent}" : $"extra:{leaf}";
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private static bool PathsEqual(string? left, string? right) => NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<WorkspaceFileRecord> ListWorkspaceFiles(string rootPath, int take = 300)
    {
        if (!Directory.Exists(rootPath)) return Array.Empty<WorkspaceFileRecord>();
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".next", "dist", "build", ".cache", "coverage"
        };
        var result = new List<WorkspaceFileRecord>();
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        while (pending.Count > 0 && result.Count < take)
        {
            var dir = pending.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { continue; }
            foreach (var entry in entries.OrderBy(e => Directory.Exists(e) ? 0 : 1).ThenBy(Path.GetFileName))
            {
                if (result.Count >= take) break;
                var name = Path.GetFileName(entry);
                if (ignored.Contains(name)) continue;
                var isDir = Directory.Exists(entry);
                var rel = Path.GetRelativePath(rootPath, entry);
                var size = 0L;
                if (!isDir)
                {
                    try { size = new FileInfo(entry).Length; } catch { }
                }
                result.Add(new WorkspaceFileRecord(name, entry, rel, isDir, size));
                if (isDir) pending.Enqueue(entry);
            }
        }
        return result;
    }

    public string ReadTextPreview(string filePath, int maxBytes = 128 * 1024)
    {
        if (!File.Exists(filePath)) return "File not found.";
        if (IsSensitiveFile(filePath)) return "Preview omitted for sensitive-looking file. Use path-only attachment unless you explicitly need to inspect this secret.";
        var info = new FileInfo(filePath);
        if (info.Length > maxBytes) return $"File is too large for preview ({FormatSize(info.Length)}).";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var binaryExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".docx", ".mp3", ".wav", ".exe", ".dll" };
        if (binaryExts.Contains(ext)) return $"Binary preview unavailable for {ext} file · {FormatSize(info.Length)}";
        try { return File.ReadAllText(filePath); } catch (Exception ex) { return ex.Message; }
    }

    private static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (name.StartsWith(".env")) return true;
        if (name.Contains("mnemonic") || name.Contains("seed") || name.Contains("wallet")) return true;
        if (name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519") return true;
        return ext is ".pem" or ".key" or ".p12" or ".pfx";
    }

    public IReadOnlyList<PluginPackageRecord> ListPackages()
    {
        var result = new List<PluginPackageRecord>();
        ReadPackagesFromSettings(SettingsPath, "global", result);
        // Project package support can be added once project settings discovery is wired.
        return result;
    }

    public bool AddPackageSource(string source)
    {
        source = source.Trim();
        if (string.IsNullOrWhiteSpace(source)) return false;
        var root = ReadSettingsRoot();
        var packages = EnsurePackagesArray(root);
        if (packages.Any(node => PackageSourceFromNode(node).Equals(source, StringComparison.OrdinalIgnoreCase))) return false;
        packages.Add(source);
        WriteSettingsRoot(root);
        return true;
    }

    public bool RemovePackageSource(string source)
    {
        source = source.Trim();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(SettingsPath)) return false;
        var root = ReadSettingsRoot();
        var packages = EnsurePackagesArray(root);
        var removed = false;
        for (var i = packages.Count - 1; i >= 0; i--)
        {
            if (PackageSourceFromNode(packages[i]).Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                packages.RemoveAt(i);
                removed = true;
            }
        }
        if (removed) WriteSettingsRoot(root);
        return removed;
    }

    public bool SetPackageDisabled(string source, bool disabled)
    {
        source = source.Trim();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(SettingsPath)) return false;
        var root = ReadSettingsRoot();
        var packages = EnsurePackagesArray(root);
        var changed = false;
        for (var i = 0; i < packages.Count; i++)
        {
            if (!PackageSourceFromNode(packages[i]).Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            packages[i] = disabled ? CreateDisabledPackageNode(source) : JsonValue.Create(source);
            changed = true;
        }
        if (changed) WriteSettingsRoot(root);
        return changed;
    }

    public string NpmPackagesDir => RuntimeInfo.NpmPackagesDir;

    private JsonObject ReadSettingsRoot()
    {
        if (!File.Exists(SettingsPath) || string.IsNullOrWhiteSpace(File.ReadAllText(SettingsPath))) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject();
    }

    private void WriteSettingsRoot(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonArray EnsurePackagesArray(JsonObject root)
    {
        if (root["packages"] is JsonArray existing) return existing;
        var packages = new JsonArray();
        root["packages"] = packages;
        return packages;
    }

    private static JsonObject CreateDisabledPackageNode(string source) => new()
    {
        ["source"] = source,
        ["extensions"] = new JsonArray(),
        ["skills"] = new JsonArray(),
        ["prompts"] = new JsonArray(),
        ["themes"] = new JsonArray()
    };

    private static string PackageSourceFromNode(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var str)) return str ?? "";
        if (node is JsonObject obj && obj["source"] is JsonValue source && source.TryGetValue<string>(out var s)) return s ?? "";
        return node.ToJsonString();
    }

    public string ExportSessionMarkdown(string sessionFile)
    {
        var messages = ReadSessionMessages(sessionFile, 2000);
        var exportDir = Path.Combine(_appDataDir, "exports");
        Directory.CreateDirectory(exportDir);
        var name = Path.GetFileNameWithoutExtension(sessionFile);
        var output = Path.Combine(exportDir, $"{name}.md");
        using var writer = new StreamWriter(output, false);
        writer.WriteLine($"# ipi session export");
        writer.WriteLine();
        writer.WriteLine($"Source: `{sessionFile}`");
        writer.WriteLine();
        foreach (var message in messages)
        {
            writer.WriteLine($"## {message.Role}");
            writer.WriteLine();
            writer.WriteLine(message.Text);
            writer.WriteLine();
        }
        return output;
    }

    public long? ReadContextWindow(string provider, string model)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return null;
        var fileName = $"{provider}.models.js";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent", "node_modules", "@earendil-works", "pi-ai", "dist", "providers", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-ai", "dist", "providers", fileName),
            Path.Combine(Environment.CurrentDirectory, "pi-web", "node_modules", "@earendil-works", "pi-coding-agent", "node_modules", "@earendil-works", "pi-ai", "dist", "providers", fileName),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                var marker = $"\"{model}\"";
                var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                var nextModel = text.IndexOf("\n    \"", start + marker.Length, StringComparison.OrdinalIgnoreCase);
                var length = nextModel > start ? nextModel - start : Math.Min(3000, text.Length - start);
                var body = text.Substring(start, length);
                var context = Regex.Match(body, @"contextWindow\s*:\s*(\d+)");
                if (context.Success && long.TryParse(context.Groups[1].Value, out var value)) return value;
            }
            catch { }
        }

        return null;
    }

    public (string DefaultProvider, string DefaultModel, string DefaultThinking) ReadSettingsSummary()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ("unknown", "unknown", "unknown");
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            return (
                TryGetString(root, "defaultProvider", out var provider) ? provider : "unknown",
                TryGetString(root, "defaultModel", out var model) ? model : "unknown",
                TryGetString(root, "defaultThinkingLevel", out var thinking) ? thinking : "unknown"
            );
        }
        catch { return ("unknown", "unknown", "unknown"); }
    }

    public IReadOnlyList<PiModelOptionRecord> ReadModelOptions((string DefaultProvider, string DefaultModel, string DefaultThinking) settings)
    {
        var results = new List<PiModelOptionRecord>();
        void Add(string provider, string model, string displayName, string source, bool configured)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return;
            if (provider == "unknown" || model == "unknown") return;
            if (results.Any(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(model, StringComparison.OrdinalIgnoreCase))) return;
            results.Add(new PiModelOptionRecord(provider, model, string.IsNullOrWhiteSpace(displayName) ? model : displayName, source, configured));
        }

        Add(settings.DefaultProvider, settings.DefaultModel, settings.DefaultModel, "settings.json", true);

        try
        {
            if (!File.Exists(ModelsPath)) return results;
            using var doc = JsonDocument.Parse(File.ReadAllText(ModelsPath));
            if (!doc.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object) return results;
            foreach (var providerProperty in providers.EnumerateObject())
            {
                var provider = providerProperty.Name;
                var providerElement = providerProperty.Value;
                if (providerElement.ValueKind != JsonValueKind.Object) continue;
                if (!providerElement.TryGetProperty("models", out var models)) continue;
                ReadProviderModels(provider, models, Add);
            }
        }
        catch { }

        return results
            .OrderByDescending(item => item.Provider.Equals(settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(settings.DefaultModel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadProviderModels(string provider, JsonElement models, Action<string, string, string, string, bool> add)
    {
        if (models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind == JsonValueKind.String) add(provider, model.GetString() ?? "", model.GetString() ?? "", "models.json", true);
                else if (model.ValueKind == JsonValueKind.Object)
                {
                    var id = TryGetString(model, "id", out var i) ? i : TryGetString(model, "model", out var m) ? m : "";
                    var name = TryGetString(model, "name", out var n) ? n : id;
                    add(provider, id, name, "models.json", true);
                }
            }
        }
        else if (models.ValueKind == JsonValueKind.Object)
        {
            foreach (var modelProperty in models.EnumerateObject())
            {
                var id = modelProperty.Name;
                var name = id;
                if (modelProperty.Value.ValueKind == JsonValueKind.Object && TryGetString(modelProperty.Value, "name", out var n)) name = n;
                add(provider, id, name, "models.json", true);
            }
        }
    }

    private static bool LooksLikeTimelineLine(string line)
    {
        return line.Contains("\"type\":\"compaction\"") ||
               line.Contains("\"type\": \"compaction\"") ||
               line.Contains("\"role\":\"user\"") ||
               line.Contains("\"role\": \"user\"") ||
               line.Contains("\"role\":\"assistant\"") ||
               line.Contains("\"role\": \"assistant\"");
    }

    private static string TrimText(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..max] + "…";
    }

    private static PiMessageRecord? ParseMessageLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message)) return null;
            var id = TryGetString(root, "id", out var entryId) ? entryId : Guid.NewGuid().ToString("N");
            var role = TryGetString(message, "role", out var r) ? r : "unknown";
            var text = ExtractMessageText(message);
            return new PiMessageRecord(role, text, id, ReadEntryTimestamp(root));
        }
        catch { return null; }
    }

    private static string ExtractMessageText(JsonElement message) => ExtractMessageContent(message, "").Text;

    private static (string Text, IReadOnlyList<PiTimelineAttachmentRecord> Attachments) ExtractMessageContent(JsonElement message, string entryId)
    {
        if (!message.TryGetProperty("content", out var content)) return ("", Array.Empty<PiTimelineAttachmentRecord>());
        if (content.ValueKind == JsonValueKind.String)
        {
            var parsed = ExtractLocalFileAttachments(content.GetString() ?? "");
            return (parsed.Text, parsed.Attachments);
        }
        if (content.ValueKind != JsonValueKind.Array) return (content.ToString(), Array.Empty<PiTimelineAttachmentRecord>());

        var parts = new List<string>();
        var attachments = new List<PiTimelineAttachmentRecord>();
        var imageIndex = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (!TryGetString(block, "type", out var type)) continue;
            if (type == "text" && TryGetString(block, "text", out var text))
            {
                var parsed = ExtractLocalFileAttachments(text);
                if (!string.IsNullOrWhiteSpace(parsed.Text)) parts.Add(parsed.Text);
                attachments.AddRange(parsed.Attachments);
            }
            else if (type == "thinking" && TryGetString(block, "thinking", out var thinking)) parts.Add($"[thinking] {thinking}");
            else if (type == "toolCall")
            {
                var name = TryGetString(block, "name", out var n) ? n : TryGetString(block, "toolName", out var tn) ? tn : "tool";
                parts.Add($"[tool call] {name}");
            }
            else if (type == "image")
            {
                var attachment = TryCreateImageAttachment(block, entryId, imageIndex++);
                if (attachment is not null) attachments.Add(attachment);
            }
        }
        return (string.Join("\n\n", parts).Trim(), attachments);
    }

    private static (string Text, IReadOnlyList<PiTimelineAttachmentRecord> Attachments) ExtractLocalFileAttachments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (text, Array.Empty<PiTimelineAttachmentRecord>());
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var match = Regex.Match(normalized, @"(?im)^Attached local files:\s*$");
        if (!match.Success) return (text, Array.Empty<PiTimelineAttachmentRecord>());

        var visibleText = Regex.Replace(normalized[..match.Index], @"(?im)^\[image attachment\]\s*$", "").TrimEnd();
        var tail = normalized[(match.Index + match.Length)..];
        var attachments = new List<PiTimelineAttachmentRecord>();
        foreach (Match pathMatch in Regex.Matches(tail, @"(?m)^-\s+(.+?)\s*$"))
        {
            var rawPath = pathMatch.Groups[1].Value.Trim();
            var path = NormalizeImagePath(rawPath);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            attachments.Add(new PiTimelineAttachmentRecord(info.Name, path, FormatSize(info.Length)));
        }
        return (visibleText, attachments);
    }

    private static PiTimelineAttachmentRecord? TryCreateImageAttachment(JsonElement block, string entryId, int index)
    {
        var path = TryGetImagePath(block);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var info = new FileInfo(path);
            return new PiTimelineAttachmentRecord(info.Name, path, FormatSize(info.Length));
        }

        var data = TryGetImageData(block, out var mimeType);
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            var bytes = Convert.FromBase64String(data.Contains(',') ? data[(data.IndexOf(',') + 1)..] : data);
            var ext = MimeExtension(mimeType);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var safeEntry = string.IsNullOrWhiteSpace(entryId) ? "image" : Regex.Replace(entryId, "[^a-zA-Z0-9_-]", "");
            var dir = Path.Combine(IpiPathService.LocalAppDataDir, "session-images");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{safeEntry}-{index}-{hash}{ext}");
            if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
            return new PiTimelineAttachmentRecord(Path.GetFileName(file), file, FormatSize(bytes.Length));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetImagePath(JsonElement block)
    {
        foreach (var key in new[] { "path", "filePath", "file", "localPath" })
        {
            if (TryGetString(block, key, out var value) && !string.IsNullOrWhiteSpace(value)) return NormalizeImagePath(value);
        }
        if (block.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "path", "filePath", "file", "url" })
            {
                if (TryGetString(source, key, out var value) && !string.IsNullOrWhiteSpace(value)) return NormalizeImagePath(value);
            }
        }
        return null;
    }

    private static string NormalizeImagePath(string value)
    {
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(value).LocalPath; } catch { }
        }
        return Environment.ExpandEnvironmentVariables(value);
    }

    private static string? TryGetImageData(JsonElement block, out string mimeType)
    {
        mimeType = "image/png";
        if (TryGetString(block, "mimeType", out var mt) && !string.IsNullOrWhiteSpace(mt)) mimeType = mt;
        if (TryGetString(block, "media_type", out var mediaType) && !string.IsNullOrWhiteSpace(mediaType)) mimeType = mediaType;
        if (TryGetString(block, "data", out var data) && !string.IsNullOrWhiteSpace(data)) return data;
        if (block.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(source, "mimeType", out var sourceMt) && !string.IsNullOrWhiteSpace(sourceMt)) mimeType = sourceMt;
            if (TryGetString(source, "media_type", out var sourceMediaType) && !string.IsNullOrWhiteSpace(sourceMediaType)) mimeType = sourceMediaType;
            if (TryGetString(source, "data", out var sourceData) && !string.IsNullOrWhiteSpace(sourceData)) return sourceData;
        }
        return null;
    }

    private static string MimeExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private static DateTime? ReadEntryTimestamp(JsonElement root)
    {
        if (TryGetString(root, "timestamp", out var ts) && DateTime.TryParse(ts, out var dt)) return dt;
        return null;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = "";
        if (!element.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        value = prop.ToString();
        return true;
    }

    private const int ToolResultPreviewLimit = 6000;

    private sealed record ToolResultInfo(string Text, DateTime? Timestamp, bool IsError);
    private sealed record TimelineWindowItem(PiTimelineRecord Record, string? ToolCallId = null);

    private static Dictionary<string, ToolResultInfo> ReadToolResults(string filePath)
    {
        var results = new Dictionary<string, ToolResultInfo>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"role\":\"toolResult\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message)) continue;
                if (!TryGetString(message, "toolCallId", out var id) || string.IsNullOrWhiteSpace(id)) continue;
                var text = TrimForPreview(ExtractMessageText(message), ToolResultPreviewLimit);
                var isError = message.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                results[id] = new ToolResultInfo(text, ReadEntryTimestamp(root) ?? ReadEntryTimestamp(message), isError);
            }
            catch { }
        }
        return results;
    }

    private static Dictionary<string, ToolResultInfo> ReadSelectedToolResults(string filePath, HashSet<string> ids)
    {
        var results = new Dictionary<string, ToolResultInfo>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return results;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"role\":\"toolResult\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message)) continue;
                if (!TryGetString(message, "toolCallId", out var id) || string.IsNullOrWhiteSpace(id) || !ids.Contains(id)) continue;
                var text = TrimForPreview(ExtractMessageText(message), ToolResultPreviewLimit);
                var isError = message.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                results[id] = new ToolResultInfo(text, ReadEntryTimestamp(root) ?? ReadEntryTimestamp(message), isError);
                if (results.Count == ids.Count) break;
            }
            catch { }
        }
        return results;
    }

    private static string TrimForPreview(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit) return text;
        return text[..limit] + "\n…";
    }

    private static string BuildToolPreview(string toolName, string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            var root = doc.RootElement;
            foreach (var key in new[] { "path", "filePath", "command", "pattern", "oldText", "newText" })
            {
                if (TryGetString(root, key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
                    return oneLine.Length > 110 ? $"{toolName} {oneLine[..110]}…" : $"{toolName} {oneLine}";
                }
            }
        }
        catch { }
        var compact = args.Replace("\r", " ").Replace("\n", " ").Trim();
        if (compact.Length > 110) compact = compact[..110] + "…";
        return string.IsNullOrWhiteSpace(compact) ? toolName : $"{toolName} {compact}";
    }

    private static string FormatUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return "";
        var parts = new List<string>();
        if (TryGetNumber(usage, "input", out var input) && input > 0) parts.Add($"{input:N0} in");
        if (TryGetNumber(usage, "output", out var output) && output > 0) parts.Add($"{output:N0} out");
        if (TryGetNumber(usage, "cacheRead", out var cache) && cache > 0) parts.Add($"{cache:N0} cache");
        if (usage.TryGetProperty("cost", out var cost) && TryGetDouble(cost, "total", out var total) && total > 0) parts.Add($"${total:F4}");
        return string.Join(" · ", parts);
    }

    private static string UsageDetail(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return "";
        var lines = new List<string>();
        foreach (var key in new[] { "input", "output", "reasoning", "cacheRead", "cacheWrite", "totalTokens" })
        {
            if (TryGetNumber(usage, key, out var value)) lines.Add($"{key}: {value:N0}");
        }
        if (usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            {
                if (TryGetDouble(cost, key, out var value)) lines.Add($"cost.{key}: ${value:F6}");
            }
        }
        return string.Join("\n", lines);
    }

    private static bool TryGetNumber(JsonElement element, string property, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value)) return true;
        if (long.TryParse(prop.ToString(), out value)) return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out value)) return true;
        if (double.TryParse(prop.ToString(), out value)) return true;
        return false;
    }

    private void ReadPackagesFromSettings(string settingsPath, string scope, List<PluginPackageRecord> result)
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array) return;
            foreach (var item in packages.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var source = item.GetString() ?? "";
                    result.Add(ResolvePackageRecord(source, scope, false));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var source = TryGetString(item, "source", out var s) ? s : item.ToString();
                    var disabled = false;
                    if (item.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Array && ext.GetArrayLength() == 0 &&
                        item.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array && skills.GetArrayLength() == 0 &&
                        item.TryGetProperty("prompts", out var prompts) && prompts.ValueKind == JsonValueKind.Array && prompts.GetArrayLength() == 0 &&
                        item.TryGetProperty("themes", out var themes) && themes.ValueKind == JsonValueKind.Array && themes.GetArrayLength() == 0) disabled = true;
                    result.Add(ResolvePackageRecord(source, scope, disabled));
                }
            }
        }
        catch { }
    }

    private PluginPackageRecord ResolvePackageRecord(string source, string scope, bool disabled)
    {
        var packageName = PackageNameFromSource(source);
        var installedPath = ResolvePackageInstallPath(source, packageName);
        var version = "";
        var resources = new List<PluginResourceRecord>();

        var packageJson = string.IsNullOrWhiteSpace(installedPath) ? "" : Path.Combine(installedPath, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                var root = doc.RootElement;
                if (TryGetString(root, "name", out var name) && !string.IsNullOrWhiteSpace(name)) packageName = name;
                if (TryGetString(root, "version", out var v)) version = v;
                if (root.TryGetProperty("pi", out var pi) && pi.ValueKind == JsonValueKind.Object)
                {
                    AddResourcesFromPiArray(pi, "extensions", "extension", installedPath, resources);
                    AddResourcesFromPiArray(pi, "skills", "skill", installedPath, resources);
                    AddResourcesFromPiArray(pi, "prompts", "prompt", installedPath, resources);
                    AddResourcesFromPiArray(pi, "themes", "theme", installedPath, resources);
                }
            }
            catch { }
        }

        return new PluginPackageRecord(source, scope, disabled, string.IsNullOrWhiteSpace(packageName) ? source : packageName, version, installedPath, resources);
    }

    private string ResolvePackageInstallPath(string source, string packageName)
    {
        if (Directory.Exists(source)) return source;
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = source[5..];
            if (Directory.Exists(filePath)) return filePath;
        }
        if (!source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) && Directory.Exists(packageName)) return packageName;
        if (string.IsNullOrWhiteSpace(packageName)) return "";
        var parts = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = parts.Length == 2 && packageName.StartsWith('@')
            ? Path.Combine(AgentDir, "npm", "node_modules", parts[0], parts[1])
            : Path.Combine(AgentDir, "npm", "node_modules", packageName);
        return Directory.Exists(path) ? path : path;
    }

    private static string PackageNameFromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase)) return source[4..].Trim();
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(source[5..].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Directory.Exists(source)) return Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return source;
    }

    private static void AddResourcesFromPiArray(JsonElement pi, string property, string kind, string packageRoot, List<PluginResourceRecord> resources)
    {
        if (!pi.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var relative = item.GetString() ?? "";
            var path = Path.GetFullPath(Path.Combine(packageRoot, relative));
            if (Directory.Exists(path))
            {
                if (kind == "skill")
                {
                    foreach (var skillDir in Directory.GetDirectories(path).OrderBy(Path.GetFileName))
                    {
                        var skillPath = Path.Combine(skillDir, "SKILL.md");
                        if (File.Exists(skillPath)) resources.Add(new PluginResourceRecord(kind, Path.GetFileName(skillDir), RelativeToPackage(packageRoot, skillPath)));
                    }
                }
                else
                {
                    foreach (var file in Directory.GetFiles(path).OrderBy(Path.GetFileName)) resources.Add(new PluginResourceRecord(kind, Path.GetFileNameWithoutExtension(file), RelativeToPackage(packageRoot, file)));
                }
            }
            else if (File.Exists(path))
            {
                resources.Add(new PluginResourceRecord(kind, Path.GetFileNameWithoutExtension(path), RelativeToPackage(packageRoot, path)));
            }
        }
    }

    private static string RelativeToPackage(string packageRoot, string path)
    {
        try { return Path.GetRelativePath(packageRoot, path); }
        catch { return path; }
    }

    private static (string Name, string Description) ReadSkillFrontmatter(string file)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(file)) ?? "skill";
        var description = "";
        try
        {
            var lines = File.ReadLines(file).Take(180).ToList();
            var bodyStart = 0;
            if (lines.Count > 0 && lines[0].Trim().Equals("---", StringComparison.Ordinal))
            {
                var frontmatter = new List<string>();
                for (var i = 1; i < lines.Count; i++)
                {
                    if (lines[i].Trim().Equals("---", StringComparison.Ordinal))
                    {
                        bodyStart = i + 1;
                        break;
                    }
                    frontmatter.Add(lines[i]);
                }

                for (var i = 0; i < frontmatter.Count; i++)
                {
                    var line = frontmatter[i].Trim();
                    if (YamlKey(line, "name")) name = CleanYamlScalar(line[5..]);
                    if (!YamlKey(line, "description")) continue;

                    var value = line[12..].Trim();
                    if (IsYamlBlockScalar(value))
                    {
                        var block = new List<string>();
                        for (var j = i + 1; j < frontmatter.Count; j++)
                        {
                            var raw = frontmatter[j];
                            var trimmed = raw.Trim();
                            if (trimmed.Length > 0 && !char.IsWhiteSpace(raw[0]) && Regex.IsMatch(trimmed, "^[A-Za-z0-9_-]+\\s*:")) break;
                            block.Add(trimmed);
                            i = j;
                        }
                        description = NormalizeSkillDescription(string.Join(" ", block));
                    }
                    else
                    {
                        description = CleanYamlScalar(value);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(description) || description is "|" or ">")
            {
                description = ExtractSkillBodySummary(lines.Skip(bodyStart));
            }
        }
        catch { }
        return (name, description);
    }

    private static bool YamlKey(string line, string key) => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase);

    private static bool IsYamlBlockScalar(string value) => value.StartsWith("|", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal);

    private static string CleanYamlScalar(string value) => NormalizeSkillDescription(value.Trim().Trim('"', '\''));

    private static string NormalizeSkillDescription(string value) => Regex.Replace(value, "\\s+", " ").Trim().Trim('"', '\'');

    private static string ExtractSkillBodySummary(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith(">", StringComparison.Ordinal)) line = line.TrimStart('>').Trim();
            if (line.Length > 0) return NormalizeSkillDescription(line);
        }
        return "";
    }

    private static string DecodeSessionCwdFromFolder(string sessionFile)
    {
        try
        {
            var folder = Directory.GetParent(sessionFile)?.Name ?? "";
            if (!folder.StartsWith("--") || !folder.EndsWith("--")) return "";
            var body = folder[2..^2];
            var marker = body.IndexOf("--", StringComparison.Ordinal);
            if (marker <= 0) return "";
            var drive = body[..marker];
            var encoded = body[(marker + 2)..];
            if (drive.Length != 1 || encoded.Length == 0) return "";

            var root = drive + @":\";
            if (!Directory.Exists(root)) return "";
            var parts = encoded.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var index = 0;
            while (index < parts.Length)
            {
                string? match = null;
                var matchEnd = index;
                for (var end = parts.Length; end > index; end--)
                {
                    var candidateName = string.Join('-', parts[index..end]);
                    var candidate = Path.Combine(current, candidateName);
                    if (Directory.Exists(candidate))
                    {
                        match = candidate;
                        matchEnd = end;
                        break;
                    }
                }
                if (match is null) return "";
                current = match;
                index = matchEnd;
            }
            return current;
        }
        catch { return ""; }
    }

    private static string MakeSessionTitle(string firstMessage, string cwd)
    {
        if (!string.IsNullOrWhiteSpace(firstMessage) && firstMessage != "(no messages)")
        {
            var oneLine = firstMessage.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length > 56 ? oneLine[..56] + "…" : oneLine;
        }
        var dir = string.IsNullOrWhiteSpace(cwd) ? "session" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(dir) ? "session" : dir;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    private sealed class SkillSettings
    {
        public List<SkillSourceSetting> ExtraSources { get; set; } = new();
        public List<string> DisabledBuiltInSources { get; set; } = new();
        public List<string> DisabledSkills { get; set; } = new();
    }

    private sealed class SkillSourceSetting
    {
        public SkillSourceSetting() { }
        public SkillSourceSetting(string label, string path, bool enabled)
        {
            Label = label;
            Path = path;
            Enabled = enabled;
        }

        public string Label { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}
