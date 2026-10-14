using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;
using System.Numerics;

namespace Deviant.Export.Models;

public delegate IDisposable FbxExportMemoryReservation(long bytes, string category);

public enum FbxMediaPackaging
{
    Sidecars,
    Embedded
}

public enum FbxCoordinateMetadataPolicy
{
    VerifiedBasis,
    PreserveSourceNumbers
}

/// <summary>Identifies the geometry detail selected by the caller; the exporter never chooses an LOD.</summary>
public sealed record FbxGeometrySelection(ResourceIdentity Identity, string Key,
    int? LodIndex = null, int? GroupIndex = null);

public sealed record FbxMaterialReferenceJoin(ResourceReference Reference, ReferenceResolution Resolution);

public sealed record FbxMaterialTextureInput(int SlotOrdinal, ResourceReference Reference, ReferenceResolution Resolution,
    DdsTextureDocument Document, TextureSurface Surface, SourceRevision? ExpectedRevision,
    Func<CancellationToken, Task> ValidateSourceRevision, FbxTextureDocumentJoin? DocumentJoin = null)
{
    public Func<string, string, CancellationToken, Task<TextureExportResult>>? PublishPreparedMedia { get; init; }
}

public sealed record FbxTextureDocumentJoin(ResourceIdentity ResolvedIdentity, ResourceIdentity DocumentIdentity,
    SourceRevision Revision, RelationshipEvidence Evidence);

/// <summary>A sampled source retains its verified document metadata after its pixels have been consumed.</summary>
public sealed record FbxGraphTextureSourceInput(int SlotOrdinal, ResourceReference Reference,
    ReferenceResolution Resolution, ResourceIdentity NativeIdentity, ResourceIdentity DocumentIdentity,
    SourceRevision Revision, string DocumentSha256, TextureSubresourceSelection Subresource,
    int Width, int Height, RelationshipEvidence Evidence, Func<CancellationToken, Task> ValidateSourceRevision);

public sealed record FbxMaterialExportInput(MaterialAsset Material,
    IReadOnlyList<FbxMaterialReferenceJoin> SubmeshReferences,
    IReadOnlyList<FbxMaterialTextureInput> Textures,
    Func<CancellationToken, Task> ValidateSourceRevision)
{
    public IReadOnlyList<FbxBakedMaterialMapInput> BakedMaps { get; init; } = [];
    public IReadOnlyList<string> BakingDiagnostics { get; init; } = [];
    public IReadOnlyList<FbxGraphTextureSourceInput> GraphSources { get; init; } = [];
    public IReadOnlyList<FbxStagedMaterialMapInput> StagedMaps { get; init; } = [];
    public IEnumerable<FbxMaterialMapInput> AllMaps => BakedMaps.Cast<FbxMaterialMapInput>().Concat(StagedMaps);
}

/// <summary>A derived map retains its material, geometry and sampled texture provenance. Opacity maps encode linear RGB transparency (one minus coverage).</summary>
public abstract record FbxMaterialMapInput(MaterialTextureRole Role, int TextureCoordinateSet,
    string GraphSha256, IReadOnlyList<string> PrimitiveKeys, IReadOnlyList<FbxBakeTextureSource> Sources,
    Func<CancellationToken, Task> ValidateSources)
{
    public IReadOnlyList<Vector2>? PolygonCornerCoordinates { get; init; }
    public abstract ResourceIdentity? Identity { get; }
    public abstract SourceRevision? Revision { get; }
    public abstract string DdsSha256 { get; }
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract bool IsSrgb { get; }
}

public sealed record FbxBakedMaterialMapInput(MaterialTextureRole Role, int TextureCoordinateSet,
    DdsTextureDocument Document, TextureSurface Surface, string GraphSha256,
    IReadOnlyList<string> PrimitiveKeys, IReadOnlyList<FbxBakeTextureSource> Sources,
    Func<CancellationToken, Task> ValidateSources)
    : FbxMaterialMapInput(Role, TextureCoordinateSet, GraphSha256, PrimitiveKeys, Sources, ValidateSources)
{
    public override ResourceIdentity? Identity => Document.Identity;
    public override SourceRevision? Revision => Document.Revision;
    public override string DdsSha256 => Document.ContentSha256;
    public override int Width => Surface.Width;
    public override int Height => Surface.Height;
    public override bool IsSrgb => Surface.IsSrgb;
}

/// <summary>An encoded map can be published without retaining its decoded image arrays.</summary>
public sealed record FbxStagedMaterialMapInput(MaterialTextureRole Role, int TextureCoordinateSet,
    ResourceIdentity MaterialIdentity, SourceRevision MaterialRevision, string DerivedDdsSha256,
    int PixelWidth, int PixelHeight, bool EncodedSrgb, string Path, long Length, string PngSha256,
    string GraphSha256, IReadOnlyList<string> PrimitiveKeys, IReadOnlyList<FbxBakeTextureSource> Sources,
    Func<CancellationToken, Task> ValidateSources)
    : FbxMaterialMapInput(Role, TextureCoordinateSet, GraphSha256, PrimitiveKeys, Sources, ValidateSources)
{
    /// <summary>Opens cache-leased, pinned content. The owner retains the lease through publication.</summary>
    public Func<CancellationToken, Task<Stream>>? OpenPreparedContent { get; init; }
    public override ResourceIdentity? Identity => MaterialIdentity;
    public override SourceRevision? Revision => MaterialRevision;
    public override string DdsSha256 => DerivedDdsSha256;
    public override int Width => PixelWidth;
    public override int Height => PixelHeight;
    public override bool IsSrgb => EncodedSrgb;
}

public sealed record FbxBakeTextureSource(int SlotOrdinal, ResourceIdentity Identity,
    SourceRevision Revision, string PayloadSha256, TextureSubresourceSelection Subresource)
{
    public NativeTextureSamplingProfile? Sampling { get; init; }
}

/// <summary>Exporter-specific input; reservation requests must use the owning asset session's budget.</summary>
public sealed record FbxModelExportInput(DecodeResult<GeometryAsset> Geometry, FbxGeometrySelection Selection,
    FbxExportMemoryReservation ReserveMemory, IReadOnlyList<FbxMaterialExportInput>? Materials = null,
    Func<CancellationToken, Task>? ValidateGeometrySourceRevision = null,
    FbxSkeletonExportInput? Skeleton = null,
    FbxCoordinateMetadataPolicy CoordinateMetadata = FbxCoordinateMetadataPolicy.VerifiedBasis)
{
    public IReadOnlyDictionary<string, FbxMaterialExportInput> MaterialsBySubmesh { get; init; } =
        new Dictionary<string, FbxMaterialExportInput>();
    public FbxAnimationExportInput? Animation { get; init; }
}

public sealed record FbxBakedMaterialMapExportResult(ResourceIdentity MaterialIdentity, SourceRevision MaterialRevision,
    MaterialTextureRole Role, int TextureCoordinateSet, IReadOnlyList<string> PrimitiveKeys,
    string GraphSha256, string DerivedDdsSha256, IReadOnlyList<FbxBakeTextureSource> Sources,
    string RelativePath, long Length, string Sha256, bool OutputIsSrgb);

/// <summary>Full affine bind matrix, in row-major storage with row-vector multiplication.</summary>
public sealed record FbxBindMatrix(IReadOnlyList<double> Elements);

public sealed record FbxJointExportInput(string Key, string? ParentKey, string? Name,
    FbxBindMatrix BindWorld, SourceTransform PoseLocal);

public sealed record FbxJointInfluence(int JointOrdinal, double Weight);

/// <summary>Influence rows follow the primitive's control-point order. Duplicate joints are merged and weights normalized.</summary>
public sealed record FbxSkinExportInput(string PrimitiveKey, FbxBindMatrix MeshBindWorld,
    IReadOnlyList<IReadOnlyList<FbxJointInfluence>> VertexInfluences);

/// <summary>Caller-qualified hierarchy and bind context for the selected mesh detail.</summary>
public sealed record FbxSkeletonExportInput(ResourceIdentity Identity, SourceRevision Revision,
    IReadOnlyList<FbxJointExportInput> Joints, IReadOnlyList<FbxSkinExportInput> Skins,
    Func<CancellationToken, Task> ValidateSourceRevision);

/// <summary>Caller-sampled local transforms in seconds. FBX interpolates emitted XYZ channels linearly;
/// callers must sample their source interpolation to the required motion tolerance.</summary>
public sealed record FbxJointAnimationKey(double TimeSeconds, SourceTransform LocalTransform);

public sealed record FbxJointAnimationTrack(string JointKey, IReadOnlyList<FbxJointAnimationKey> Keys);

/// <summary>One clip whose joint targets have been qualified against the exact exported skeleton.</summary>
public sealed record FbxAnimationExportInput(ResourceIdentity Identity, SourceRevision Revision, string Name,
    ResourceIdentity SkeletonIdentity, SourceRevision SkeletonRevision, double DurationSeconds,
    IReadOnlyList<FbxJointAnimationTrack> Tracks, Func<CancellationToken, Task> ValidateSourceRevision);

public sealed record FbxGeometryExportOptions(bool Overwrite = false, int MaximumTriangles = 5_000_000,
    int MaximumNodes = 1_000_000, int MaximumVertices = 20_000_000,
    int MaximumSubmeshes = 1_000_000, int MaximumMaterials = 4096,
    int MaximumMaterialReferences = 1_000_000,
    int MaximumTextures = 4096, long MaximumTexturePixels = 256L * 1024 * 1024,
    long MaximumMediaBytes = 2L * 1024 * 1024 * 1024,
    FbxMediaPackaging MediaPackaging = FbxMediaPackaging.Sidecars,
    long MaximumBinaryBytes = 2L * 1024 * 1024 * 1024,
    int MaximumSkinInfluences = 40_000_000, int MaximumAnimationKeys = 2_000_000);

public sealed record FbxGeometryExportLoss(string Code, string Subject, string Detail,
    IReadOnlyList<ResourceReference>? References = null);

public sealed record FbxGeometryExportReport(ResourceIdentity Identity, SourceRevision Revision,
    FbxGeometrySelection Selection, string OutputFileName, string OutputSha256, int MeshCount, int NodeCount, int MaterialCount,
    IReadOnlyList<FbxGeometryExportLoss> Losses,
    IReadOnlyList<FbxMaterialTextureExportResult>? MaterialTextures = null,
    FbxMediaPackaging MediaPackaging = FbxMediaPackaging.Sidecars,
    FbxCoordinateMetadataPolicy CoordinateMetadata = FbxCoordinateMetadataPolicy.VerifiedBasis)
{
    public IReadOnlyList<FbxBakedMaterialMapExportResult> DerivedMaterialMaps { get; init; } = [];
}

public sealed record FbxMaterialTextureExportResult(ResourceIdentity MaterialIdentity,
    SourceRevision MaterialRevision, int SlotOrdinal, MaterialTextureRole Role,
    ResourceReference TextureReference, ResourceIdentity TextureIdentity, SourceRevision TextureRevision,
    string SourcePayloadSha256, TextureSubresourceSelection SelectedSubresource,
    int TextureCoordinateSet, Vector2 UvScale, Vector2 UvOffset,
    string RelativePath, long Length, string Sha256, string PixelFormat,
    bool SourceIsHdr, bool OutputIsHdr, bool SourceIsSrgb, bool OutputIsSrgb,
    TextureDisplayTransform? DisplayTransform, string Losses);

public sealed class FbxGeometryExportException(string code, string message,
    IReadOnlyList<FbxGeometryExportLoss> losses, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
    public IReadOnlyList<FbxGeometryExportLoss> Losses { get; } = losses;
}
