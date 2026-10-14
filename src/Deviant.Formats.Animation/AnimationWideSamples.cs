using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Animation;

public readonly record struct AnimationTransformSample(Vector4 Rotation, Vector4 Translation, Vector4 Scale);
public sealed record AnimationTransformTrack(uint Key, ushort StoredIndex, ushort ConstantIndex, byte Flags);

public sealed class AnimationWideSamples : IDisposable
{
    public const int MaximumSamples = 2_000_000;
    private IDisposable? memory;
    private AnimationTransformSample[]? samples;
    private AnimationWideSamples(AnimationVersion14Timing timing, uint window, AnimationTransformTrack[] tracks,
        AnimationTransformSample[] samples, IDisposable memory, float sourceHeight)
    { Timing = timing; FramesPerWindow = window; Tracks = Array.AsReadOnly(tracks); this.samples = samples; this.memory = memory; SourceHeight = sourceHeight; }
    public AnimationVersion14Timing Timing { get; }
    public uint FramesPerWindow { get; }
    public IReadOnlyList<AnimationTransformTrack> Tracks { get; }
    public float SourceHeight { get; }
    public AnimationTransformSample RawSample(int frame, int track)
    {
        ObjectDisposedException.ThrowIf(samples is null, this);
        if ((uint)frame >= Timing.Count || (uint)track >= Tracks.Count) throw new ArgumentOutOfRangeException(nameof(frame));
        return samples[checked(frame * Tracks.Count + track)];
    }
    public AnimationTransformSample Sample(float seconds, int track)
    {
        var interval = AnimationFrameIntervals.At(Timing, FramesPerWindow, seconds);
        var a = RawSample(interval.First, track); var b = RawSample(interval.Second, track);
        var rotation = a.Rotation;
        if ((Tracks[track].Flags & 2) != 0)
        {
            var q = Quaternion.Slerp(new(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W),
                new(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W), interval.Fraction);
            float length = q.LengthSquared();
            if (length > 0) q = Quaternion.Normalize(q);
            rotation = new(q.X, q.Y, q.Z, q.W);
        }
        return Validate(new(rotation, a.Translation + (b.Translation - a.Translation) * interval.Fraction,
            a.Scale + (b.Scale - a.Scale) * interval.Fraction));
    }

    public static AnimationWideSamples Read(AnimationDataDocument document, ReadOnlyMemory<byte> stream,
        Func<long, string, IDisposable> reserve, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(reserve); token.ThrowIfCancellationRequested();
        var descriptor = AnimationVersion14Streams.ReadDescriptor(document, token);
        var layout = document.Version14Layout!; var timing = layout.Timing!;
        if ((layout.Flags & 0x800) == 0 || timing.Count < 2 ||
            stream.Length != (long)descriptor.Sizing.AlignedSourceByteLength)
            throw new InvalidDataException("AnimationWideProfileUnsupported");
        var table = layout.Tables.Single(x => x.Kind == "0x40");
        if (table.Rows.Count == 0 || (long)(table.Rows.Count + descriptor.SecondaryFlags.Length) * timing.Count > MaximumSamples)
            throw new InvalidDataException("AnimationSampleLimit");
        var memory = reserve(checked((long)table.Rows.Count * timing.Count * 48 + table.Rows.Count * 256L +
            descriptor.SecondaryFlags.Length * 16L + 65536), "animation samples");
        try
        {
            var tracks = table.Rows.Select((row, i) => new AnimationTransformTrack(row.Fields[0].Bits,
                checked((ushort)row.Fields[1].Bits), checked((ushort)row.Fields[2].Bits), descriptor.PrimaryFlags.Span[i])).ToArray();
            if (tracks.Any(x => (x.Flags & 16) == 0) || tracks.Select(x => x.Key).Distinct().Count() != tracks.Length ||
                !tracks.Select(x => x.Key).SequenceEqual(tracks.Select(x => x.Key).Order()))
                throw new InvalidDataException("AnimationWideTrackProfileUnsupported");
            var output = new AnimationTransformSample[checked(tracks.Length * timing.Count)];
            var anchors = new AnimationTransformSample[tracks.Length];
            var coefficients = new byte[checked(tracks.Length * 7)];
            var scalarFlags = descriptor.SecondaryFlags.ToArray();
            int cursor = 0; int frame = 0;
            int windowFrames = checked((int)Math.Min(descriptor.Sizing.PostWordK, (uint)timing.Count));
            while (frame < timing.Count)
            {
                token.ThrowIfCancellationRequested(); int start = cursor;
                int count = Math.Min(windowFrames, timing.Count - frame);
                Array.Clear(coefficients);
                if (descriptor.Sizing.PostWordK != 1)
                {
                    for (int track = 0; track < tracks.Length; track++)
                    {
                        int coefficient = track * 7;
                        byte flags = tracks[track].Flags;
                        if ((flags & 2) != 0) coefficients[coefficient] = Byte();
                        if ((flags & 1) != 0) for (int lane = 0; lane < 3; lane++) coefficients[coefficient + 1 + lane] = Byte();
                        if ((flags & 8) != 0) for (int lane = 0; lane < 3; lane++) coefficients[coefficient + 4 + lane] = Byte();
                    }
                    foreach (byte flags in scalarFlags) if ((flags & 16) != 0 && (flags & 0xB) != 0) _ = Byte();
                    cursor = checked(start + (int)descriptor.Sizing.SizeC);
                }
                for (int track = 0; track < tracks.Length; track++)
                {
                    int constant = tracks[track].ConstantIndex; byte flags = tracks[track].Flags;
                    var q = (flags & 2) != 0 ? new Vector4(Packed(), Packed(), Packed(), Packed()) : Constant(ref constant);
                    var t = (flags & 1) != 0 ? new Vector4(Float(), Float(), Float(), 0) : Constant(ref constant);
                    var s = (flags & 8) != 0 ? new Vector4(Float(), Float(), Float(), 0) : Constant(ref constant);
                    anchors[track] = output[frame * tracks.Length + track] = Validate(new(q, t, s));
                }
                foreach (byte flags in scalarFlags) if ((flags & 16) != 0 && (flags & 0xB) != 0) _ = Float();
                for (int local = 1; local < count; local++)
                {
                    for (int track = 0; track < tracks.Length; track++)
                    {
                        if ((track & 255) == 0) token.ThrowIfCancellationRequested();
                        var a = anchors[track]; byte flags = tracks[track].Flags; int coefficient = track * 7;
                        var q = (flags & 2) != 0 ? a.Rotation + new Vector4(Delta(coefficient), Delta(coefficient), Delta(coefficient), Delta(coefficient)) : a.Rotation;
                        var t = (flags & 1) != 0 ? a.Translation + new Vector4(Delta(coefficient + 1), Delta(coefficient + 2), Delta(coefficient + 3), 0) : a.Translation;
                        var s = (flags & 8) != 0 ? a.Scale + new Vector4(Delta(coefficient + 4), Delta(coefficient + 5), Delta(coefficient + 6), 0) : a.Scale;
                        output[(frame + local) * tracks.Length + track] = Validate(new(q, t, s));
                    }
                    foreach (byte flags in scalarFlags) if ((flags & 16) != 0 && (flags & 0xB) != 0) _ = Byte();
                }
                frame += count;
            }
            if ((ulong)cursor != descriptor.Sizing.LogicalByteLength) throw new InvalidDataException("AnimationStreamCursorMismatch");
            token.ThrowIfCancellationRequested();
            return new(timing, descriptor.Sizing.PostWordK, tracks, output, memory, AnimationRetargeting.ReadSourceHeight(document));

            byte Byte() { if ((uint)cursor >= stream.Length) throw new InvalidDataException("AnimationStreamTruncated"); return stream.Span[cursor++]; }
            float Float()
            {
                if (cursor > stream.Length - 4) throw new InvalidDataException("AnimationStreamTruncated");
                float value = BinaryPrimitives.ReadSingleLittleEndian(stream.Span[cursor..]); cursor += 4;
                return Finite(value);
            }
            float Packed()
            {
                if (cursor > stream.Length - 2) throw new InvalidDataException("AnimationStreamTruncated");
                ushort value = BinaryPrimitives.ReadUInt16LittleEndian(stream.Span[cursor..]); cursor += 2;
                return value * BitConverter.Int32BitsToSingle(0x38000100) - 1f;
            }
            Vector4 Constant(ref int index)
            {
                int at = checked(index++ * 16);
                if (at > descriptor.ConstantVectors.Length - 16) throw new InvalidDataException("AnimationConstantIndexInvalid");
                var bytes = descriptor.ConstantVectors.Span[at..];
                return new(Finite(BinaryPrimitives.ReadSingleLittleEndian(bytes)), Finite(BinaryPrimitives.ReadSingleLittleEndian(bytes[4..])),
                    Finite(BinaryPrimitives.ReadSingleLittleEndian(bytes[8..])), Finite(BinaryPrimitives.ReadSingleLittleEndian(bytes[12..])));
            }
            float Delta(int offset)
            {
                int at = coefficients[offset] * 4;
                float low = BinaryPrimitives.ReadSingleLittleEndian(descriptor.CoefficientLow.Span[at..]);
                float high = BinaryPrimitives.ReadSingleLittleEndian(descriptor.CoefficientHigh.Span[at..]);
                return Finite(low + (Byte() * BitConverter.Int32BitsToSingle(0x3b808081)) * (high - low));
            }
        }
        catch { memory.Dispose(); throw; }
        static float Finite(float value) => float.IsFinite(value) ? value : throw new InvalidDataException("AnimationSampleNonfinite");
    }

    public void Dispose() { samples = null; Interlocked.Exchange(ref memory, null)?.Dispose(); }

    private static AnimationTransformSample Validate(AnimationTransformSample sample)
    {
        if (!Finite(sample.Rotation) || !Finite(sample.Translation) || !Finite(sample.Scale))
            throw new InvalidDataException("AnimationSampleNonfinite");
        return sample;
        static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
            float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
