namespace Deviant.Assets;

/// <summary>Neutral native-object frame data shared by container readers and format facades.</summary>
public sealed record NativeObjectFrame(ResourceIdentity Identity, int DirectoryIndex,
    uint NativeType, uint NativeId, DecodedExtent DirectoryEntryExtent,
    DecodedExtent HeaderExtent, DecodedExtent PayloadExtent, byte[] RawHeader,
    ReadOnlyMemory<byte> RawPayload, byte WrapperFlag, uint WrapperCount, uint WrapperKey,
    uint? ClassCount);

/// <summary>Neutral directory document retaining raw directory, frame, body, and remainder data.</summary>
public sealed record NativeObjectDirectoryDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent DirectoryExtent, DecodedExtent BodyExtent, byte[] RawDirectory,
    ReadOnlyMemory<byte> RawBody, IReadOnlyList<NativeObjectFrame> Objects,
    DecodedExtent? RemainderExtent, ReadOnlyMemory<byte> RemainderBytes)
{
    public bool IsMetadataOnly { get; init; }
    public DecodedExtent? DeclaredRemainderExtent { get; init; }
    public IReadOnlyList<NativeObjectFrame> FindNative(uint nativeType, uint nativeId) =>
        Objects.Where(item => item.NativeType == nativeType && item.NativeId == nativeId).ToArray();
}
