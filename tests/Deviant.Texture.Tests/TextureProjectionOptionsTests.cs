using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionOptionsTests
{
    [Fact]
    public async Task PublishedProjectionsHaveBothCoveringIndexesBeforeFilterReads()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); store.InitializeTextureProjections("options", [baseline]);
        long captured = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);
        Assert.Equal(new[] { "BC1" }, store.TextureProjectionFilters("options", captured).Formats);
        var firstIndexes = Indexes(store);
        Assert.Contains("texture_current_options", firstIndexes);
        Assert.Contains("texture_history_options", firstIndexes);
        Assert.DoesNotContain("texture_options_owner_roles", firstIndexes);
        long current = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 64, 32, "BC3", "Normal", "Hall")]);
        Assert.Equal(new[] { "BC3" }, store.TextureProjectionFilters("options", current).Formats);
        Assert.Contains("texture_history_options", Indexes(store));
        Assert.Equal(new[] { "BC1" }, store.TextureProjectionFilters("options", captured).Formats);
        Assert.Contains("texture_history_options", Indexes(store));
        Assert.DoesNotContain("texture_options_owner_roles", Indexes(store));
        Assert.Equal(current, store.CaptureTextureProjectionGeneration("options"));
    }

    [Fact]
    public async Task CurrentAndCapturedOptionsExcludeRetiredAndUnverifiedRoles()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); store.InitializeTextureProjections("options", [baseline]);
        long oldGeneration = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);
        long current = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 64, 32, "BC3", "Normal", "Hall")]);
        var captured = store.TextureProjectionFilters("options", oldGeneration);
        Assert.Equal(new[] { new TextureDimensionOption(32, 16) }, captured.Dimensions);
        Assert.Equal(new[] { "BC1" }, captured.Formats); Assert.Equal(new[] { "Diffuse" }, captured.VerifiedRoles);
        Assert.Equal(new[] { new TextureContextOption("Room", "Room") }, captured.Contexts);
        var latest = store.TextureProjectionFilters("options", current);
        Assert.Equal(new[] { new TextureDimensionOption(64, 32) }, latest.Dimensions);
        Assert.Equal(new[] { "BC3" }, latest.Formats); Assert.Equal(new[] { "Normal" }, latest.VerifiedRoles);
        Assert.Equal(new[] { new TextureContextOption("Hall", "Hall") }, latest.Contexts);
        Assert.DoesNotContain("Unverified", latest.VerifiedRoles); Assert.DoesNotContain("", latest.VerifiedRoles);
    }

    [Fact]
    public async Task AnotherWriterImmediatelyChangesLatestOptionsWithoutChangingCapturedGeneration()
    {
        using var fixture = await Fixture.CreateAsync(); using var first = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); first.InitializeTextureProjections("options", [baseline]);
        long captured = first.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);
        Assert.Equal(new[] { "BC1" }, first.TextureProjectionFilters("options", captured).Formats);
        using var second = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long updated = second.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 128, 64, "BC5", "Normal", "Hall")]);
        Assert.Equal(new[] { "BC5" }, first.TextureProjectionFilters("options", updated).Formats);
        Assert.Equal(new[] { "Normal" }, first.TextureProjectionFilters("options", updated).VerifiedRoles);
        Assert.Equal(new[] { "BC1" }, first.TextureProjectionFilters("options", captured).Formats);
        Assert.Equal(new[] { "Diffuse" }, first.TextureProjectionFilters("options", captured).VerifiedRoles);
    }

    [Theory]
    [InlineData(true, "dimensions")]
    [InlineData(true, "formats")]
    [InlineData(true, "contexts")]
    [InlineData(true, "roles")]
    [InlineData(false, "dimensions")]
    [InlineData(false, "formats")]
    [InlineData(false, "contexts")]
    [InlineData(false, "roles")]
    public async Task OptionsReadCoveringScalarsAndDriveRoleLookupsFromTheRequestedProjection(bool current, string facet)
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); store.InitializeTextureProjections("options", [baseline]);
        long generation = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);
        for (int version = 0; version < 24; version++)
            store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
                [fixture.Named(baseline, 64, 32, "BC3", "Retired " + version, "Hall")]);
        if (current) generation = store.CaptureTextureProjectionGeneration("options");
        store.TextureProjectionFilters("options", generation); // Materialize the requested scalar generation before inspecting its plan.
        var method = typeof(FeatureStore).GetMethod("TextureOptionsCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var command = (SqliteCommand)method.Invoke(store, ["options", generation, current, facet, null, null])!;
        Assert.DoesNotContain("p.*", command.CommandText); Assert.DoesNotContain("p.thin", command.CommandText);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var plan = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) plan.Add(reader.GetString(3));
        string expectedIndex = current ? "texture_current_options" : "texture_history_options";
        if (current && facet == "roles")
        {
            Assert.Contains(plan, line => line.Contains("texture_current_role_values", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("texture_projection_links", StringComparison.Ordinal));
        }
        else Assert.Contains(plan, line => line.Contains("COVERING INDEX " + expectedIndex, StringComparison.Ordinal));
        if (!current && facet == "roles")
        {
            int projection = plan.FindIndex(line => line.Contains(expectedIndex, StringComparison.Ordinal));
            int role = plan.FindIndex(line => line.Contains("sqlite_autoindex_texture_projection_links_1", StringComparison.Ordinal));
            Assert.True(projection >= 0 && role > projection, string.Join("\n", plan));
            Assert.Contains(plan, line => line.Contains("sqlite_autoindex_texture_projection_links_1", StringComparison.Ordinal) &&
                line.Contains("identity=?", StringComparison.Ordinal) && line.Contains("generation=?", StringComparison.Ordinal) &&
                line.Contains("relation=?", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CancellationLeavesTheRequestedGenerationAndOptionsUnchanged()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); store.InitializeTextureProjections("options", [baseline]);
        long generation = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);
        var before = store.TextureProjectionFilters("options", generation);
        Assert.ThrowsAny<OperationCanceledException>(() => store.TextureProjectionFilters("options", generation, new(true)));
        var after = store.TextureProjectionFilters("options", generation);
        Assert.Equal(generation, store.CaptureTextureProjectionGeneration("options"));
        Assert.Equal(before.Dimensions, after.Dimensions); Assert.Equal(before.Formats, after.Formats);
        Assert.Equal(before.VerifiedRoles, after.VerifiedRoles); Assert.Equal(before.Contexts, after.Contexts);
    }

    [Fact]
    public async Task FourSqlStagesEmitBeginEndAndElapsedMeasurements()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture"); long generation = store.InitializeTextureProjections("options", [baseline]);
        var observer = new Observer(); using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer,
            name => name is AssetLoadDiagnostics.CounterName or AssetLoadDiagnostics.MeasurementName);
        store.TextureProjectionFilters("options", generation);
        foreach (string suffix in new[] { "Dimensions", "Formats", "Roles", "Contexts" })
        {
            string stage = "TextureBrowser.Store.Options" + suffix + "Sql";
            Assert.Contains(observer.Counters, item => item.Stage == stage && item.Values["Active"] == 1);
            Assert.Contains(observer.Counters, item => item.Stage == stage && item.Values["Active"] == 0);
            Assert.Contains(observer.Measurements, item => item.Stage == stage && item.ElapsedMilliseconds >= 0);
        }
    }

    private sealed class Observer : IObserver<KeyValuePair<string, object?>>
    {
        public System.Collections.Concurrent.ConcurrentBag<AssetLoadCounterMeasurement> Counters { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<AssetLoadMeasurement> Measurements { get; } = [];
        public void OnNext(KeyValuePair<string, object?> item)
        { if (item.Value is AssetLoadCounterMeasurement counter) Counters.Add(counter); else if (item.Value is AssetLoadMeasurement measurement) Measurements.Add(measurement); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
    private static string[] Indexes(FeatureStore store)
    {
        var connection = (SqliteConnection)typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN('texture_current_options','texture_history_options','texture_options_owner_roles') ORDER BY name";
        var names = new List<string>(); using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly object value;
        private Fixture(object value) => this.value = value;
        private T Get<T>(string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        public ArchiveSnapshot Snapshot => Get<ArchiveSnapshot>("Snapshot");
        public string Cache => Get<string>("Cache");
        public ResourceIdentity Owner => Get<ResourceIdentity>("Owner");
        public TextureEvidence Evidence => Get<TextureEvidence>("Evidence");
        public TextureProjectionOwnerCheckpoint Checkpoint => Get<TextureProjectionOwnerCheckpoint>("Checkpoint");
        public TextureDiscoveryEntry Entry(string label) => (TextureDiscoveryEntry)value.GetType().GetMethod("Entry")!.Invoke(value, [label, 1L])!;
        public TextureDiscoveryEntry Named(TextureDiscoveryEntry entry, int width, int height, string format, string role, string context) => entry with
        {
            NameKind = TextureNameKind.VerifiedOwnerRole, Width = width, Height = height, Format = format,
            ContextKey = context, ContextLabel = context,
            Owners = [new(Owner, "Generated panel", TextureRelationshipKind.Object, true, true, [Evidence], role),
                new(Owner, "Generated candidate", TextureRelationshipKind.Object, true, false, [Evidence], "Unverified"),
                new(Owner, "Generated unnamed role", TextureRelationshipKind.Object, true, true, [Evidence], "")],
            NameCandidates = [new(entry.Label, TextureNameKind.VerifiedOwnerRole, [Evidence], Owner, role)]
        };
        public static async Task<Fixture> CreateAsync()
        {
            var type = typeof(TextureProjectionStoreTests).GetNestedType("Fixture", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [false])!; await task;
            return new(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }
        public void Dispose() => ((IDisposable)value).Dispose();
    }
}
