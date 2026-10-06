using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Scenes;

/// <summary>Inspects dependency framing and archive-key candidates; it does not decode scene entities.</summary>
public sealed class SceneDependencyReader
{
    public const string Version = "pc-v18-dependencies-1";

    public DecodeResult<SceneDependencyInspection> Inspect(ReadOnlySpan<byte> bytes, ArchiveSnapshot archive,
        ResourceIdentity owner, string relativeSource, SceneDependencyLimits? limits = null,
        CancellationToken token = default)
    {
        limits ??= new();
        ValidateLimits(limits);
        ValidateOwner(archive, owner);
        ValidateRelativeSource(relativeSource);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var sourceLength = bytes.Length;
        DecodeResult<SceneDependencyInspection> Fail(string code) =>
            new(null, owner, revision, [new(relativeSource, 0, sourceLength)],
                [new(nameof(SceneDependencyReader), Version, "big-endian")], [code], [], new Dictionary<string, string>());

        token.ThrowIfCancellationRequested();
        var selected = new List<(DependencyWords Words, int Offset)>();
        var selectedOffset = 0;
        var selectedLength = 0;
        var walk = WalkOwners(bytes, archive, limits, token, (source, row) =>
        {
            if (row.Ordinal != owner.Ordinal) return;
            if (row.ReferenceCount > limits.MaximumSelectedReferences)
                throw new DependencyWalkFailureException("SelectedDependencyReferenceLimit");
            selectedOffset = row.OwnerOffset;
            selectedLength = row.OwnerLength;
            for (var i = 0; i < row.ReferenceCount; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var start = checked(row.ReferenceOffset + i * 20);
                selected.Add((ReadWords(source, start), start));
            }
        });
        if (walk.ErrorCode is { } error) return Fail(error);

        var byKey = BuildTopLevelMatches(archive);
        var matches = selected.Select(item => CreateCandidate(item.Words, item.Offset, owner,
            relativeSource, byKey)).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var selectedRow = archive.Records[(int)owner.Ordinal];
        var inspection = new SceneDependencyInspection(owner, selectedRow.Type, selectedRow.Id,
            walk.TableRows, walk.TableReferences, new(relativeSource, selectedOffset, selectedLength), matches);
        return new(inspection, owner, revision, [new(relativeSource, 0, bytes.Length, digest)],
            [new(nameof(SceneDependencyReader), Version, "big-endian; candidate-key=words0,1", digest)], [],
            matches.Select(m => m.Candidate).ToArray(),
            new Dictionary<string, string>
            {
                ["referenceWords2Through4"] = "Retained verbatim in each reference.",
                ["relationshipKind"] = "Unknown",
                ["nestedTargetResolution"] = "Unavailable"
            });
    }

    public DependencyOwnerPage FindOwners(ReadOnlySpan<byte> bytes, ArchiveSnapshot archive,
        string relativeSource, IReadOnlySet<uint> referenceTypes, long offsetOrdinal = 0,
        int maximumOwners = 100, int maximumMatchingReferencesPerOwner = 256,
        SceneDependencyLimits? limits = null, CancellationToken token = default)
    {
        limits ??= new();
        ValidateLimits(limits);
        ValidateRelativeSource(relativeSource);
        ArgumentNullException.ThrowIfNull(referenceTypes);
        if (referenceTypes.Count == 0) throw new ArgumentException("Supply at least one reference type.", nameof(referenceTypes));
        if (offsetOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(offsetOrdinal));
        if (maximumOwners is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumOwners));
        if (maximumMatchingReferencesPerOwner is < 1 || maximumMatchingReferencesPerOwner > limits.MaximumMatchingReferencesPerOwner)
            throw new ArgumentOutOfRangeException(nameof(maximumMatchingReferencesPerOwner));

        token.ThrowIfCancellationRequested();
        IReadOnlyDictionary<(uint Type, uint Id), ResourceIdentity[]>? byKey = null;
        var owners = new List<DependencyOwnerMatch>(Math.Min(maximumOwners, 256));
        var hasMore = false;
        long? nextOrdinal = null;
        var walk = WalkOwners(bytes, archive, limits, token, (source, row) =>
        {
            token.ThrowIfCancellationRequested();
            var collect = row.Ordinal >= offsetOrdinal && owners.Count < maximumOwners;
            List<DependencyCandidate>? matches = null;
            var totalMatching = 0;
            for (var i = 0; i < row.ReferenceCount; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var referenceOffset = checked(row.ReferenceOffset + i * 20);
                var words = ReadWords(source, referenceOffset);
                if (!referenceTypes.Contains(words.Word0)) continue;
                totalMatching++;
                if (!collect) continue;
                byKey ??= BuildTopLevelMatches(archive);
                matches ??= [];
                if (matches.Count < maximumMatchingReferencesPerOwner)
                    matches.Add(CreateCandidate(words, referenceOffset,
                        new(archive.IndexContentId, row.Ordinal), relativeSource, byKey));
            }
            if (totalMatching == 0 || row.Ordinal < offsetOrdinal) return;
            if (owners.Count >= maximumOwners)
            {
                hasMore = true;
                nextOrdinal ??= row.Ordinal;
                return;
            }
            owners.Add(new(new(archive.IndexContentId, row.Ordinal), row.Type, row.Id,
                new(relativeSource, row.OwnerOffset, row.OwnerLength), totalMatching,
                totalMatching > maximumMatchingReferencesPerOwner,
                matches is null ? Array.Empty<DependencyCandidate>() : matches.AsReadOnly()));
        });
        if (walk.ErrorCode is { } error)
            throw new ArchiveException(error, DiagnosticMessage(error));
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(new(archive.IndexContentId, archive.PhysicalSourceStamp),
            new(relativeSource, 0, bytes.Length, digest), digest, walk.TableRows,
            walk.TableReferences, owners.AsReadOnly(), hasMore, nextOrdinal);
    }

    private static DependencyCandidate CreateCandidate(DependencyWords raw, long offset, ResourceIdentity owner,
        string relativeSource, IReadOnlyDictionary<(uint Type, uint Id), ResourceIdentity[]> byKey)
    {
        var candidate = new ResourceReference(IdentifierNamespace.Archive,
            raw.Word1.ToString(CultureInfo.InvariantCulture), owner, raw.Word0);
        var identities = byKey.GetValueOrDefault((raw.Word0, raw.Word1)) ?? [];
        var status = identities.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous };
        return new(raw, candidate, new(status, identities), new(relativeSource, offset, 20));
    }

    private static IReadOnlyDictionary<(uint Type, uint Id), ResourceIdentity[]> BuildTopLevelMatches(ArchiveSnapshot archive) =>
        archive.Records.GroupBy(r => (r.Type, r.Id)).ToDictionary(g => g.Key,
            g => g.Select(r => new ResourceIdentity(archive.IndexContentId, r.Ordinal)).ToArray());

    private delegate void DependencyOwnerVisitor(ReadOnlySpan<byte> bytes, DependencyOwnerRow row);

    private static DependencyWalkResult WalkOwners(ReadOnlySpan<byte> bytes, ArchiveSnapshot archive,
        SceneDependencyLimits limits, CancellationToken token, DependencyOwnerVisitor visitor)
    {
        if (bytes.Length > limits.MaximumBytes) return DependencyWalkResult.Fail("DependencyByteLimit");
        if (bytes.Length < 28) return DependencyWalkResult.Fail("TruncatedDependencyHeader");
        if (!bytes[..20].SequenceEqual(IndexLayout.Magic)) return DependencyWalkResult.Fail("InvalidDependencySignature");
        var version = Word(bytes, 20);
        if (version != 18 || archive.Version != 18) return DependencyWalkResult.Fail("UnsupportedDependencyVersion");
        var count = Word(bytes, 24);
        if (count > limits.MaximumRows) return DependencyWalkResult.Fail("DependencyRowLimit");
        if (count != archive.Records.Count) return DependencyWalkResult.Fail("DependencyOwnerCountMismatch");

        var offset = 28;
        long totalReferences = 0;
        try
        {
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                if ((ordinal & 1023) == 0) token.ThrowIfCancellationRequested();
                if (bytes.Length - offset < 16) return DependencyWalkResult.Fail("TruncatedDependencyOwner");
                var type = Word(bytes, offset); var flags = Word(bytes, offset + 4);
                var id = Word(bytes, offset + 8); var references = Word(bytes, offset + 12);
                var row = archive.Records[ordinal];
                if (row.Ordinal != ordinal || row.Type != type || row.Flags != flags || row.Id != id)
                    return DependencyWalkResult.Fail("DependencyOwnerKeyMismatch");
                totalReferences += references;
                if (totalReferences > limits.MaximumReferences) return DependencyWalkResult.Fail("DependencyReferenceLimit");
                var referenceBytes = (long)references * 20;
                if (referenceBytes > bytes.Length - offset - 16) return DependencyWalkResult.Fail("TruncatedDependencyReferences");
                visitor(bytes, new(ordinal, type, id, references, offset, checked(offset + 16), checked(16 + (int)referenceBytes)));
                offset = checked(offset + 16 + (int)referenceBytes);
            }
        }
        catch (DependencyWalkFailureException error)
        {
            return DependencyWalkResult.Fail(error.Code);
        }
        if (offset != bytes.Length) return DependencyWalkResult.Fail("DependencyTrailingBytes");
        return new(null, count, totalReferences);
    }

    private static void ValidateOwner(ArchiveSnapshot archive, ResourceIdentity owner)
    {
        if (owner.IndexContentId != archive.IndexContentId || owner.MemberPath is not null ||
            owner.Ordinal < 0 || owner.Ordinal >= archive.Records.Count || archive.Records[(int)owner.Ordinal].Ordinal != owner.Ordinal)
            throw new ArgumentException("Select a top-level resource from the supplied archive.", nameof(owner));
    }

    private static void ValidateRelativeSource(string relativeSource)
    {
        if (string.IsNullOrWhiteSpace(relativeSource) || relativeSource.Contains(':') || relativeSource.StartsWith('/') ||
            relativeSource.StartsWith('\\') || relativeSource.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("Supply a relative dependency source name.", nameof(relativeSource));
    }

    private static void ValidateLimits(SceneDependencyLimits limits)
    {
        if (limits.MaximumBytes < 28 || limits.MaximumRows < 0 || limits.MaximumReferences < 0 ||
            limits.MaximumSelectedReferences < 0 || limits.MaximumMatchingReferencesPerOwner < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private static string DiagnosticMessage(string code) => code switch
    {
        "DependencyByteLimit" => "Dependency input exceeds the configured byte limit.",
        "DependencyRowLimit" => "Dependency owner count exceeds the configured row limit.",
        "DependencyReferenceLimit" => "Dependency reference count exceeds the configured limit.",
        "DependencyTrailingBytes" => "Dependency input has trailing bytes.",
        _ => code
    };

    private static DependencyWords ReadWords(ReadOnlySpan<byte> bytes, int offset) =>
        new(Word(bytes, offset), Word(bytes, offset + 4), Word(bytes, offset + 8),
            Word(bytes, offset + 12), Word(bytes, offset + 16));

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);

    private sealed record DependencyOwnerRow(int Ordinal, uint Type, uint Id, uint ReferenceCount,
        int OwnerOffset, int ReferenceOffset, int OwnerLength);
    private sealed record DependencyWalkResult(string? ErrorCode, uint TableRows, long TableReferences)
    {
        public static DependencyWalkResult Fail(string code) => new(code, 0, 0);
    }
    private sealed class DependencyWalkFailureException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
