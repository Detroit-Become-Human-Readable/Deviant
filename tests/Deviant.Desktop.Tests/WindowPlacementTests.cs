using System.Windows;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void NarrowWorkAreaKeepsTheWholeInitialWindowVisible()
    {
        var fitted = WindowPlacement.Fit(new Rect(-160, 100, 1600, 900), new Rect(0, 0, 1280, 1920));
        Assert.Equal(new Rect(0, 100, 1280, 900), fitted);
    }

    [Fact]
    public void NegativeMonitorOriginAndTaskbarInsetAreRespected()
    {
        var fitted = WindowPlacement.Fit(new Rect(-2200, -100, 1600, 900), new Rect(-1920, 40, 1440, 800));
        Assert.Equal(new Rect(-1920, 40, 1440, 800), fitted);
    }

    [Fact]
    public void BoundsAlreadyInsideALargerWorkAreaArePreserved()
    {
        var requested = new Rect(300, 150, 1600, 900);
        Assert.Equal(requested, WindowPlacement.Fit(requested, new Rect(0, 0, 2560, 1400)));
    }
}
