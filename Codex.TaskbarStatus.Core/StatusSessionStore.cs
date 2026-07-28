using System.Security.Cryptography;
using System.Text;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Stores the latest hook snapshot for each Codex session. Keeping these
/// snapshots separate prevents simultaneous tasks from overwriting each
/// other's permission or input-required state.
/// </summary>
public sealed class StatusSessionStore
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTaskbarStatus",
        "sessions");

    public StatusSessionStore(string? directoryPath = null)
    {
        DirectoryPath = Path.GetFullPath(directoryPath ?? DefaultDirectory);
    }

    public string DirectoryPath { get; }

    public StatusFileStore GetStore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var normalized = sessionId.Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var fileName = $"{Convert.ToHexString(digest)}.json";
        return new StatusFileStore(Path.Combine(DirectoryPath, fileName));
    }

    public IReadOnlyList<CodexExecutionState> ReadRecent(int limit = 32)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (limit == 0 || !Directory.Exists(DirectoryPath))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    try
                    {
                        return (Path: path, LastWriteTimeUtc: File.GetLastWriteTimeUtc(path));
                    }
                    catch (IOException)
                    {
                        return (Path: string.Empty, LastWriteTimeUtc: DateTime.MinValue);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return (Path: string.Empty, LastWriteTimeUtc: DateTime.MinValue);
                    }
                })
                .Where(item => item.Path.Length > 0)
                .OrderByDescending(item => item.LastWriteTimeUtc)
                .Take(limit)
                .Select(item => new StatusFileStore(item.Path).Read())
                .Where(state => !string.IsNullOrWhiteSpace(state.SessionId))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
