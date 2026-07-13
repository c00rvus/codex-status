namespace Codex.TaskbarStatus.Core;

public sealed record PreviewPresentation(
    bool ShowActivity,
    bool ShowSpinner,
    bool ShowElapsed,
    string? FilesText,
    string? SubagentsText,
    double ActivityMaxWidth,
    double FilesMaxWidth,
    double SubagentsMaxWidth);

public static class PreviewPresentationFactory
{
    public static PreviewPresentation Create(
        bool showActivity,
        bool showFiles,
        bool showSubagents,
        bool showElapsed,
        bool showSpinner,
        bool isActive,
        bool compact,
        int filesChangedCount,
        int totalSubagents)
    {
        return new PreviewPresentation(
            showActivity,
            showSpinner && isActive,
            showElapsed,
            showFiles ? FormatCount(filesChangedCount, "file", "files") : null,
            showSubagents ? FormatCount(totalSubagents, "subagent", "subagents") : null,
            compact ? 92 : 148,
            compact ? 70 : 86,
            compact ? 78 : 92);
    }

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
