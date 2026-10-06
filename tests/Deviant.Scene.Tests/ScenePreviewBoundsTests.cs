using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class ScenePreviewBoundsTests
{
    [Fact]
    public void SurroundingAndDistantObjectsDoNotPullPreviewAwayFromOccupiedCluster()
    {
        GeometryBounds[] bounds =
        [
            new(new(99, 1, 19), new(101, 3, 21)),
            new(new(102, 1, 19), new(104, 3, 21)),
            new(new(96, 1, 21), new(98, 3, 23)),
            new(new(99, 0, 16), new(101, 2, 18)),
            new(new(-1500, -100, -1500), new(1500, 1200, 1500)),
            new(new(4900, 4900, 4900), new(5100, 5100, 5100))
        ];
        var preview = ScenePreviewBounds.Choose(bounds);
        var center = (preview.Minimum + preview.Maximum) * .5f;
        Assert.InRange(center.X, 97, 103);
        Assert.InRange(center.Y, 1, 2);
        Assert.InRange(center.Z, 17, 22);
        Assert.Equal(new Vector3(20), preview.Maximum - preview.Minimum);
    }

    [Fact]
    public void SingleObjectKeepsItsCenter()
    {
        var preview = ScenePreviewBounds.Choose([new(new(98, -2, 18), new(102, 2, 22))]);
        Assert.Equal(new Vector3(100, 0, 20), (preview.Minimum + preview.Maximum) * .5f);
    }
}
