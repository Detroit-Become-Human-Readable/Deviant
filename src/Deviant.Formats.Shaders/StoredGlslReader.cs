using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Shaders;

public sealed record ShaderDiagnostic(string Code, string Message, long? DecodedOffset = null);

public sealed record StoredGlslSource(
    ResourceIdentity Identity,
    SourceRevision Revision,
    int RegionOrdinal,
    string? Text,
    ReadOnlyMemory<byte> Utf8Bytes,
    ReadOnlyMemory<byte> ContextBefore,
    string Sha256,
    string VersionDirective,
    SourceExtent PhysicalExtent,
    IReadOnlyList<SourceExtent> PhysicalExtents,
    DecodedExtent DecodedExtent,
    DecodedExtent ContextExtent,
    IReadOnlyList<DecodeStep> DecodeSteps,
    bool HasNullTerminator)
{
    public bool CanPreview => Text is not null;

    public AssetDescriptor ToAssetDescriptor()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sha256"] = Sha256,
            ["versionDirective"] = VersionDirective,
            ["decodedOffset"] = DecodedExtent.Offset.ToString(CultureInfo.InvariantCulture),
            ["decodedLength"] = DecodedExtent.Length.ToString(CultureInfo.InvariantCulture),
            ["contextOffset"] = ContextExtent.Offset.ToString(CultureInfo.InvariantCulture),
            ["contextLength"] = ContextExtent.Length.ToString(CultureInfo.InvariantCulture),
            ["contextBeforeHex"] = Convert.ToHexString(ContextBefore.Span),
            ["nullTerminated"] = HasNullTerminator.ToString(CultureInfo.InvariantCulture)
        };
        var containerIdentity = new ResourceIdentity(Identity.IndexContentId, Identity.Ordinal);
        return new AssetDescriptor(Identity, Revision, "StoredShaderSource",
            $"GLSL region {RegionOrdinal + 1}", containerIdentity.Key, AssetAvailability.Available,
            HasNullTerminator && Text is not null ? AssetCompleteness.Complete : AssetCompleteness.Partial,
            AssetCapabilities.Original | AssetCapabilities.Json,
            Array.Empty<NameEvidence>(), PhysicalExtents,
            Reason: !HasNullTerminator ? "The source reaches the end of its decoded prefix without a NUL terminator." :
                CanPreview ? null : "The source bytes are not valid UTF-8.",
            SortKey: DecodedExtent.Offset.ToString("D12", CultureInfo.InvariantCulture))
        {
            SearchTerms = Text,
            LookupKey = Identity.Key,
            Metadata = metadata
        };
    }

    public async Task WriteOriginalAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await destination.WriteAsync(Utf8Bytes, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record StoredGlslScan(
    ResourceIdentity ContainerIdentity,
    SourceRevision Revision,
    IReadOnlyList<StoredGlslSource> Sources,
    IReadOnlyList<ShaderDiagnostic> Diagnostics,
    IReadOnlyList<SourceExtent> SourceExtents,
    IReadOnlyList<DecodeStep> DecodeSteps);

/// <summary>Finds NUL-bounded GLSL source text inside a validated contiguous SEGS decoded prefix.</summary>
public static class StoredGlslReader
{
    public const string Version = "stored-glsl-1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> VersionPrefix => "#version "u8;

    public static StoredGlslScan Scan(DecodeResult<SegsDocument> decoded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        cancellationToken.ThrowIfCancellationRequested();
        var document = decoded.Value;
        if (document is null)
            return new(decoded.Identity, decoded.Revision, [],
                decoded.Diagnostics.Select(message => new ShaderDiagnostic("ContainerDecode", message)).ToArray(),
                decoded.Extents, decoded.Steps);

        var sources = new List<StoredGlslSource>();
        var diagnostics = new List<ShaderDiagnostic>();
        if (!TryValidatePrefix(decoded.Identity, document, diagnostics, cancellationToken))
            return BuildScan(decoded, document, sources, diagnostics);

        ScanPrefix(decoded, document, sources, diagnostics, cancellationToken);
        return BuildScan(decoded, document, sources, diagnostics);
    }

    private static StoredGlslScan BuildScan(DecodeResult<SegsDocument> decoded, SegsDocument document,
        IReadOnlyList<StoredGlslSource> sources, IReadOnlyList<ShaderDiagnostic> diagnostics) =>
        new(decoded.Identity, decoded.Revision, sources, diagnostics,
            decoded.Extents.Concat(document.Chunks.Select(chunk => chunk.PhysicalExtent)).Distinct().ToArray(),
            decoded.Steps.Concat([new DecodeStep(nameof(StoredGlslReader), Version,
                "validated contiguous decoded prefix; UTF-8 source bytes retained; NUL boundary inspected")]).ToArray());

    private static bool TryValidatePrefix(ResourceIdentity identity, SegsDocument document,
        List<ShaderDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var prefix = document.DecodedPrefix.AsSpan();
        long expectedOffset = 0;
        var valid = true;
        foreach (var chunk in document.Chunks.OrderBy(chunk => chunk.DecodedExtent.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.DecodedBytes.Length != chunk.DecodedExtent.Length)
            {
                diagnostics.Add(new("DecodedExtentMismatch", "The decoded chunk byte count differs from its recorded extent.", chunk.DecodedExtent.Offset));
                valid = false;
                continue;
            }
            if (chunk.DecodedExtent.Container != identity || chunk.DecodedExtent.Offset != expectedOffset ||
                chunk.DecodedExtent.Offset < 0 || chunk.DecodedExtent.Offset > prefix.Length - chunk.DecodedBytes.Length)
            {
                diagnostics.Add(new("DecodedPrefixDiscontinuity", "The chunk extents do not describe one contiguous decoded prefix.", chunk.DecodedExtent.Offset));
                valid = false;
                continue;
            }
            if (!prefix.Slice(checked((int)chunk.DecodedExtent.Offset), chunk.DecodedBytes.Length).SequenceEqual(chunk.DecodedBytes))
            {
                diagnostics.Add(new("DecodedPrefixMismatch", "The joined prefix differs from the recorded decoded chunk bytes.", chunk.DecodedExtent.Offset));
                valid = false;
            }
            expectedOffset = checked(expectedOffset + chunk.DecodedBytes.Length);
        }
        if (expectedOffset != prefix.Length || document.ActualDecodedLength != prefix.Length ||
            document.DeclaredDecodedLength != prefix.Length)
        {
            diagnostics.Add(new("DecodedPrefixLengthMismatch", "The chunk extents, actual decoded length, and declared prefix length do not agree."));
            valid = false;
        }
        return valid;
    }

    private static void ScanPrefix(DecodeResult<SegsDocument> decoded, SegsDocument document,
        List<StoredGlslSource> sources, List<ShaderDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var bytes = document.DecodedPrefix.AsSpan();
        var searchFrom = 0;
        while (searchFrom <= bytes.Length - VersionPrefix.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeStart = bytes[searchFrom..].IndexOf(VersionPrefix);
            if (relativeStart < 0) break;
            var start = searchFrom + relativeStart;
            if (!TryReadVersionDirective(bytes[start..], out var directive, out var headerLength))
            {
                searchFrom = start + VersionPrefix.Length;
                continue;
            }

            var terminatorRelative = bytes[(start + headerLength)..].IndexOf((byte)0);
            var hasNullTerminator = terminatorRelative >= 0;
            var end = hasNullTerminator ? start + headerLength + terminatorRelative : bytes.Length;
            if (end <= start)
            {
                diagnostics.Add(new("EmptySourceRegion", "The GLSL source region is empty.", start));
                searchFrom = start + headerLength;
                continue;
            }

            var absoluteDecodedOffset = (long)start;
            var contextStart = Math.Max(0, start - 2);
            var provenanceEnd = end + (hasNullTerminator ? 1 : 0);
            var physicalExtents = PhysicalExtentsFor(document.Chunks, contextStart, provenanceEnd - contextStart);
            if (physicalExtents.Count == 0)
            {
                diagnostics.Add(new("MissingPhysicalExtent", "No compressed chunk extent covers the GLSL source bytes.", absoluteDecodedOffset));
                searchFrom = hasNullTerminator ? end + 1 : end;
                continue;
            }

            var regionBytes = bytes[start..end].ToArray();
            string? text;
            try
            {
                text = StrictUtf8.GetString(regionBytes);
            }
            catch (DecoderFallbackException)
            {
                text = null;
                diagnostics.Add(new("InvalidUtf8", "The candidate GLSL text contains invalid UTF-8 bytes.", absoluteDecodedOffset));
            }

            if (!hasNullTerminator)
                diagnostics.Add(new("UnterminatedSourceRegion", "The candidate GLSL source reaches the end of the validated decoded prefix without a NUL terminator.", absoluteDecodedOffset));

            var ordinal = sources.Count;
            var contextBytes = bytes[contextStart..start].ToArray();
            var identity = new ResourceIdentity(decoded.Identity.IndexContentId, decoded.Identity.Ordinal,
                $"shader/{absoluteDecodedOffset:X8}");
            var decodedExtent = new DecodedExtent(decoded.Identity, absoluteDecodedOffset, regionBytes.Length);
            var contextExtent = new DecodedExtent(decoded.Identity, contextStart, contextBytes.Length);
            sources.Add(new(identity, decoded.Revision, ordinal, text, regionBytes, contextBytes,
                Convert.ToHexString(SHA256.HashData(regionBytes)), directive,
                physicalExtents[0], physicalExtents, decodedExtent, contextExtent, decoded.Steps, hasNullTerminator));

            searchFrom = hasNullTerminator ? end + 1 : end;
        }
    }

    private static IReadOnlyList<SourceExtent> PhysicalExtentsFor(IReadOnlyList<SegsChunk> chunks, long start, int length)
    {
        var end = checked(start + length);
        return chunks.Where(chunk =>
                chunk.DecodedExtent.Length > 0 && chunk.DecodedExtent.Offset < end &&
                chunk.DecodedExtent.Offset + chunk.DecodedExtent.Length > start)
            .OrderBy(chunk => chunk.DecodedExtent.Offset)
            .Select(chunk => chunk.PhysicalExtent)
            .Distinct()
            .ToArray();
    }

    private static bool TryReadVersionDirective(ReadOnlySpan<byte> bytes, out string directive, out int headerLength)
    {
        directive = string.Empty;
        headerLength = 0;
        var newline = bytes.IndexOf((byte)'\n');
        if (newline < 0) return false;
        var lineLength = newline + 1;
        var line = bytes[..lineLength];
        if (line.Length <= VersionPrefix.Length) return false;
        var digitStart = VersionPrefix.Length;
        var digitEnd = digitStart;
        while (digitEnd < line.Length && line[digitEnd] is >= (byte)'0' and <= (byte)'9') digitEnd++;
        if (digitEnd == digitStart) return false;
        for (var index = digitEnd; index < line.Length; index++)
            if (line[index] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        try
        {
            directive = StrictUtf8.GetString(line);
            headerLength = lineLength;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
