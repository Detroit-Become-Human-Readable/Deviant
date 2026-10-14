using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationVersion14TimingTests
{
    [Fact]
    public void DerivesDurationFromSignedCountAndFloatPeriodAtTheEmbeddedRecordOffset()
    {
        const uint rawCount = 16_777_217;
        const uint periodBits = 0x3DCCCCCD; // generated IEEE float value near one tenth
        var layout = Read(rawCount, periodBits).Version14Layout!;

        Assert.True(layout.RemainingResourceOffset > 0);
        Assert.Equal(rawCount, layout.Scalar16);
        Assert.Equal(periodBits, layout.ScalarBits24);

        var timing = ReadTiming(layout);
        Assert.NotNull(timing);
        var timingValue = timing!;
        Assert.Equal("Deviant.Formats.Animation.AnimationVersion14Timing", timingValue.GetType().FullName);
        Assert.Equal((int)rawCount, Property<int>(timingValue, "Count"));
        var period = BitConverter.Int32BitsToSingle(unchecked((int)periodBits));
        Assert.Equal(period, Property<float>(timingValue, "Period"));
        var nativeDuration = (float)(int)rawCount * period;
        Assert.Equal(1_677_721.625f, nativeDuration);
        Assert.Equal(nativeDuration, Property<float>(timingValue, "Duration"));
        Assert.NotEqual((float)((double)(int)rawCount * period), Property<float>(timingValue, "Duration"));
        Assert.Equal(rawCount, layout.Scalar16);
        Assert.Equal(periodBits, layout.ScalarBits24);
    }

    [Fact]
    public void UsesTheWholePositiveSampleCountForDuration()
    {
        var layout = Read(4, 0x3E800000).Version14Layout!; // 0.25 seconds per sample
        var timing = ReadTiming(layout);
        Assert.NotNull(timing);
        var timingValue = timing!;

        Assert.Equal(4, Property<int>(timingValue, "Count"));
        Assert.Equal(0.25f, Property<float>(timingValue, "Period"));
        Assert.Equal(1.0f, Property<float>(timingValue, "Duration"));
    }

    [Fact]
    public void NonVersion14RawLayoutDoesNotComputeTiming()
    {
        var layout = new AnimationVersion14Layout(9, 1, 1, 0, 4, 0, 0x3E800000,
            null, Array.Empty<AnimationScalarTable>(), 128, 0);

        Assert.Null(ReadTiming(layout));
        Assert.Equal(9u, layout.Variant);
        Assert.Equal(4u, layout.Scalar16);
        Assert.Equal(0x3E800000u, layout.ScalarBits24);
    }

    [Theory]
    [InlineData(0u, 0x3F800000u)]
    [InlineData(uint.MaxValue, 0x3F800000u)]
    [InlineData(0x80000000u, 0x3F800000u)]
    [InlineData(4u, 0u)]
    [InlineData(4u, 0xBF000000u)]
    [InlineData(4u, 0x7FC01234u)]
    [InlineData(4u, 0x7F800000u)]
    [InlineData(4u, 0xFF800000u)]
    [InlineData(0x7FFFFFFFu, 0x7F7FFFFFu)]
    public void InvalidTimingKeepsTheRawVersion14Layout(uint rawCount, uint periodBits)
    {
        var layout = Read(rawCount, periodBits).Version14Layout!;

        Assert.Null(ReadTiming(layout));
        Assert.Equal(rawCount, layout.Scalar16);
        Assert.Equal(periodBits, layout.ScalarBits24);
        Assert.True(layout.RemainingResourceOffset > 0);
    }

    private static object? ReadTiming(AnimationVersion14Layout layout)
    {
        var property = typeof(AnimationVersion14Layout).GetProperty("Timing", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal("Deviant.Formats.Animation.AnimationVersion14Timing", property.PropertyType.FullName);
        return property.GetValue(layout);
    }

    private static T Property<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
    }

    private static AnimationDataDocument Read(uint rawCount, uint periodBits)
    {
        var encoded = Fixture(rawCount, periodBits);
        var identity = new ResourceIdentity("synthetic-index", 1);
        var revision = new SourceRevision(identity.IndexContentId, "synthetic-revision");
        var decoded = new AnimationDataReader().Decode(encoded, identity, revision,
            new SourceExtent("generated-package.bin", 256, encoded.Length), AnimationReadProfile.Version14Tables);
        Assert.Empty(decoded.Diagnostics);
        return Assert.IsType<AnimationDataDocument>(decoded.Value);
    }

    private static byte[] Fixture(uint rawCount, uint periodBits)
    {
        var body = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 2); // v14 profile variant
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), rawCount);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), periodBits);

        using var output = new MemoryStream();
        WriteSection("COM_CONT", 6, new byte[13]);
        WriteSection("LOADCONT", 2, []);
        WriteSection("ANIMDATA", 14, body);
        return output.ToArray();

        void WriteSection(string tag, uint version, byte[] data)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, version);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)data.Length));
            output.Write(header);
            output.Write(data);
        }
    }
}
