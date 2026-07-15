using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class TaskbarPlacementResolverTests
{
    private static readonly PixelRectangle Taskbar = new(0, 1000, 1000, 48);
    private static readonly TaskbarSlotMargins Margins = new(10, 0, 10, 0);

    [Fact]
    public void MergeIntervals_CombinesOverlappingAndTouchingRanges()
    {
        var result = TaskbarPlacementResolver.MergeIntervals(
            [new(100, 200), new(190, 240), new(240, 260), new(500, 520)],
            0,
            1000);

        Assert.Equal([new PixelInterval(100, 260), new PixelInterval(500, 520)], result);
    }

    [Fact]
    public void Automatic_KeepsPreferredPositionWhileItIsFree()
    {
        var slot = Calculate(TaskbarPlacementMode.Automatic, 64, [new(400, 600)]);

        Assert.Equal(new PixelRectangle(64, 1000, 100, 48), slot);
    }

    [Fact]
    public void Automatic_MovesOutOfAnOccupiedRange()
    {
        var slot = Calculate(TaskbarPlacementMode.Automatic, 450, [new(400, 600)]);

        Assert.Equal(300, slot?.X);
    }

    [Fact]
    public void Left_UsesFirstFreeGapWithNegativeCoordinates()
    {
        var slot = TaskbarPlacementResolver.Calculate(
            new PixelRectangle(-1920, 1032, 1920, 48),
            300,
            Margins,
            TaskbarPlacementMode.Left,
            64,
            [new PixelInterval(-1100, -800)]);

        Assert.Equal(new PixelRectangle(-1910, 1032, 300, 48), slot);
    }

    [Fact]
    public void Right_UsesLastFreeGap()
    {
        var slot = Calculate(TaskbarPlacementMode.Right, 0, [new(400, 600)]);

        Assert.Equal(890, slot?.X);
    }

    [Fact]
    public void Center_UsesClosestCollisionFreePosition()
    {
        var slot = Calculate(TaskbarPlacementMode.Center, 0, [new(200, 500)]);

        Assert.Equal(500, slot?.X);
    }

    [Fact]
    public void Manual_UsesNearestFreePositionWhenRequestedPointCollides()
    {
        var slot = Calculate(TaskbarPlacementMode.Manual, 450, [new(400, 600)]);

        Assert.Equal(300, slot?.X);
    }

    [Fact]
    public void Calculate_ReturnsNullWhenNoGapCanContainWidget()
    {
        var slot = Calculate(TaskbarPlacementMode.Automatic, 64, [new(0, 1000)]);

        Assert.Null(slot);
    }

    [Fact]
    public void OccupiedSpacing_IsReservedAroundSystemButtons()
    {
        var slot = TaskbarPlacementResolver.Calculate(
            Taskbar,
            20,
            Margins,
            TaskbarPlacementMode.Automatic,
            195,
            [new PixelInterval(200, 300)],
            occupiedSpacingPx: 10);

        Assert.Equal(310, slot?.X);
    }

    private static PixelRectangle? Calculate(
        TaskbarPlacementMode mode,
        int offset,
        IEnumerable<PixelInterval> occupied) =>
        TaskbarPlacementResolver.Calculate(
            Taskbar,
            100,
            Margins,
            mode,
            offset,
            occupied);
}
