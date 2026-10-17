using Deviant.Assets;
using Deviant.Archives;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Deviant.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class SnapshotReaderTests
{
    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public async Task ReadsAudioFromExistingArchiveSnapshotAndRetainsStableIdentity(uint version)
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(version);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var progress = new InlineProgress<AudioReadProgress>();
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot, progress);
        var catalog = result.Catalog;

        Assert.Equal(snapshot.IndexContentId, catalog.IndexContentId);
        Assert.Equal(snapshot.PhysicalSourceStamp, catalog.PhysicalSourceStamp);
        Assert.Equal(snapshot.Records.Count, catalog.IndexedResourceCount);
        Assert.Equal(snapshot.Records.Count(row => row.Type is 29 or 1016 or 1022 or 1023 or 1031 or 1033 or 4091), catalog.Resources.Count);
        Assert.All(catalog.Resources, resource => Assert.Equal(
            new ResourceIdentity(snapshot.IndexContentId, Assert.IsType<long>(resource.IndexOrdinal)), resource.Identity));
        var bank = Assert.Single(catalog.Banks);
        Assert.Equal(version, bank.Version);
        Assert.Equal(snapshot.Records.Single(row => row.Type == 1022).Ordinal, bank.Identity!.Ordinal);
        Assert.Equal(snapshot.Records.Single(row => row.Type == 29).Ordinal, bank.ContainerIdentity!.Ordinal);
        Assert.Equal("bank", bank.ContainerIdentity.MemberPath);
        Assert.True(bank.IndexedObjectCount > 0);
        Assert.Empty(bank.Objects);
        Assert.NotEmpty(catalog.SourceDeclarations);
        Assert.Contains(progress.Items, item => item.Phase == "Reading banks" && item.Snapshot is not null);
        Assert.Contains(progress.Items, item => item.Phase == "Reading dialogue media" && item.Snapshot?.Media.Count > 0);
        Assert.All(progress.Items.Where(item => item.Snapshot is not null), item =>
            Assert.All(item.Snapshot!.Banks, captured => Assert.Empty(captured.Objects)));
        var dialogue = Assert.Single(catalog.Media, media => media.Language == "ENG");
        Assert.Equal(snapshot.Records.Single(row => row.Type == 1033).Ordinal, dialogue.Identity!.Ordinal);
        Assert.Equal("wem", dialogue.Identity.MemberPath);
        Assert.Empty(dialogue.Subtitle); // Stored localization stays unassociated without validated key linkage.
        var bankMedia = Assert.Single(catalog.Media, media => media.BankKey == bank.Key);
        Assert.Equal(bankMedia.DidxExtent, bankMedia.OriginalSlice);
        Assert.True(bankMedia.OriginalSlice!.Length > bankMedia.Slice.Length);
        Assert.Equal(bank.ContainerIdentity.Ordinal, bankMedia.Identity!.Ordinal);
        Assert.StartsWith("media/", bankMedia.Identity.MemberPath);

        var graph = await new DetroitAudioSnapshotReader().ReadBankGraphAsync(snapshot, bank.Identity);
        Assert.Equal(version, graph.Version);
        Assert.NotEmpty(graph.Objects);
        Assert.All(graph.Objects.Values, item => Assert.Equal(bank.Identity.IndexContentId, item.Identity!.IndexContentId));
    }

    [Fact]
    public async Task DialogueProgressReportsOnlyNewOrChangedMediaIdentities()
    {
        const int mediaCount = 8193;
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(mediaCount);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var progress = new InlineProgress<AudioReadProgress>();

        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot, progress);

        var changedProperty = typeof(AudioReadProgress).GetProperty("ChangedMediaIdentities");
        Assert.NotNull(changedProperty);
        var reports = progress.Items.Where(item => item.Snapshot is not null).ToArray();
        Assert.True(reports.Length >= 3);
        var deltas = reports.Select(item => Assert.IsAssignableFrom<IReadOnlyList<ResourceIdentity>>(
            changedProperty!.GetValue(item))).ToArray();
        var changedIdentities = deltas.SelectMany(delta => delta).ToArray();
        Assert.Equal(mediaCount, result.Catalog.Media.Count);
        Assert.Equal(mediaCount, changedIdentities.Distinct().Count());
        Assert.InRange(changedIdentities.Length, mediaCount, mediaCount * 2);
        foreach (var report in reports)
        {
            var visible = report.Snapshot!.Media.Select(media => media.Identity).Where(identity => identity is not null)
                .ToHashSet();
            Assert.All(Assert.IsAssignableFrom<IReadOnlyList<ResourceIdentity>>(
                changedProperty!.GetValue(report)), identity => Assert.Contains(identity, visible));
        }
    }

    [Fact]
    public async Task AmbiguousBankContainerReferenceDoesNotSelectFirstDuplicate()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(duplicateContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "AmbiguousBankContainer");
        var bank = Assert.Single(result.Catalog.Banks);
        Assert.Equal(snapshot.Records.Single(row => row.Type == 1022).Ordinal, bank.Identity!.Ordinal);
    }

    [Fact]
    public async Task AmbiguousDialogueReferenceDoesNotLinkDuplicateAudioRows()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(duplicateDialogueAudio: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DialogueReferenceAmbiguous");
        Assert.Equal(2, result.Catalog.Media.Count(media => media.Language == "ENG"));
        Assert.Empty(result.Catalog.Media.Where(media => media.Language == "ENG").SelectMany(media => media.EventKeys));
    }

    [Fact]
    public async Task DuplicateHircObjectsRemainAmbiguousAndDoNotSelectTheFirstObject()
    {
        using var fixture = FormatsFixtureBuilder.CreateDuplicateHircObjectCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bankDescriptor = Assert.Single(result.Catalog.Banks);
        var graph = await new DetroitAudioSnapshotReader().ReadBankGraphAsync(snapshot, bankDescriptor.Identity!);
        var catalog = new AudioCatalog { Banks = [graph] };

        Assert.Contains(0x60000000u, bankDescriptor.AmbiguousObjectIds);
        Assert.Contains(0x60000000u, graph.AmbiguousObjectIds);
        Assert.False(graph.Objects.ContainsKey(0x60000000u));
        Assert.Equal(2, result.Catalog.SourceDeclarations.Count);
        var resolution = new CatalogGraph(catalog).Resolve(graph, 0x60000000u);
        Assert.True(resolution.Ambiguous);
        Assert.Null(resolution.Unique);
    }

    [Fact]
    public async Task ChangedArchiveVolumeInvalidatesTheSnapshotBeforeAudioParsing()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var packagePath = Path.Combine(fixture.DirectoryPath, "BigFile_PC.dat");
        var bytes = await File.ReadAllBytesAsync(packagePath);
        bytes[0] ^= 0x7F;
        await File.WriteAllBytesAsync(packagePath, bytes);
        // Fast same-length writes can retain the original filesystem timestamps.
        var expectedStamp = snapshot.Volumes.Single(volume => volume.FilePath == packagePath).Stamp;
        File.SetLastWriteTimeUtc(packagePath, new DateTime(expectedStamp.LastWriteUtcTicks, DateTimeKind.Utc).AddMinutes(1));
        Assert.False(SourceIdentity.SameStamp(expectedStamp, new FileRevisionProvider().Capture(packagePath)));

        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot));
        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public void ValidatedUnverifiedMediaMayConvertWithoutBeingPromoted()
    {
        var media = new MediaEntry
        {
            Slice = new DataSlice("fixture-volume.dat", 0, 16),
            State = MediaState.CompleteEmbedded,
            Completeness = MediaCompleteness.Unverified,
            Availability = SourceAvailability.Available,
            ContainerValidity = ContainerValidity.Valid,
            IsRiff = true
        };

        Assert.True(MediaPolicy.CanConvert(media));
        Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
    }

    [Fact]
    public void FragmentCanPreviewOnlyThroughExplicitPartialPreviewPolicy()
    {
        var fragment = new MediaEntry
        {
            Slice = new DataSlice("fixture-volume.dat", 0, 16),
            State = MediaState.PrefetchOnly,
            Completeness = MediaCompleteness.Fragment,
            Availability = SourceAvailability.Available,
            ContainerValidity = ContainerValidity.Truncated,
            IsRiff = true
        };

        Assert.True(MediaPolicy.CanPreview(fragment));
        Assert.False(MediaPolicy.CanConvert(fragment));
        Assert.Throws<InvalidOperationException>(() => MediaPolicy.RequireConversion(fragment));
    }

    [Fact]
    public async Task EmbeddedMediaKeepsCompleteFragmentMissingAndAmbiguousStatesSeparate()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;

        var completeExternal = Assert.Single(catalog.Media, media => media.BankName == "source_unique");
        Assert.Equal(MediaState.CompleteExternal, completeExternal.State);
        Assert.Equal(MediaCompleteness.Complete, completeExternal.Completeness);
        Assert.True(MediaPolicy.CanConvert(completeExternal));
        Assert.NotNull(completeExternal.Identity);

        var fragment = Assert.Single(catalog.Media, media => media.BankName == "source_prefetch" && media.Completeness == MediaCompleteness.Fragment);
        Assert.Equal(MediaCompleteness.Fragment, fragment.Completeness);
        Assert.Equal(12, fragment.Slice.Length);
        Assert.Equal(fragment.Slice, fragment.DidxExtent);
        Assert.False(MediaPolicy.CanConvert(fragment));

        Assert.Equal(MediaState.MissingExternal, Assert.Single(catalog.Media, media => media.BankName == "source_missing").State);
        var ambiguous = Assert.Single(catalog.Media, media => media.BankName == "source_ambiguous");
        Assert.Equal(MediaState.Ambiguous, ambiguous.State);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Equal(MediaState.Unsupported, Assert.Single(catalog.Media, media => media.BankName == "source_plugin").State);
        Assert.True(completeExternal.MetadataReadCoverageComplete);
        Assert.Contains(completeExternal.MetadataReadDigests, digest => digest.Role == "HIRC");
        Assert.Contains(completeExternal.MetadataReadDigests, digest => digest.Role == "WEM");
    }

    [Fact]
    public async Task BankReadBaselineIncludesStringTableAndHircInputs()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint bankId = 0xAABBCCDD;
        var bytes = FormatsFixtureBuilder.AppendStringTable(
            FormatsFixtureBuilder.MakeBankForSources(bankId, [], []), 0x01020304, "Footstep");
        fixture.AddBankFixture(1, "named_bank", bytes);
        fixture.WriteIndex();

        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var bank = Assert.Single(catalog.Banks);
        var stid = Assert.Single(bank.Chunks, chunk => chunk.Tag == "STID");

        Assert.True(bank.MetadataReadCoverageComplete);
        Assert.Contains(catalog.Names, name => name.Text == "Footstep");
        Assert.Contains(bank.MetadataReadDigests, digest => digest.Role == "HIRC");
        Assert.Contains(bank.MetadataReadDigests, digest => digest.Slice == stid.Slice);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
}
