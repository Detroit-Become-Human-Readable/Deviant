using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

/// <summary>Qualification required to load one type-evidence dataset.</summary>
public sealed record TypeRegistryDatasetExpectation(SourceRevision CurrentRevision,
    string SelectedExecutableSha256, string? ExpectedDatasetSha256 = null);

/// <summary>Hard limits applied before JSON object materialization.</summary>
public sealed record TypeRegistryDatasetLimits(
    int MaximumRawBytes = 8 * 1024 * 1024,
    int MaximumDefinitions = 4096,
    int MaximumFieldsAndEvidence = 65536,
    long MaximumMetadataBytes = 64L * 1024 * 1024,
    int MaximumDepth = 32,
    int MaximumJsonTokens = 1_000_000,
    int MaximumStringBytes = 64 * 1024,
    int MaximumArrayItems = 65536)
{
    internal void Validate()
    {
        if (MaximumRawBytes < 1 || MaximumDefinitions < 1 || MaximumFieldsAndEvidence < 1 ||
            MaximumMetadataBytes < 1 || MaximumDepth < 1 || MaximumJsonTokens < 1 ||
            MaximumStringBytes < 1 || MaximumArrayItems < 1)
            throw new ArgumentOutOfRangeException(nameof(TypeRegistryDatasetLimits), "All dataset limits must be positive.");
    }
}

/// <summary>Owns a qualified registry and its shared-memory reservation.</summary>
public sealed class TypeRegistryDatasetLease : IDisposable
{
    private readonly object gate = new();
    private readonly SourceRevision revision;
    private readonly string executableSha256;
    private readonly string datasetSha256;
    private readonly int definitionCount;
    private TypeRegistry? registry;
    private AssetMemoryBudget.Reservation? reservation;

    internal TypeRegistryDatasetLease(SourceRevision revision, string executableSha256,
        string datasetSha256, int definitionCount, TypeRegistry registry,
        AssetMemoryBudget.Reservation reservation)
    {
        this.revision = revision;
        this.executableSha256 = executableSha256;
        this.datasetSha256 = datasetSha256;
        this.definitionCount = definitionCount;
        this.registry = registry;
        this.reservation = reservation;
    }

    public SourceRevision Revision { get { lock (gate) { ThrowIfDisposed(); return revision; } } }
    public string ExecutableSha256 { get { lock (gate) { ThrowIfDisposed(); return executableSha256; } } }
    public string DatasetSha256 { get { lock (gate) { ThrowIfDisposed(); return datasetSha256; } } }
    public int DefinitionCount { get { lock (gate) { ThrowIfDisposed(); return definitionCount; } } }

    /// <summary>Gets the registry while this lease remains active.</summary>
    public TypeRegistry Registry { get { lock (gate) { ThrowIfDisposed(); return registry!; } } }

    public void Dispose()
    {
        lock (gate)
        {
            registry = null;
            Interlocked.Exchange(ref reservation, null)?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(registry is null, this);
}
