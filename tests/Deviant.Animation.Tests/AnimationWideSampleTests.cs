using System.Numerics;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationWideSampleTests
{
    [Fact]
    public void ReconstructsTranslationDeltasAndWindowTransitions()
    {
        var (document, stream) = Fixture(); int releases = 0;
        using (var decoded = AnimationWideSamples.Read(document, stream, (_, _) => new Release(() => releases++)))
        {
            Assert.Equal(17u, decoded.Tracks.Single().Key);
            Assert.Equal(new Vector4(0, 1, 2, 0), decoded.RawSample(0, 0).Translation);
            Assert.Equal(new Vector4(1, 1 + 128 * BitConverter.Int32BitsToSingle(0x3b808081), 2, 0), decoded.RawSample(1, 0).Translation);
            Assert.Equal(new Vector4(10, 11, 12, 0), decoded.RawSample(2, 0).Translation);
            Assert.Equal(new Vector4(10, 12, 13, 0), decoded.RawSample(3, 0).Translation);
            Assert.Equal(5.5f, decoded.Sample(.75f, 0).Translation.X);
            Assert.Equal(decoded.RawSample(3, 0), decoded.Sample(2f, 0));
            Assert.Equal(new Vector4(0, 0, 0, 1), decoded.Sample(.75f, 0).Rotation);
            Assert.Equal(new Vector4(1, 1, 1, 0), decoded.Sample(.75f, 0).Scale);
        }
        Assert.Equal(1, releases);
    }

    [Fact]
    public void RejectsWrongStreamExtentAndNonfiniteValuesAndReleasesOnFailure()
    {
        var (document, stream) = Fixture(); int releases = 0;
        Assert.Throws<InvalidDataException>(() => AnimationWideSamples.Read(document, stream[..^1], (_, _) => new Release(() => releases++)));
        Assert.Equal(0, releases);
        BitConverter.GetBytes(float.NaN).CopyTo(stream, 4);
        Assert.Throws<InvalidDataException>(() => AnimationWideSamples.Read(document, stream, (_, _) => new Release(() => releases++)));
        Assert.Equal(1, releases);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => AnimationWideSamples.Read(document, stream, (_, _) => new Release(() => releases++), cancel.Token));
        Assert.Equal(1, releases);
    }

    [Fact]
    public void SecondaryScalarStreamKeepsSkeletalWindowsAligned()
    {
        var (document, stream) = Fixture(secondary: true);
        using var decoded = AnimationWideSamples.Read(document, stream, (_, _) => new Release(() => { }));
        Assert.Equal(new Vector4(10, 11, 12, 0), decoded.RawSample(2, 0).Translation);
        Assert.Equal(new Vector4(10, 12, 13, 0), decoded.RawSample(3, 0).Translation);
        Assert.Equal(5.5f, decoded.Sample(.75f, 0).Translation.X);
    }

    [Theory]
    [InlineData(0, 2f, 0f)]
    [InlineData(1, .49f, 0f)]
    [InlineData(1, .5f, .5f)]
    [InlineData(2, .25f, .25f)]
    public void RetargetSourceHeightFollowsTheStoredControl(int mode, float height, float expected)
    {
        var (document, stream) = Fixture(retargetMode: (byte)mode, sourceHeight: height);
        using var decoded = AnimationWideSamples.Read(document, stream, (_, _) => new Release(() => { }));
        Assert.Equal(expected, decoded.SourceHeight);
        Assert.Equal(expected == 0 ? 1f : 2f / expected, AnimationRetargeting.TranslationRatio(decoded.SourceHeight, 2));
    }

    private static (AnimationDataDocument, byte[]) Fixture(bool secondary = false, byte? retargetMode = null, float sourceHeight = 0)
    {
        using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.ASCII, true);
        w.Write(3u); w.Write("COM_CONT"u8); w.Write(6u); w.Write(0u);
        w.Write("LOADCONT"u8); w.Write(2u); w.Write("ANIMDATA"u8); w.Write(14u);
        w.Write(2u); w.Write(1u); w.Write(1u); w.Write((secondary ? 0xe42u : 0xa42u) | (retargetMode is null ? 0u : 0x2000u));
        w.Write(4u); w.Write(1u); w.Write(.5f);
        if (secondary) w.Write(1u);
        w.Write(17u); w.Write((ushort)6); w.Write((ushort)0);
        if (secondary) { w.Write(23u); w.Write((ushort)0); w.Write((ushort)0); }
        if (retargetMode is { } control) { w.Write(control); w.Write(sourceHeight); }
        w.Write((byte)0x11); if (secondary) w.Write((byte)0x11); w.Write(2u);
        foreach (float f in new float[] { 0, 0, 0, 1, 1, 1, 1, 0 }) w.Write(f);
        w.Write(2u); w.Write((byte)0);
        for (int i = 0; i < 256; i++) w.Write(0f);
        for (int i = 0; i < 256; i++) w.Write(1f);
        w.Write(0u);
        var payload = data.ToArray(); var identity = new ResourceIdentity("index", 7);
        var frame = new NativeObjectFrame(identity with { MemberPath = "dc-object/0/2033/1" }, 0, 2033, 1,
            new(identity, 0, 8), new(identity, 8, 13), new(identity, 21, payload.Length), [], payload, 1, 1, 0, 3);
        var document = new NativeAnimationDataReader().Decode(frame, new("index", "revision"),
            [new("source.dat", 0, 1, new string('0', 64))]).Value!;
        using var stream = new MemoryStream(); using var s = new BinaryWriter(stream, Encoding.ASCII, true);
        s.Write(new byte[4]); s.Write(0f); s.Write(1f); s.Write(2f);
        if (secondary) s.Write(42f);
        s.Write(new byte[] { 255, 128, 0 }); if (secondary) s.Write((byte)17);
        s.Write(new byte[4]); s.Write(10f); s.Write(11f); s.Write(12f);
        if (secondary) s.Write(43f);
        s.Write(new byte[] { 0, 255, 255 }); if (secondary) s.Write((byte)18);
        s.Write(new byte[128 - stream.Length]); return (document, stream.ToArray());
    }
    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
}
