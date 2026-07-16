using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexDesktopUnreadThreadReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"CodexUnreadThreads-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_directory, ".codex-global-state.json");

    [Fact]
    public void Read_MissingFileReturnsUnavailableState()
    {
        var state = new CodexDesktopUnreadThreadReader(StatePath).Read();

        Assert.False(state.IsAvailable);
        Assert.Empty(state.ThreadIds);
    }

    [Fact]
    public async Task Read_ReturnsDistinctLocalUnreadThreadIdsAsImmutableSet()
    {
        await WriteStateAsync(
            """
            {
              "electron-persisted-atom-state": {
                "unread-thread-ids-by-host-v1": {
                  "local": ["thread-a", "thread-b", "thread-a", ""]
                }
              }
            }
            """);

        var state = new CodexDesktopUnreadThreadReader(StatePath).Read();

        Assert.True(state.IsAvailable);
        Assert.Equal(2, state.ThreadIds.Count);
        Assert.Contains("thread-a", state.ThreadIds);
        Assert.Contains("thread-b", state.ThreadIds);
        var mutableView = Assert.IsAssignableFrom<ISet<string>>(state.ThreadIds);
        Assert.Throws<NotSupportedException>(() => mutableView.Add("thread-c"));
        Assert.DoesNotContain("thread-c", state.ThreadIds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"electron-persisted-atom-state\":{}}")]
    [InlineData("{\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":{}}}")]
    [InlineData("{\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":{\"local\":null}}}")]
    public async Task Read_MissingStatePathReturnsUnavailableState(string json)
    {
        await WriteStateAsync(json);

        var state = new CodexDesktopUnreadThreadReader(StatePath).Read();

        Assert.False(state.IsAvailable);
        Assert.Empty(state.ThreadIds);
    }

    [Fact]
    public async Task Read_EmptyLocalArrayIsAvailable()
    {
        await WriteStateAsync(ValidStateJson());

        var state = new CodexDesktopUnreadThreadReader(StatePath).Read();

        Assert.True(state.IsAvailable);
        Assert.Empty(state.ThreadIds);
    }

    [Fact]
    public async Task Read_UsesLengthAndLastWriteTimeCache()
    {
        var timestamp = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var firstJson = ValidStateJson("thread-a");
        var secondJson = firstJson.Replace("thread-a", "thread-b", StringComparison.Ordinal);
        Assert.Equal(firstJson.Length, secondJson.Length);

        await WriteStateAsync(firstJson, timestamp);
        var reader = new CodexDesktopUnreadThreadReader(StatePath);
        Assert.Contains("thread-a", reader.Read().ThreadIds);

        await WriteStateAsync(secondJson, timestamp);
        var cached = reader.Read();

        Assert.Contains("thread-a", cached.ThreadIds);
        Assert.DoesNotContain("thread-b", cached.ThreadIds);
    }

    [Fact]
    public async Task Read_RetainsLastGoodStateDuringTransientInvalidJson()
    {
        await WriteStateAsync(
            ValidStateJson("thread-a"),
            new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc));
        var reader = new CodexDesktopUnreadThreadReader(StatePath);
        var first = reader.Read();

        await WriteStateAsync(
            "{invalid-json",
            new DateTime(2026, 7, 15, 12, 1, 0, DateTimeKind.Utc));
        var duringInvalidWrite = reader.Read();

        Assert.Same(first, duringInvalidWrite);

        await WriteStateAsync(
            ValidStateJson("thread-b"),
            new DateTime(2026, 7, 15, 12, 2, 0, DateTimeKind.Utc));
        var recovered = reader.Read();

        Assert.Contains("thread-b", recovered.ThreadIds);
        Assert.DoesNotContain("thread-a", recovered.ThreadIds);
    }

    [Fact]
    public async Task Read_CanReadWhileDesktopKeepsStateFileOpen()
    {
        await WriteStateAsync(ValidStateJson("thread-a"));
        var reader = new CodexDesktopUnreadThreadReader(StatePath);

        await using var desktopWriter = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var state = reader.Read();

        Assert.True(state.IsAvailable);
        Assert.Contains("thread-a", state.ThreadIds);
    }

    [Fact]
    public async Task Read_NonStringThreadIdReturnsUnavailableState()
    {
        await WriteStateAsync(
            """
            {
              "electron-persisted-atom-state": {
                "unread-thread-ids-by-host-v1": {
                  "local": ["thread-a", 42]
                }
              }
            }
            """);

        var state = new CodexDesktopUnreadThreadReader(StatePath).Read();

        Assert.False(state.IsAvailable);
        Assert.Empty(state.ThreadIds);
    }

    private async Task WriteStateAsync(string json, DateTime? lastWriteTimeUtc = null)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StatePath, json);
        if (lastWriteTimeUtc is not null)
        {
            File.SetLastWriteTimeUtc(StatePath, lastWriteTimeUtc.Value);
        }
    }

    private static string ValidStateJson(params string[] threadIds)
    {
        var serializedIds = string.Join(",", threadIds.Select(id => $"\"{id}\""));
        return $$"""
        {
          "electron-persisted-atom-state": {
            "unread-thread-ids-by-host-v1": {
              "local": [{{serializedIds}}]
            }
          }
        }
        """;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
