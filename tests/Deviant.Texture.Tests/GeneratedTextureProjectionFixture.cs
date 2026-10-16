using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Texture.Tests;

internal sealed class GeneratedTextureProjectionFixture : IDisposable
{
    private readonly object value;

    private GeneratedTextureProjectionFixture(object value) => this.value = value;

    private T Get<T>(string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    public ArchiveSnapshot Snapshot => Get<ArchiveSnapshot>("Snapshot");
    public string Cache => Get<string>("Cache");
    public SourceRevision Revision => Get<SourceRevision>("Revision");
    public ResourceIdentity Owner => Get<ResourceIdentity>("Owner");
    public TextureEvidence Evidence => Get<TextureEvidence>("Evidence");
    public TextureProjectionOwnerCheckpoint Checkpoint => Get<TextureProjectionOwnerCheckpoint>("Checkpoint");

    public TextureDiscoveryEntry Entry(string label, long ordinal = 1) =>
        (TextureDiscoveryEntry)value.GetType().GetMethod("Entry")!.Invoke(value, [label, ordinal])!;

    public TextureDiscoveryEntry Named(TextureDiscoveryEntry entry, int width, int height, string format,
        string role, string context) => entry with
    {
        NameKind = TextureNameKind.VerifiedOwnerRole,
        Width = width,
        Height = height,
        Format = format,
        ContextKey = context,
        ContextLabel = context,
        Owners = [new(Owner, "Generated panel", TextureRelationshipKind.Object, true, true, [Evidence], role)],
        NameCandidates = [new(entry.Label, TextureNameKind.VerifiedOwnerRole, [Evidence], Owner, role)]
    };

    public static async Task<GeneratedTextureProjectionFixture> CreateAsync()
    {
        var type = typeof(TextureProjectionStoreTests).GetNestedType("Fixture", BindingFlags.NonPublic)!;
        var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [false])!;
        await task.ConfigureAwait(false);
        return new(task.GetType().GetProperty("Result")!.GetValue(task)!);
    }

    public void Dispose() => ((IDisposable)value).Dispose();
}
