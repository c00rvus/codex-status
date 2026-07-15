using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class TaskbarSlotGeometryTests
{
    private static readonly PixelRectangle Taskbar = new(100, 900, 1_000, 40);
    private static readonly TaskbarSlotMargins Margins = new(20, 2, 30, 3);

    [Fact]
    public void Calculate_UsesAnchorOffsetWithinAvailableArea()
    {
        var slot = TaskbarSlotGeometry.Calculate(
            Taskbar,
            anchorOffsetPx: 260,
            desiredWidthPx: 300,
            Margins);

        Assert.Equal(new PixelRectangle(360, 902, 300, 35), slot);
    }

    [Fact]
    public void Calculate_ClampsAnchorAtLeftMargin()
    {
        var slot = TaskbarSlotGeometry.Calculate(
            Taskbar,
            anchorOffsetPx: -50,
            desiredWidthPx: 300,
            Margins);

        Assert.Equal(new PixelRectangle(120, 902, 300, 35), slot);
    }

    [Fact]
    public void Calculate_ClampsAnchorAtRightMargin()
    {
        var slot = TaskbarSlotGeometry.Calculate(
            Taskbar,
            anchorOffsetPx: 900,
            desiredWidthPx: 300,
            Margins);

        Assert.Equal(new PixelRectangle(770, 902, 300, 35), slot);
    }

    [Fact]
    public void Calculate_ClampsWidthToEntireAvailableArea()
    {
        var slot = TaskbarSlotGeometry.Calculate(
            Taskbar,
            anchorOffsetPx: 500,
            desiredWidthPx: 2_000,
            Margins);

        Assert.Equal(new PixelRectangle(120, 902, 950, 35), slot);
    }
}
