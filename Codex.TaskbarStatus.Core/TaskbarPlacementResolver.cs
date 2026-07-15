namespace Codex.TaskbarStatus.Core;

public enum TaskbarPlacementMode
{
    Automatic,
    Left,
    Center,
    Right,
    Manual,
}

public readonly record struct PixelInterval(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
}

/// <summary>
/// Chooses a collision-free taskbar slot from physical-pixel occupied ranges.
/// </summary>
public static class TaskbarPlacementResolver
{
    public static PixelRectangle? Calculate(
        PixelRectangle taskbar,
        int desiredWidthPx,
        TaskbarSlotMargins margins,
        TaskbarPlacementMode mode,
        int preferredOffsetPx,
        IEnumerable<PixelInterval> occupied,
        int occupiedSpacingPx = 0)
    {
        ArgumentNullException.ThrowIfNull(occupied);

        var left = taskbar.X + Math.Clamp(margins.Left, 0, Math.Max(0, taskbar.Width));
        var rightMargin = Math.Clamp(
            margins.Right,
            0,
            Math.Max(0, taskbar.Width - (left - taskbar.X)));
        var right = taskbar.X + Math.Max(0, taskbar.Width) - rightMargin;
        var top = taskbar.Y + Math.Clamp(margins.Top, 0, Math.Max(0, taskbar.Height));
        var bottomMargin = Math.Clamp(
            margins.Bottom,
            0,
            Math.Max(0, taskbar.Height - (top - taskbar.Y)));
        var height = Math.Max(0, taskbar.Y + taskbar.Height - bottomMargin - top);
        var width = Math.Clamp(desiredWidthPx, 0, Math.Max(0, right - left));
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var spacing = Math.Max(0, occupiedSpacingPx);
        var merged = MergeIntervals(
            occupied.Select(interval => new PixelInterval(
                Math.Max(left, interval.Start - spacing),
                Math.Min(right, interval.End + spacing))),
            left,
            right);
        var gaps = BuildGaps(merged, left, right)
            .Where(gap => gap.Length >= width)
            .ToArray();
        if (gaps.Length == 0)
        {
            return null;
        }

        var requestedX = Math.Clamp(
            taskbar.X + Math.Max(0, preferredOffsetPx),
            left,
            right - width);
        var x = mode switch
        {
            TaskbarPlacementMode.Left => gaps[0].Start,
            TaskbarPlacementMode.Center => ResolveCenter(gaps, taskbar, width),
            TaskbarPlacementMode.Right => gaps[^1].End - width,
            TaskbarPlacementMode.Manual => ResolveNearest(gaps, requestedX, width),
            _ => ResolveAutomatic(gaps, requestedX, width),
        };

        return new PixelRectangle(x, top, width, height);
    }

    public static IReadOnlyList<PixelInterval> MergeIntervals(
        IEnumerable<PixelInterval> intervals,
        int minimum,
        int maximum)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var ordered = intervals
            .Select(interval => new PixelInterval(
                Math.Clamp(interval.Start, minimum, maximum),
                Math.Clamp(interval.End, minimum, maximum)))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Array.Empty<PixelInterval>();
        }

        var merged = new List<PixelInterval>();
        var current = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.Start <= current.End)
            {
                current = new PixelInterval(current.Start, Math.Max(current.End, next.End));
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    private static IEnumerable<PixelInterval> BuildGaps(
        IReadOnlyList<PixelInterval> occupied,
        int left,
        int right)
    {
        var cursor = left;
        foreach (var interval in occupied)
        {
            if (interval.Start > cursor)
            {
                yield return new PixelInterval(cursor, interval.Start);
            }
            cursor = Math.Max(cursor, interval.End);
        }

        if (cursor < right)
        {
            yield return new PixelInterval(cursor, right);
        }
    }

    private static int ResolveAutomatic(
        IReadOnlyList<PixelInterval> gaps,
        int requestedX,
        int width)
    {
        var containing = gaps.FirstOrDefault(
            gap => requestedX >= gap.Start && requestedX + width <= gap.End);
        if (containing.Length >= width)
        {
            return requestedX;
        }

        var widest = gaps
            .OrderByDescending(gap => gap.Length)
            .ThenBy(gap => DistanceToGap(gap, requestedX, width))
            .First();
        return Math.Clamp(requestedX, widest.Start, widest.End - width);
    }

    private static int ResolveNearest(
        IReadOnlyList<PixelInterval> gaps,
        int requestedX,
        int width)
    {
        return gaps
            .Select(gap => Math.Clamp(requestedX, gap.Start, gap.End - width))
            .OrderBy(x => Math.Abs((long)x - requestedX))
            .First();
    }

    private static int ResolveCenter(
        IReadOnlyList<PixelInterval> gaps,
        PixelRectangle taskbar,
        int width)
    {
        var targetCenter = taskbar.X + taskbar.Width / 2d;
        return gaps
            .Select(gap => Math.Clamp(
                (int)Math.Round(targetCenter - width / 2d),
                gap.Start,
                gap.End - width))
            .OrderBy(x => Math.Abs(x + width / 2d - targetCenter))
            .First();
    }

    private static long DistanceToGap(PixelInterval gap, int requestedX, int width)
    {
        var candidate = Math.Clamp(requestedX, gap.Start, gap.End - width);
        return Math.Abs((long)candidate - requestedX);
    }
}
