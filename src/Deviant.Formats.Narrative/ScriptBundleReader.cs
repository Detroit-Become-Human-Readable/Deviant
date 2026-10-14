using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Narrative;

/// <summary>Reads complete SBCHK/NOCHK envelopes. Lua prototype validation is a separate caller-selected operation.</summary>
public sealed class ScriptBundleReader
{
    public const string Version = "script-bundle-v1";
    private const int CopyBlockBytes = 128 * 1024;
    private const long RecordBudget = 1024;
    private static ReadOnlySpan<byte> LuaHeader => [27, 76, 117, 97, 81, 0, 1, 4, 4, 4, 4, 0];

    public DecodeResult<ScriptBundleDocument> Read(ReadOnlyMemory<byte> input, ResourceIdentity resource,
        SourceRevision revision, DecodedExtent inputExtent, IReadOnlyList<SourceExtent> sources,
        ScriptBundleLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(inputExtent);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        IReadOnlyList<SourceExtent> physical = Array.Empty<SourceExtent>();
        var steps = new[] { new DecodeStep(nameof(ScriptBundleReader), Version, "QZIP;SBCHK-v1;NOCHK-v1;Lua-header-only") };
        var unknown = new Dictionary<string, string>
        {
            ["metadataPayloadMeaning"] = "Unknown", ["opaqueKeyNamespace"] = "Unknown",
            ["bytecodeValidation"] = "Header only"
        };
        DecodeResult<ScriptBundleDocument> Fail(string code) =>
            new(null, resource, revision, physical, steps, [code], [], unknown);
        if (!ValidLimits(limits)) return Fail("ScriptBundleInvalidLimits");
        if (!ValidIdentity(resource, limits.MaximumIdentityCharacters) ||
            !ValidToken(revision.IndexContentId, limits.MaximumIdentityCharacters) ||
            !ValidToken(revision.PhysicalSourceStamp, limits.MaximumIdentityCharacters) ||
            resource.IndexContentId != revision.IndexContentId || inputExtent.Container is null ||
            !ValidIdentity(inputExtent.Container, limits.MaximumIdentityCharacters) ||
            !Owns(inputExtent.Container, resource) || inputExtent.Offset < 0 ||
            inputExtent.Length != input.Length || inputExtent.Offset > long.MaxValue - inputExtent.Length)
            return Fail("ScriptBundleSourceIdentityMismatch");
        if (input.Length > limits.MaximumInputBytes) return Fail("ScriptBundleInputLimit");
        var sourceCount = sources.Count;
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceCount < 0 || sourceCount > limits.MaximumPhysicalSources)
            return Fail("ScriptBundlePhysicalSourceLimit");

        try
        {
            // Charge both the supplied wire span and its stable snapshot, plus conservative DTO,
            // collection, identity-string, extent, and dictionary allowances before allocating.
            var budget = new Budget(limits.MaximumRetainedBytes);
            budget.Add(checked(4096L + input.Length * 2L + sourceCount * 16L +
                (resource.IndexContentId.Length + revision.PhysicalSourceStamp.Length +
                 (resource.MemberPath?.Length ?? 0) + (inputExtent.Container.MemberPath?.Length ?? 0)) * 2L));
            var sourceArray = new SourceExtent[sourceCount];
            for (var i = 0; i < sourceCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extent = sources[i];
                if (extent is null || !ValidSource(extent, limits.MaximumSourceNameCharacters))
                    return Fail("ScriptBundleInvalidPhysicalSource");
                budget.Add(checked(512L + extent.RelativeSource.Length * 2L + (extent.PayloadSha256?.Length ?? 0) * 2L));
                sourceArray[i] = extent;
            }
            physical = Array.AsReadOnly(sourceArray);
            var raw = new byte[input.Length];
            for (var offset = 0; offset < raw.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(CopyBlockBytes, raw.Length - offset);
                input.Slice(offset, length).CopyTo(raw.AsMemory(offset, length)); offset += length;
            }
            var cursor = new Cursor(raw, inputExtent, cancellationToken);
            if (!cursor.Take(5).SequenceEqual("QZIP\0"u8) || !cursor.Take(8).SequenceEqual("SBCHK___"u8))
                return Fail("ScriptBundleUnsupportedTag");
            var version = cursor.Word();
            if (version != 1) return Fail("ScriptBundleUnsupportedVersion");
            var bodyLength = cursor.Word();
            if (bodyLength != raw.Length - cursor.Position) return Fail("ScriptBundleBodyLengthMismatch");
            var chunkCount = cursor.Count(limits.MaximumChunks, "ScriptBundleChunkLimit");
            if (chunkCount > cursor.Remaining / 28) return Fail("ScriptBundleChunkCountBounds");
            var memberPrefix = resource.MemberPath is null ? "script/chunk/" : resource.MemberPath + "/script/chunk/";
            budget.Add(checked(64L + memberPrefix.Length * 2L + chunkCount * 16L));
            var chunks = new ScriptBundleChunk[chunkCount];
            var chunkKeys = new HashSet<ulong>(); var metadataKeys = new HashSet<ulong>();
            var totalMetadata = 0; var duplicateChunks = 0; var duplicateMetadata = 0;
            for (var index = 0; index < chunks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Add(checked(RecordBudget + (memberPrefix.Length + 20) * 2L));
                var chunkIdentity = resource with { MemberPath = memberPrefix + index.ToString(CultureInfo.InvariantCulture) };
                var start = cursor.Position; var key = cursor.Key();
                if (!chunkKeys.Add(key)) duplicateChunks++;
                var bytecodeLength = cursor.Count(limits.MaximumBytecodeBytes, "ScriptBundleBytecodeLimit");
                var bytecodeAt = cursor.Position; var bytecode = cursor.Memory(bytecodeLength);
                if (bytecode.Length < LuaHeader.Length || !bytecode.Span[..LuaHeader.Length].SequenceEqual(LuaHeader))
                    return Fail("ScriptBundleUnsupportedBytecodeHeader");
                var metadataCountAt = cursor.Position;
                var metadataCount = cursor.Count(limits.MaximumMetadataRecords, "ScriptBundleMetadataLimit");
                if (metadataCount > limits.MaximumMetadataRecords - totalMetadata)
                    return Fail("ScriptBundleMetadataLimit");
                totalMetadata += metadataCount;
                if (metadataCount > cursor.Remaining / 34) return Fail("ScriptBundleMetadataCountBounds");
                budget.Add(checked(64L + metadataCount * 16L));
                var entries = new ScriptBundleMetadata[metadataCount];
                for (var mi = 0; mi < entries.Length; mi++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.Add(checked(RecordBudget + (chunkIdentity.MemberPath!.Length + 32) * 2L));
                    var identity = chunkIdentity with { MemberPath = chunkIdentity.MemberPath + "/metadata/" + mi.ToString(CultureInfo.InvariantCulture) };
                    var entryAt = cursor.Position; var metadataKey = cursor.Key();
                    if (!metadataKeys.Add(metadataKey)) duplicateMetadata++;
                    var discriminator = cursor.Word(); var classCount = cursor.Word();
                    var tag = cursor.Take(8); var metadataVersion = cursor.Word();
                    if (discriminator != 19000 || classCount != 1 || !tag.SequenceEqual("NOCHK___"u8) || metadataVersion != 1)
                        return Fail("ScriptBundleUnsupportedMetadataProfile");
                    var flagsAt = cursor.Position; var flags = cursor.Take(2);
                    var flag0 = flags[0]; var flag1 = flags[1]; var payloadLengthAt = cursor.Position;
                    var payloadLength = cursor.Count(limits.MaximumMetadataPayloadBytes, "ScriptBundleMetadataPayloadLimit");
                    var payloadAt = cursor.Position; var payload = cursor.Memory(payloadLength);
                    entries[mi] = new(identity, metadataKey, discriminator, classCount, metadataVersion, flag0, flag1,
                        cursor.Extent(entryAt, cursor.Position - entryAt), cursor.Extent(entryAt, 34),
                        cursor.Extent(entryAt, 8), cursor.Extent(flagsAt, 2), cursor.Extent(payloadLengthAt, 4),
                        cursor.Extent(payloadAt, payloadLength), payload);
                }
                chunks[index] = new(chunkIdentity, key, cursor.Extent(start, cursor.Position - start),
                    cursor.Extent(start, 12), cursor.Extent(start, 8), cursor.Extent(bytecodeAt, bytecodeLength),
                    cursor.Extent(metadataCountAt, 4), Array.AsReadOnly(entries), bytecode);
            }
            if (cursor.Remaining != 0) return Fail("ScriptBundleTrailingBytes");
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var offset = 0; offset < raw.Length; offset += CopyBlockBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(raw.AsSpan(offset, Math.Min(CopyBlockBytes, raw.Length - offset)));
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            steps[0] = steps[0] with { PayloadSha256 = digest };
            var diagnostics = new List<string>();
            if (duplicateChunks > 0) diagnostics.Add("DuplicateScriptChunkKey: " + duplicateChunks.ToString(CultureInfo.InvariantCulture));
            if (duplicateMetadata > 0) diagnostics.Add("DuplicateScriptMetadataKey: " + duplicateMetadata.ToString(CultureInfo.InvariantCulture));
            var document = new ScriptBundleDocument(resource, revision, inputExtent, cursor.Extent(0, 25),
                cursor.Extent(0, raw.Length), version, bodyLength, Array.AsReadOnly(chunks), physical, raw);
            cancellationToken.ThrowIfCancellationRequested();
            return new(document, resource, revision, physical, steps, diagnostics, [], unknown);
        }
        catch (ReadFailure failure) { return Fail(failure.Code); }
        catch (OverflowException) { return Fail("ScriptBundleIntegerOverflow"); }
    }

    private static bool ValidLimits(ScriptBundleLimits limits) =>
        limits.MaximumInputBytes is >= 25 and <= 128 * 1024 * 1024 &&
        limits.MaximumBytecodeBytes is >= 12 and <= 128 * 1024 * 1024 &&
        limits.MaximumMetadataPayloadBytes is >= 0 and <= 128 * 1024 * 1024 &&
        limits.MaximumChunks is >= 0 and <= 1_000_000 &&
        limits.MaximumMetadataRecords is >= 0 and <= 1_000_000 &&
        limits.MaximumPhysicalSources is >= 0 and <= 16_384 &&
        limits.MaximumIdentityCharacters is > 0 and <= 16_384 &&
        limits.MaximumSourceNameCharacters is > 0 and <= 16_384 &&
        limits.MaximumRetainedBytes is >= 0 and <= 512L * 1024 * 1024;

    private static bool ValidToken(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters && !value.Any(char.IsControl);
    private static bool ValidIdentity(ResourceIdentity identity, int maximumCharacters) =>
        identity.Ordinal >= 0 && ValidToken(identity.IndexContentId, maximumCharacters) &&
        (identity.MemberPath is null || ValidRelative(identity.MemberPath, maximumCharacters));
    private static bool Owns(ResourceIdentity parent, ResourceIdentity child) =>
        parent.IndexContentId == child.IndexContentId && parent.Ordinal == child.Ordinal &&
        (parent.MemberPath is null || parent.MemberPath == child.MemberPath ||
         child.MemberPath is not null && child.MemberPath.StartsWith(parent.MemberPath + "/", StringComparison.Ordinal));
    private static bool ValidRelative(string? value, int maximumCharacters)
    {
        if (!ValidToken(value, maximumCharacters) || value![0] is '/' or '\\' || value.Contains(':')) return false;
        var start = 0;
        for (var at = 0; at <= value.Length; at++)
        {
            if (at < value.Length && value[at] is not ('/' or '\\')) continue;
            var segment = value.AsSpan(start, at - start);
            if (segment.IsEmpty || segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan())) return false;
            start = at + 1;
        }
        return true;
    }
    private static bool ValidSource(SourceExtent extent, int maximumCharacters) =>
        ValidRelative(extent.RelativeSource, maximumCharacters) && extent.Offset >= 0 && extent.Length >= 0 &&
        extent.Offset <= long.MaxValue - extent.Length &&
        (extent.PayloadSha256 is null || extent.PayloadSha256.Length == 64 &&
         extent.PayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'));

    private sealed class Budget(long maximum)
    {
        private long used;
        public void Add(long bytes)
        {
            if (bytes < 0 || bytes > maximum - used) throw new ReadFailure("ScriptBundleMemoryLimit");
            used += bytes;
        }
    }
    private ref struct Cursor(ReadOnlyMemory<byte> data, DecodedExtent parent, CancellationToken token)
    {
        private int position;
        public readonly int Position => position;
        public readonly int Remaining => data.Length - position;
        public ReadOnlySpan<byte> Take(int length) => Memory(length).Span;
        public ReadOnlyMemory<byte> Memory(int length)
        {
            token.ThrowIfCancellationRequested();
            if (length < 0 || length > data.Length - position) throw new ReadFailure("ScriptBundleTruncatedData");
            var result = data.Slice(position, length); position += length; return result;
        }
        public uint Word() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ulong Key() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public int Count(int maximum, string code)
        {
            var count = Word();
            if (count > maximum || count > int.MaxValue) throw new ReadFailure(code);
            return (int)count;
        }
        public readonly DecodedExtent Extent(int offset, int length) => new(parent.Container, checked(parent.Offset + offset), length);
    }
    private sealed class ReadFailure(string code) : Exception(code) { public string Code { get; } = code; }
}
