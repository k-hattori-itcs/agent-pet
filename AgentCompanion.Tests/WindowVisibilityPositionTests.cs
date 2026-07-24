using System.Windows;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class WindowVisibilityPositionTests
{
    [Fact]
    public void ResolveRestoredScreenPosition_PreservesHiddenPhysicalCoordinates()
    {
        var hiddenPosition = new Point(2500, 180);

        var restored = MainWindow.ResolveRestoredScreenPosition(
            hiddenPosition,
            monitorAvailable: true);

        Assert.Equal(hiddenPosition, restored);
    }

    [Fact]
    public void ResolveRestoredScreenPosition_RejectsPositionFromDisconnectedMonitor()
    {
        var restored = MainWindow.ResolveRestoredScreenPosition(
            new Point(2500, 180),
            monitorAvailable: false);

        Assert.Null(restored);
    }
}
