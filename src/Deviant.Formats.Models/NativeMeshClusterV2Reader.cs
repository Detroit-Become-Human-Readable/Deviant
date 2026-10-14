using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Reads bounded CLUPSKME v2 sections while retaining the wire fields as raw data.</summary>
public sealed class NativeMeshClusterV2Reader
{
    public const int MaximumEntryCount = NativeMeshDataReader.MaximumParts;

    public bool TryRead(ReadOnlyMemory<byte> payload, DecodedExtent source, int offset,
        out NativeMeshClusterV2Section? section, out string? diagnostic,
        CancellationToken token = default,
        int maximumPayloadBytes = NativeMeshDataReader.MaximumMetadataBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumPayloadBytes is < 0 or > NativeMeshDataReader.MaximumMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        section = null;
        diagnostic = null;
        token.ThrowIfCancellationRequested();
        if (payload.Length > maximumPayloadBytes)
        {
            diagnostic = "NativeMeshClusterLimit";
            return false;
        }
        if (source.Length != payload.Length || source.Offset < 0 ||
            source.Offset > long.MaxValue - payload.Length || offset < 0 || offset > payload.Length)
        {
            diagnostic = "NativeMeshClusterSourceMismatch";
            return false;
        }

        try
        {
            var clusterSource = new DecodedExtent(source.Container,
                checked(source.Offset + offset), payload.Length - offset);
            var reader = new NativeMeshByteReader(payload[offset..], clusterSource);
            reader.Tag("CLUPSKME"u8, 2);
            var flag = reader.Byte();
            if (flag == 0)
            {
                var length = reader.Position;
                var extent = reader.Extent(0, length);
                section = new(flag, 0, [], ReadOnlyMemory<byte>.Empty,
                    reader.Extent(length, 0), ReadOnlyMemory<byte>.Empty, reader.Extent(length, 0),
                    ReadOnlyMemory<byte>.Empty, reader.Extent(length, 0),
                    payload.Slice(offset, length), extent);
                return true;
            }
            if (flag != 1) throw new InvalidDataException("NativeMeshClustersUnsupported");

            var count = (uint)reader.Count(MaximumEntryCount);
            token.ThrowIfCancellationRequested();
            var scalarStart = reader.Position;
            var scalarBits = new uint[6];
            for (var index = 0; index < scalarBits.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                scalarBits[index] = reader.Word();
            }
            var scalarExtent = reader.Extent(scalarStart, reader.Position - scalarStart);
            var scalarBytes = payload.Slice(checked(offset + scalarStart), 24);

            var byteValueLength = checked((int)count * 3);
            var tupleValueLength = checked((int)count * 12);
            var requiredBytes = checked(byteValueLength + tupleValueLength);
            if (requiredBytes > payload.Length - checked(offset + reader.Position))
                throw new InvalidDataException("NativeMeshTruncated");

            token.ThrowIfCancellationRequested();
            var byteValueStart = reader.Position;
            var byteValueExtent = reader.Skip(byteValueLength);
            var byteValues = payload.Slice(checked(offset + byteValueStart), byteValueLength);
            token.ThrowIfCancellationRequested();
            var tupleValueStart = reader.Position;
            var tupleValueExtent = reader.Skip(tupleValueLength);
            var tupleValues = payload.Slice(checked(offset + tupleValueStart), tupleValueLength);
            token.ThrowIfCancellationRequested();

            var sectionLength = reader.Position;
            var sectionExtent = reader.Extent(0, sectionLength);
            section = new(flag, count, scalarBits, scalarBytes, scalarExtent,
                byteValues, byteValueExtent, tupleValues, tupleValueExtent,
                payload.Slice(offset, sectionLength), sectionExtent);
            return true;
        }
        catch (InvalidDataException error)
        {
            diagnostic = error.Message;
            return false;
        }
        catch (OverflowException)
        {
            diagnostic = "NativeMeshClusterSizeOverflow";
            return false;
        }
    }
}
