using Deviant.Assets;

namespace Deviant.Formats.Narrative;

/// <summary>Retains outer bundle framing and optional validation of one selected Lua chunk.</summary>
public sealed record ScriptBundleInspectionResult(
    DecodeResult<ScriptBundleDocument> Bundle,
    ScriptBundleChunk? SelectedChunk,
    DecodeResult<Lua51ChunkDocument>? LuaChunk);

/// <summary>Frames one script bundle, then validates only the caller-selected Lua chunk.</summary>
public sealed class ScriptBundleInspectionReader
{
    private readonly ScriptBundleReader bundleReader = new();
    private readonly Lua51ChunkReader luaReader = new();

    public ScriptBundleInspectionResult Read(ReadOnlyMemory<byte> bundleBytes,
        ResourceIdentity resource, SourceRevision revision, DecodedExtent inputExtent,
        IReadOnlyList<SourceExtent> physicalSources, int selectedChunkIndex,
        ScriptBundleLimits? bundleLimits = null, Lua51ReadOptions? luaOptions = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(inputExtent);
        ArgumentNullException.ThrowIfNull(physicalSources);
        token.ThrowIfCancellationRequested();

        var bundle = bundleReader.Read(bundleBytes, resource, revision, inputExtent,
            physicalSources, bundleLimits, token);
        token.ThrowIfCancellationRequested();
        if (bundle.Value is not { } document)
            return new(bundle, null, null);

        if (selectedChunkIndex < 0 || selectedChunkIndex >= document.Chunks.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedChunkIndex), selectedChunkIndex,
                $"Choose a chunk index between 0 and {document.Chunks.Count - 1}.");

        token.ThrowIfCancellationRequested();
        var selected = document.Chunks[selectedChunkIndex];
        var lua = luaReader.Read(selected.BytecodeBytes.Span, selected.Identity, bundle.Revision,
            selected.BytecodeSpan, document.OriginalPhysicalExtents, luaOptions, token);
        token.ThrowIfCancellationRequested();
        return new(bundle, selected, lua);
    }
}
