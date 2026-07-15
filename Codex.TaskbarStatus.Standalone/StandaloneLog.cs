using System.Text;

namespace Codex.TaskbarStatus.Standalone;

internal static class StandaloneLog
{
    private static readonly object Sync = new();

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTaskbarStatus",
        "standalone.log");

    internal static void Write(string message, Exception? exception = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [")
                .Append(Environment.ProcessId)
                .Append("] ")
                .Append(message);
            if (exception is not null)
            {
                line.Append(": ").Append(exception);
            }

            lock (Sync)
            {
                File.AppendAllText(FilePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never bring down the taskbar widget.
        }
    }
}
