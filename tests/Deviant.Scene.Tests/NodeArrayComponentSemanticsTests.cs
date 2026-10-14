using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NodeArrayComponentSemanticsTests
{
    [Fact]
    public void StoredRangesRequireAMatchedBoundedOriginAndPreserveItsContainer()
    {
        var container = new ResourceIdentity("generated-index", 1);
        var origin = new DecodedExtent(container, long.MaxValue - 40, 40);
        var components = new NodeArrayStoredComponents(new byte[40], origin);
        var quaternion = components.QuaternionStoredExtent();
        var translation = components.TranslationStoredExtent();
        var scale = components.BasisScaleStoredExtent();
        Assert.Same(container, quaternion.Container); Assert.Same(container, translation.Container); Assert.Same(container, scale.Container);
        Assert.Equal(origin.Offset, quaternion.Offset); Assert.Equal(16, quaternion.Length);
        Assert.Equal(origin.Offset + 16, translation.Offset); Assert.Equal(12, translation.Length);
        Assert.Equal(origin.Offset + 28, scale.Offset); Assert.Equal(long.MaxValue, scale.Offset + scale.Length);

        foreach (var malformed in new[]
        {
            components with { Extent = origin with { Offset = -1 } },
            components with { Extent = origin with { Offset = long.MaxValue - 39 } },
            components with { Extent = origin with { Length = 39 } },
            components with { Extent = origin with { Container = null! } },
            components with { Extent = null! },
            components with { RawBytes = new byte[39] }
        })
        {
            Assert.Throws<ArgumentException>(() => malformed.QuaternionStoredExtent());
            Assert.Throws<ArgumentException>(() => malformed.TranslationStoredExtent());
            Assert.Throws<ArgumentException>(() => malformed.BasisScaleStoredExtent());
        }
    }
}
