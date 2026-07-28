using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class ReviewedTaskStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"CodexReviewedTasks-{Guid.NewGuid():N}");

    [Fact]
    public void MarkReviewed_PersistsDistinctTaskKeys()
    {
        var path = Path.Combine(_directory, "reviewed.json");
        var store = new ReviewedTaskStore(path);

        store.MarkReviewed("session:a|turn:1");
        store.MarkReviewed("session:b|turn:2");
        store.MarkReviewed("session:a|turn:1");

        var restored = new ReviewedTaskStore(path).Read();
        Assert.Equal(2, restored.Count);
        Assert.Contains("session:a|turn:1", restored);
        Assert.Contains("session:b|turn:2", restored);
    }

    [Fact]
    public void Read_MalformedFile_ReturnsEmptySet()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "reviewed.json");
        File.WriteAllText(path, "{not-json");

        Assert.Empty(new ReviewedTaskStore(path).Read());
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
