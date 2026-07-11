namespace Ipi.Desktop.Models;

public sealed record PiSessionRecord(
    string Id,
    string FilePath,
    string Cwd,
    string Title,
    DateTime Created,
    DateTime Modified,
    int MessageCount,
    string FirstMessage,
    string? ParentSessionPath
);

public sealed record PiMessageRecord(
    string Role,
    string Text,
    string EntryId,
    DateTime? Timestamp,
    string Kind = "message"
);

public sealed record PiTimelineAttachmentRecord(string Name, string Path, string Detail);

public sealed record PiTimelineMarkerRecord(int TimelineIndex, string Title, string EntryId, string? ParentId, DateTime? Timestamp);

public sealed record PiTimelineRecord(
    string Badge,
    string Title,
    string Detail,
    string Kind,
    DateTime? Timestamp = null,
    string EntryId = "",
    string? ParentId = null,
    IReadOnlyList<PiTimelineAttachmentRecord>? Attachments = null
);

public sealed record PiModelOptionRecord(
    string Provider,
    string Model,
    string DisplayName,
    string Source,
    bool IsConfigured,
    string ProviderDisplayName = ""
);

public sealed record PiProviderCatalogRecord(
    string Provider,
    string DisplayName,
    string Api,
    string BaseUrl,
    int ModelCount,
    bool IsConfigured
);

public sealed record SessionUsageSummary(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long TotalTokens,
    double Cost
);

public sealed record SessionInspectionSummary(
    string FilePath,
    string Id,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolResults,
    int TotalMessages,
    SessionUsageSummary Usage
);

public sealed record SkillRecord(
    string Name,
    string Description,
    string Path,
    string Source,
    bool IsEnabled = true,
    string SourcePath = "",
    bool SourceEnabled = true
);

public sealed record SkillSourceRecord(
    string Label,
    string Path,
    bool IsEnabled,
    bool IsBuiltIn
);

public sealed record WorkspaceFileRecord(
    string Name,
    string Path,
    string RelativePath,
    bool IsDirectory,
    long Size
);

public sealed record PluginResourceRecord(
    string Kind,
    string Name,
    string Path
);

public sealed record PluginPackageRecord(
    string Source,
    string Scope,
    string Status,
    bool Disabled,
    string PackageName,
    string Version,
    string InstalledPath,
    IReadOnlyList<PluginResourceRecord> Resources
);
