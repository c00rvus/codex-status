using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

public sealed class StatusFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTaskbarStatus",
        "status.json");

    public StatusFileStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? DefaultPath);
    }

    public string FilePath { get; }

    public CodexExecutionState Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new CodexExecutionState();
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return Normalize(JsonSerializer.Deserialize<CodexExecutionState>(json, SerializerOptions));
        }
        catch (IOException)
        {
            return new CodexExecutionState();
        }
        catch (UnauthorizedAccessException)
        {
            return new CodexExecutionState();
        }
        catch (JsonException)
        {
            return new CodexExecutionState();
        }
    }

    public async Task<CodexExecutionState> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new CodexExecutionState();
            }

            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var state = await JsonSerializer.DeserializeAsync<CodexExecutionState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            return Normalize(state);
        }
        catch (IOException)
        {
            return new CodexExecutionState();
        }
        catch (UnauthorizedAccessException)
        {
            return new CodexExecutionState();
        }
        catch (JsonException)
        {
            return new CodexExecutionState();
        }
    }

    public async Task WriteAsync(
        CodexExecutionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The status file must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(state),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A stale temporary file is harmless; the canonical file remains atomic.
            }
            catch (UnauthorizedAccessException)
            {
                // A stale temporary file is harmless; the canonical file remains atomic.
            }
        }
    }

    public Task<CodexExecutionState> UpdateAsync(
        Func<CodexExecutionState, CodexExecutionState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        using var mutex = new Mutex(initiallyOwned: false, BuildMutexName(FilePath));
        var hasMutex = false;
        try
        {
            try
            {
                hasMutex = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                hasMutex = true;
            }

            if (!hasMutex)
            {
                throw new TimeoutException("Timed out waiting to update the Codex status file.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var updated = Normalize(update(Read()));
            WriteAsync(updated, cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(updated);
        }
        finally
        {
            if (hasMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static CodexExecutionState Normalize(CodexExecutionState? state)
    {
        state ??= new CodexExecutionState();
        state.FilesChanged ??= [];
        state.Status = string.IsNullOrWhiteSpace(state.Status)
            ? CodexExecutionStatuses.Idle
            : state.Status;
        state.Activity = string.IsNullOrWhiteSpace(state.Activity)
            ? "Aguardando"
            : state.Activity;
        return state;
    }

    private static string BuildMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return $"CodexTaskbarStatus-{Convert.ToHexString(digest)}";
    }
}
