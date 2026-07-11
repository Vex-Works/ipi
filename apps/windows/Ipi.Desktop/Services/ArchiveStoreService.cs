using System.IO;
using System.Text;
using System.Text.Json;
using Ipi.Desktop.Models;

namespace Ipi.Desktop.Services;

public sealed class ArchiveStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _archivePath;

    public ArchiveStoreService()
    {
        var dir = IpiPathService.AppDataDir;
        Directory.CreateDirectory(dir);
        _archivePath = Path.Combine(dir, "archive.json");
    }

    public event Action? Changed;

    public string ArchivePath => _archivePath;

    public IReadOnlyList<ArchivedSessionRecord> ListArchived()
        => LoadState().Sessions
            .OrderByDescending(session => session.ArchivedAt)
            .ThenByDescending(session => session.Modified)
            .ToList();

    public ISet<string> ArchivedSessionPaths()
        => new HashSet<string>(LoadState().Sessions.Select(session => NormalizePath(session.FilePath)), StringComparer.OrdinalIgnoreCase);

    public bool IsArchived(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath) && ArchivedSessionPaths().Contains(NormalizePath(filePath));

    public void Archive(PiSessionRecord session)
    {
        var state = LoadState();
        var normalizedPath = NormalizePath(session.FilePath);
        state.Sessions.RemoveAll(item => NormalizePath(item.FilePath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        state.Sessions.Add(new ArchivedSessionRecord(
            session.Id,
            session.FilePath,
            session.Cwd,
            session.Title,
            session.Created,
            session.Modified,
            session.MessageCount,
            session.FirstMessage,
            DateTime.Now));
        SaveState(state);
    }

    public void Restore(string filePath)
    {
        Remove(filePath);
    }

    public void RestoreAll()
    {
        var state = LoadState();
        if (state.Sessions.Count == 0) return;
        state.Sessions.Clear();
        SaveState(state);
    }

    public ArchiveDeleteResult DeleteArchivedSession(string filePath)
    {
        var state = LoadState();
        var normalizedPath = NormalizePath(filePath);
        var item = state.Sessions.FirstOrDefault(session => NormalizePath(session.FilePath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new ArchiveDeleteResult(0, 0, Array.Empty<string>());

        try
        {
            var deletedFiles = 0;
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
                deletedFiles = 1;
            }
            var removed = state.Sessions.RemoveAll(session => NormalizePath(session.FilePath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveState(state);
            return new ArchiveDeleteResult(removed, deletedFiles, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new ArchiveDeleteResult(0, 0, new[] { $"{item.FilePath}: {ex.Message}" });
        }
    }

    public ArchiveDeleteResult DeleteAllArchivedSessions()
    {
        var state = LoadState();
        if (state.Sessions.Count == 0) return new ArchiveDeleteResult(0, 0, Array.Empty<string>());

        var remaining = new List<ArchivedSessionRecord>();
        var errors = new List<string>();
        var removedRecords = 0;
        var deletedFiles = 0;

        foreach (var item in state.Sessions)
        {
            try
            {
                if (File.Exists(item.FilePath))
                {
                    File.Delete(item.FilePath);
                    deletedFiles++;
                }
                removedRecords++;
            }
            catch (Exception ex)
            {
                remaining.Add(item);
                errors.Add($"{item.FilePath}: {ex.Message}");
            }
        }

        if (removedRecords > 0 || remaining.Count != state.Sessions.Count)
        {
            state.Sessions.Clear();
            state.Sessions.AddRange(remaining);
            SaveState(state);
        }

        return new ArchiveDeleteResult(removedRecords, deletedFiles, errors);
    }

    public void RemoveMissing()
    {
        var state = LoadState();
        var originalCount = state.Sessions.Count;
        state.Sessions.RemoveAll(item => !File.Exists(item.FilePath));
        if (state.Sessions.Count != originalCount) SaveState(state);
    }

    public void Remove(string filePath)
    {
        var state = LoadState();
        var normalizedPath = NormalizePath(filePath);
        var removed = state.Sessions.RemoveAll(item => NormalizePath(item.FilePath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) SaveState(state);
    }

    private ArchiveFileState LoadState()
    {
        try
        {
            if (!File.Exists(_archivePath)) return new ArchiveFileState(new List<ArchivedSessionRecord>());
            var state = JsonSerializer.Deserialize<ArchiveFileState>(File.ReadAllText(_archivePath), JsonOptions);
            return state ?? new ArchiveFileState(new List<ArchivedSessionRecord>());
        }
        catch
        {
            return new ArchiveFileState(new List<ArchivedSessionRecord>());
        }
    }

    private void SaveState(ArchiveFileState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
        WriteTextAtomically(_archivePath, JsonSerializer.Serialize(state, JsonOptions));
        Changed?.Invoke();
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

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar); }
    }

    private sealed record ArchiveFileState(List<ArchivedSessionRecord> Sessions);
}

public sealed record ArchivedSessionRecord(
    string Id,
    string FilePath,
    string Cwd,
    string Title,
    DateTime Created,
    DateTime Modified,
    int MessageCount,
    string FirstMessage,
    DateTime ArchivedAt);

public sealed record ArchiveDeleteResult(int RemovedRecords, int DeletedFiles, IReadOnlyList<string> Errors);
