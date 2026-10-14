using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Textures;

public sealed record TextureContainerObjectFrame(ResourceIdentity Identity, int DirectoryIndex,
    uint NativeType, uint NativeId, DecodedExtent DirectoryEntryExtent,
    DecodedExtent HeaderExtent, DecodedExtent PayloadExtent, byte[] RawHeader,
    ReadOnlyMemory<byte> RawPayload, byte WrapperFlag, uint WrapperCount, uint WrapperKey,
    uint? ClassCount);

public sealed record TextureContainerDirectoryDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent DirectoryExtent, DecodedExtent BodyExtent, byte[] RawDirectory,
    ReadOnlyMemory<byte> RawBody, IReadOnlyList<TextureContainerObjectFrame> Objects,
    DecodedExtent? RemainderExtent, ReadOnlyMemory<byte> RemainderBytes)
{
    public bool IsMetadataOnly { get; init; }
    public DecodedExtent? DeclaredRemainderExtent { get; init; }
    public IReadOnlyList<TextureContainerObjectFrame> FindNative(uint nativeType, uint nativeId) =>
        Objects.Where(item => item.NativeType == nativeType && item.NativeId == nativeId).ToArray();
}

/// <summary>Compatibility facade for the native-object directory reader.</summary>
public sealed class TextureContainerDirectoryReader
{
    public const string Version = NativeObjectDirectoryReader.Version;
    public const int MaximumBodyBytes = NativeObjectDirectoryReader.MaximumBodyBytes;
    public const int MaximumObjects = NativeObjectDirectoryReader.MaximumObjects;
    public const int MaximumWrapperEntries = NativeObjectDirectoryReader.MaximumWrapperEntries;

    private readonly NativeObjectDirectoryReader inner = new();

    public DecodeResult<TextureContainerDirectoryDocument> Parse(ReadOnlySpan<byte> decodedDirectoryPrefix,
        ReadOnlyMemory<byte> logicalDcDataBody, ResourceIdentity container, SourceRevision revision,
        DecodedExtent logicalBodyExtent, IReadOnlyList<SourceExtent> originalPhysicalExtents,
        CancellationToken cancellationToken = default)
    {
        var parsed = inner.Parse(decodedDirectoryPrefix, logicalDcDataBody, container, revision,
            logicalBodyExtent, originalPhysicalExtents, cancellationToken);
        var value = parsed.Value is { } document ? Convert(document) : null;
        var steps = parsed.Steps.Select(step =>
            step with { Decoder = nameof(TextureContainerDirectoryReader) }).ToArray();
        return new(value, parsed.Identity, parsed.Revision, parsed.Extents, steps, parsed.Diagnostics,
            parsed.UnresolvedReferences, parsed.UnknownFields);
    }

    public static TextureContainerDirectoryDocument Convert(NativeObjectDirectoryDocument document)
    {
        var objects = document.Objects.Select(Convert).ToList().AsReadOnly();
        return new(document.Identity, document.Revision, document.DirectoryExtent, document.BodyExtent,
            document.RawDirectory, document.RawBody, objects, document.RemainderExtent, document.RemainderBytes)
            { IsMetadataOnly = document.IsMetadataOnly, DeclaredRemainderExtent = document.DeclaredRemainderExtent };
    }

    private static TextureContainerObjectFrame Convert(NativeObjectFrame frame) =>
        new(frame.Identity, frame.DirectoryIndex, frame.NativeType, frame.NativeId,
            frame.DirectoryEntryExtent, frame.HeaderExtent, frame.PayloadExtent, frame.RawHeader,
            frame.RawPayload, frame.WrapperFlag, frame.WrapperCount, frame.WrapperKey, frame.ClassCount);
}
