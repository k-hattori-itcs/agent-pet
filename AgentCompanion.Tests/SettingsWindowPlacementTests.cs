using System.Windows;
using AgentCompanion.Windows;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class SettingsWindowPlacementTests
{
    [Fact]
    public void ResolveWindowPosition_PreservesFiniteCoordinatesOnAnotherDisplay()
    {
        var virtualDesktopBounds = new Rect(0, 0, 3840, 1080);

        var position = SettingsWindow.ResolveWindowPosition(
            left: 2500,
            top: 120,
            width: 640,
            height: 520,
            virtualDesktopBounds);

        Assert.Equal(new Point(2500, 120), position);
    }

    [Fact]
    public void ResolveWindowPosition_ClampsWindowThatIsFullyOffScreen()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        var position = SettingsWindow.ResolveWindowPosition(
            left: 2500,
            top: 120,
            width: 640,
            height: 520,
            bounds);

        Assert.Equal(new Point(1280, 120), position);
    }

    [Fact]
    public void ResolveWindowPosition_InitializesUnsetCoordinatesInsideBounds()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        var position = SettingsWindow.ResolveWindowPosition(
            left: double.NaN,
            top: double.NaN,
            width: 640,
            height: 520,
            bounds);

        Assert.Equal(new Point(1240, 520), position);
    }

    [Fact]
    public void ResolveAboveAnchorPosition_PlacesWindowAboveTraySelection()
    {
        var workArea = new Rect(0, 0, 1920, 1040);

        var position = SettingsWindow.ResolveAboveAnchorPosition(
            new Point(1500, 1020),
            width: 640,
            height: 520,
            workArea);

        Assert.Equal(new Point(1180, 488), position);
    }

    [Fact]
    public void ResolveAboveAnchorPosition_ClampsToSelectedMonitorWorkArea()
    {
        var secondaryWorkArea = new Rect(-1920, 0, 1920, 1040);

        var position = SettingsWindow.ResolveAboveAnchorPosition(
            new Point(-100, 1020),
            width: 640,
            height: 520,
            secondaryWorkArea);

        Assert.Equal(new Point(-640, 488), position);
    }

    [Fact]
    public void ResolveAboveAnchorPosition_ClampsWhenAnchorIsNearTopEdge()
    {
        var workArea = new Rect(0, 0, 1920, 1040);

        var position = SettingsWindow.ResolveAboveAnchorPosition(
            new Point(500, 20),
            width: 640,
            height: 520,
            workArea);

        Assert.Equal(new Point(180, 0), position);
    }
}
