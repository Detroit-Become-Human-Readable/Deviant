using System.Collections;
using System.Reflection;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureMetadataContextTests
{
    [Fact]
    public async Task PartialOwnerPublicationPreservesRestoredMetadataDimensionsAndCatalogs()
    {
        using var fixture = Fixture.Create();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entries = Field<Dictionary<ResourceIdentity, TextureDiscoveryEntry>>(discovery, "entries");
        var original = Assert.Single(entries.Values);
        Assert.Equal(2, original.MetadataIdentities.Count);
        Assert.Equal(4, original.Width); Assert.Equal(2, original.Height);
        var bindings = Field<IDictionary>(discovery, "metadataBindings");
        var list = (IList)bindings[original.Identity]!;
        var oneBinding = list[0]!;
        var bindingArray = Array.CreateInstance(oneBinding.GetType(), 1); bindingArray.SetValue(oneBinding, 0);
        var featureType = typeof(TextureDiscoverySession).GetNestedType("ContainerFeatures", BindingFlags.NonPublic)!;
        var ownerType = typeof(TextureDiscoverySession).GetNestedType("Owner", BindingFlags.NonPublic)!;
        var evidence = (TextureEvidence)oneBinding.GetType().GetProperty("Evidence")!.GetValue(oneBinding)!;
        var features = Activator.CreateInstance(featureType, evidence.SourceSha256, bindingArray,
            Array.CreateInstance(ownerType, 0), Array.Empty<string>())!;
        bindings.Clear(); // A warm restored projection has no session-local binding accumulator.
        Method("ApplyContainer").Invoke(discovery, [features]);
        var updated = entries[original.Identity];
        Assert.Equal(original.MetadataIdentities.OrderBy(value => value.Key), updated.MetadataIdentities.OrderBy(value => value.Key));
        Assert.Equal(original.Width, updated.Width); Assert.Equal(original.Height, updated.Height); Assert.Equal(original.Format, updated.Format);
        Assert.Equal(original.CatalogKeys.Order(), updated.CatalogKeys.Order());
        Assert.Equal(2, updated.MetadataContexts.Count);
        Assert.All(updated.MetadataContexts, context => Assert.NotEmpty(context.CatalogIdentities));
    }

    [Fact]
    public async Task ValidStoredFileTextEvidenceRepairsAnOmittedMetadataSelector()
    {
        using var fixture = Fixture.Create();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entry = Assert.Single(Field<Dictionary<ResourceIdentity, TextureDiscoveryEntry>>(discovery, "entries").Values);
        var omitted = entry.MetadataIdentities[0];
        var incomplete = entry with
        {
            MetadataIdentities = entry.MetadataIdentities.Where(value => value != omitted).ToArray(),
            MetadataContexts = entry.MetadataContexts.Where(context => context.MetadataIdentity != omitted).ToArray()
        };
        var restored = (TextureDiscoveryEntry)Method("RestoreMetadataContexts").Invoke(discovery, [incomplete])!;
        Assert.Contains(omitted, restored.MetadataIdentities);
        Assert.Contains(restored.MetadataContexts, context => context.MetadataIdentity == omitted && context.CatalogIdentities.Count > 0);
        var stale = incomplete with
        {
            NameCandidates = incomplete.NameCandidates.Select(candidate => candidate with { Evidence = [] }).ToArray(),
            Owners = [], Related = []
        };
        var notRecovered = (TextureDiscoveryEntry)Method("RestoreMetadataContexts").Invoke(discovery, [stale])!;
        Assert.DoesNotContain(omitted, notRecovered.MetadataIdentities);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("catalog")]
    [InlineData("context")]
    public void ThinSelectorsMustMatchTheValidatedNameProof(string damaged)
    {
        var identity = new ResourceIdentity("generated", 2);
        var metadata = new ResourceIdentity("generated", 1, "filetext/40");
        var catalog = new ResourceIdentity("generated", 0);
        var entry = new TextureDiscoveryEntry(identity, 7, 2229, "Panel texture", TextureNameKind.Fallback,
            null, null, null, 4, 2, "BGRA8", null, [], [], [], [], [], [], "Panel texture", "Panel texture")
        {
            MetadataIdentities = [metadata], CatalogKeys = [catalog.Key],
            MetadataContexts = [new(metadata, [catalog], 4, 2, "BGRA8")]
        };
        var proof = JsonSerializer.SerializeToUtf8Bytes(new { Thin = entry, Sources = Array.Empty<TextureEvidence>() });
        var valid = typeof(FeatureStore).GetMethod("ValidTextureNameProof", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Validate(TextureDiscoveryEntry thin) => (bool)valid.Invoke(null,
            [proof, JsonSerializer.Serialize(thin), identity.Key, entry.Label, (int)entry.NameKind, entry.SearchText, null])!;
        Assert.True(Validate(entry));
        var changed = damaged switch
        {
            "metadata" => entry with { MetadataIdentities = [] },
            "catalog" => entry with { CatalogKeys = [] },
            _ => entry with { MetadataContexts = [new(metadata, [catalog], 8, 2, "BGRA8")] }
        };
        Assert.False(Validate(changed));
    }

    private static MethodInfo Method(string name) => typeof(TextureDiscoverySession).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private sealed class Fixture : IDisposable
    {
        private readonly object value;
        private Fixture(object value) => this.value = value;
        public string Index => (string)value.GetType().GetProperty("Index")!.GetValue(value)!;
        public string Library => (string)value.GetType().GetProperty("Library")!.GetValue(value)!;
        public static Fixture Create()
        {
            var type = typeof(TextureDiscoverySessionTests).GetNestedType("DiscoveryFixture", BindingFlags.NonPublic)!;
            var constructor = type.GetConstructors().Single();
            var parameters = constructor.GetParameters();
            var arguments = parameters.Select(parameter => parameter.DefaultValue).ToArray();
            arguments[Array.FindIndex(parameters, parameter => parameter.Name == "meshNames")] = new[] { "Panel surface" };
            arguments[Array.FindIndex(parameters, parameter => parameter.Name == "duplicateFileText")] = true;
            return new(constructor.Invoke(arguments));
        }
        public void Dispose() => ((IDisposable)value).Dispose();
    }
}
