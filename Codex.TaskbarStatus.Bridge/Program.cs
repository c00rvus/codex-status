using Codex.TaskbarStatus.Core;
using System.Text.Json;

string? eventName = null;
try
{
    var input = await Console.In.ReadToEndAsync();
    try
    {
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.TryGetProperty("hook_event_name", out var eventProperty))
        {
            eventName = eventProperty.GetString();
        }
    }
    catch (JsonException)
    {
    }

    await new HookEventProcessor().ProcessAsync(input);
}
catch
{
    // Hooks are observational. They must never block or fail the Codex command.
}

if (eventName is "Stop" or "SubagentStop")
{
    Console.Out.WriteLine("{\"continue\":true}");
}
