namespace Codex.TaskbarStatus.Core;

/// <summary>
/// A rectangle expressed in physical screen pixels.
/// </summary>
public readonly record struct PixelRectangle(int X, int Y, int Width, int Height);

/// <summary>
/// Insets applied to the taskbar rectangle before a widget slot is placed.
/// </summary>
public readonly record struct TaskbarSlotMargins(int Left, int Top, int Right, int Bottom)
{
    public static TaskbarSlotMargins None { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Calculates a widget rectangle that is always contained by a horizontal taskbar.
/// </summary>
public static class TaskbarSlotGeometry
{
    /// <summary>
    /// Calculates a widget slot. <paramref name="anchorOffsetPx"/> is measured from
    /// the taskbar's left edge to the requested left edge of the widget.
    /// </summary>
    public static PixelRectangle Calculate(
        PixelRectangle taskbar,
        int anchorOffsetPx,
        int desiredWidthPx,
        TaskbarSlotMargins margins)
    {
        var taskbarWidth = Math.Max(0, taskbar.Width);
        var taskbarHeight = Math.Max(0, taskbar.Height);

        var leftMargin = Math.Clamp(margins.Left, 0, taskbarWidth);
        var rightMargin = Math.Clamp(margins.Right, 0, taskbarWidth - leftMargin);
        var topMargin = Math.Clamp(margins.Top, 0, taskbarHeight);
        var bottomMargin = Math.Clamp(margins.Bottom, 0, taskbarHeight - topMargin);

        var availableWidth = taskbarWidth - leftMargin - rightMargin;
        var slotWidth = Math.Clamp(desiredWidthPx, 0, availableWidth);
        var slotHeight = taskbarHeight - topMargin - bottomMargin;

        var minimumX = (long)taskbar.X + leftMargin;
        var maximumX = (long)taskbar.X + taskbarWidth - rightMargin - slotWidth;
        var requestedX = (long)taskbar.X + anchorOffsetPx;
        var slotX = Math.Clamp(requestedX, minimumX, maximumX);

        return new PixelRectangle(
            checked((int)slotX),
            checked(taskbar.Y + topMargin),
            slotWidth,
            slotHeight);
    }
}
