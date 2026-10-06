using System.Buffers.Binary;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed class SceneEnvironmentReader
{
    public const string Version = "scene-global-v17-1";
    public const uint NativeType = 2202;

    public bool TryRead(NativeObjectFrame frame, out SceneEnvironmentRecord? environment, out string? diagnostic,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); token.ThrowIfCancellationRequested();
        environment = null; diagnostic = null;
        var bytes = frame.RawPayload.Span;
        if (frame.PayloadExtent.Length != bytes.Length || frame.PayloadExtent.Offset < 0 ||
            frame.PayloadExtent.Offset > long.MaxValue - bytes.Length ||
            frame.Identity.IndexContentId != frame.PayloadExtent.Container.IndexContentId ||
            frame.Identity.Ordinal != frame.PayloadExtent.Container.Ordinal)
        { diagnostic = "SceneEnvironmentSourceMismatch"; return false; }
        if (frame.NativeType != NativeType || frame.ClassCount != 1 || bytes.Length != 167 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 || !bytes.Slice(4, 8).SequenceEqual("SCN_GLBL"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 17)
        { diagnostic = "SceneEnvironmentProfileUnsupported"; return false; }
        var references = new[] { SceneEntityReader.ReadReference(bytes, 151, frame.Identity),
            SceneEntityReader.ReadReference(bytes, 159, frame.Identity) }.OfType<ResourceReference>().ToArray();
        environment = new(frame.Identity, 17, frame.PayloadExtent, references, frame.RawPayload);
        return true;
    }
}
