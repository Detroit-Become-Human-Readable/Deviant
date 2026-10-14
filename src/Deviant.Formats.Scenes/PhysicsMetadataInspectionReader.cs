using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Retains each bounded physics metadata stage for callers that need partial inspection results.</summary>
public sealed record PhysicsMetadataInspectionResult(
    DecodeResult<PhysicsEnvelopeDocument> Envelope,
    DecodeResult<PhysicsTaggedMetadataDocument>? TaggedMetadata,
    DecodeResult<PhysicsTypeDeclarationsDocument>? TypeDeclarations,
    DecodeResult<PhysicsTypeLayoutsDocument>? TypeLayouts,
    DecodeResult<PhysicsIndexedMetadataDocument>? IndexedMetadata)
{
    public DecodeResult<PhysicsDataStorageDocument>? DataStorage { get; init; }
}

/// <summary>Runs the accepted envelope, framing, declarations, layouts, and indexed-metadata readers in order.</summary>
public sealed class PhysicsMetadataInspectionReader
{
    private readonly PhysicsEnvelopeReader envelopeReader = new();
    private readonly PhysicsTaggedMetadataReader taggedReader = new();
    private readonly PhysicsTypeDeclarationsReader declarationsReader = new();
    private readonly PhysicsTypeLayoutsReader layoutsReader = new();
    private readonly PhysicsIndexedMetadataReader indexedReader = new();
    private readonly PhysicsDataStorageReader storageReader = new();

    public PhysicsMetadataInspectionResult Read(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested();

        var envelope = envelopeReader.Read(frame, revision, physicalExtents, precedingSteps, token: token);
        token.ThrowIfCancellationRequested();
        if (envelope.Value is not { } envelopeDocument)
            return new(envelope, null, null, null, null);

        var tagged = taggedReader.Read(envelopeDocument, physicalExtents, envelope.Steps, token: token);
        token.ThrowIfCancellationRequested();
        if (tagged.Value is not { } taggedDocument)
            return new(envelope, tagged, null, null, null);

        var declarations = declarationsReader.Read(taggedDocument, physicalExtents, tagged.Steps, token: token);
        token.ThrowIfCancellationRequested();
        if (declarations.Value is not { } declarationDocument)
            return new(envelope, tagged, declarations, null, null);

        var layouts = layoutsReader.Read(taggedDocument, declarationDocument, physicalExtents,
            declarations.Steps, token: token);
        token.ThrowIfCancellationRequested();
        if (layouts.Value is not { } layoutDocument)
            return new(envelope, tagged, declarations, layouts, null);

        var indexed = indexedReader.Read(taggedDocument, declarationDocument, layoutDocument,
            physicalExtents, layouts.Steps, token: token);
        token.ThrowIfCancellationRequested();
        if (indexed.Value is not { } indexedDocument)
            return new(envelope, tagged, declarations, layouts, indexed);
        var storage = storageReader.Read(layoutDocument, indexedDocument, token: token);
        token.ThrowIfCancellationRequested();
        return new(envelope, tagged, declarations, layouts, indexed) { DataStorage = storage };
    }
}
