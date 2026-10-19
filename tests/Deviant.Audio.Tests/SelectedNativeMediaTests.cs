using System.Buffers.Binary;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class SelectedNativeMediaTests
{
    [Fact]
    public async Task SelectedEmbeddedOccurrenceRetainsLocalDeclarationsAndDistinctDuplicateIdExtents()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint id = 1234;
        const string bankName = "generated_selection_bank";
        var first = FormatsFixtureBuilder.MakeWem([1, 2]);
        var second = FormatsFixtureBuilder.MakeWem([3, 4, 5, 6]);
        fixture.AddBankFixture(1, bankName, FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(bankName),
            [(id, first), (id, second)], [(id, (byte)0, 0u, 0x00040001u)]));
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var owner = snapshot.Records.Single(row => row.Type == 29);
        var descriptor = new ResourceIdentity(snapshot.IndexContentId, snapshot.Records.Single(row => row.Type == 1022).Ordinal);
        var reader = new DetroitAudioSnapshotReader();

        var selected = await reader.ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, owner.Ordinal, $"media/{id:X8}/1"), descriptor);
        var other = await reader.ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, owner.Ordinal, $"media/{id:X8}/0"), descriptor);

        Assert.Equal(SelectedAudioClassificationScope.EmbeddedBank, selected.Scope);
        Assert.NotNull(selected.Bank);
        Assert.Equal(bankName, selected.Media.BankName);
        Assert.Equal(descriptor, selected.Bank.Identity);
        Assert.Equal(MediaCompleteness.Complete, selected.Media.Completeness);
        Assert.True(selected.Media.SourceDeclarationProcessingComplete);
        Assert.NotEmpty(selected.Media.SourceEvidence);
        Assert.NotEqual(other.Media.Identity, selected.Media.Identity);
        Assert.NotEqual(other.Media.Slice.Offset, selected.Media.Slice.Offset);
        Assert.Equal(second.Length, selected.Media.OriginalSlice!.Length);
        Assert.Equal(first.Length, other.Media.OriginalSlice!.Length);
        Assert.True(selected.Media.MetadataReadCoverageComplete);
        Assert.NotEmpty(selected.Media.MetadataReadDigests);
        Assert.Equal(selected.Media.Identity, AudioResourceBinding.Resolve(snapshot, selected.Media).LogicalIdentity);
        await new AudioMetadataVerifier(snapshot).VerifyMediaAsync(selected.Media);
    }

    [Fact]
    public async Task SelectedStandaloneReadsStoredMetadataWithoutUnrelatedVolume()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033, 10, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Line_ENG",
            FormatsFixtureBuilder.MakeWem([1, 2, 3])));
        fixture.AddResource(1033, 11, FormatsFixtureBuilder.MakeDialogueMedia("Other_Line_FRE",
            FormatsFixtureBuilder.MakeWem([4, 5, 6])), package: 1);
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        File.Delete(snapshot.Volumes.Single(volume => volume.Package == 1).FilePath);
        var owner = snapshot.Records.Single(row => row.Id == 10);

        var selected = await new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, owner.Ordinal, "wem"));

        Assert.Equal(SelectedAudioClassificationScope.Standalone, selected.Scope);
        Assert.Null(selected.Bank);
        Assert.Equal("Generated_Line_ENG", selected.Media.Name);
        Assert.Equal("ENG", selected.Media.Language);
        Assert.Equal("Wwise Vorbis", selected.Media.Codec);
        Assert.True(selected.Media.MetadataReadCoverageComplete);
        Assert.All(selected.Media.MetadataReadDigests, digest => Assert.Equal(snapshot.Volumes.Single(v => v.Package == 0).FilePath, digest.Slice.FilePath));
    }

    [Fact]
    public async Task SelectedArchiveMidiReturnsRealSequenceAndSourceProof()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        byte[] midi = [77, 84, 104, 100, 0, 0, 0, 6, 0, 0, 0, 1, 1, 224,
            77, 84, 114, 107, 0, 0, 0, 4, 0, 255, 47, 0];
        var wrapped = new byte[33 + midi.Length];
        "QZIP\0RAW_FILE"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(13), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(17), (uint)midi.Length + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), 4);
        "MIDI"u8.CopyTo(wrapped.AsSpan(25));
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(29), (uint)midi.Length);
        midi.CopyTo(wrapped, 33);
        fixture.AddResource(65, 17, wrapped); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);

        var selected = await new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, 0, "midi"));

        Assert.Equal(SelectedAudioClassificationScope.Midi, selected.Scope);
        Assert.Equal(new MidiMetadata(0, 1, 480), selected.Media.Midi);
        Assert.Equal(midi.Length, selected.Media.Slice.Length);
        Assert.True(selected.Media.MetadataReadCoverageComplete);
        Assert.False(MediaPolicy.CanConvert(selected.Media));
        await new AudioMetadataVerifier(snapshot).VerifyMediaAsync(selected.Media);
    }

    [Theory]
    [InlineData("media/8A7B6C5D/1")]
    [InlineData("media/00000001/0")]
    public async Task SelectedDidxRequiresBothRecordIndexAndId(string memberPath)
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var descriptor = new ResourceIdentity(snapshot.IndexContentId, snapshot.Records.Single(row => row.Type == 1022).Ordinal);
        var owner = snapshot.Records.Single(row => row.Type == 29);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader()
            .ReadSelectedMediaAsync(snapshot, new(snapshot.IndexContentId, owner.Ordinal, memberPath), descriptor));
        Assert.Equal("SelectedMediaMissing", error.Code);
    }

    [Fact]
    public async Task AmbiguousContainerDoesNotChooseMatchingSelectedOwner()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(duplicateContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var descriptor = new ResourceIdentity(snapshot.IndexContentId, snapshot.Records.Single(row => row.Type == 1022).Ordinal);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, snapshot.Records.First(row => row.Type == 29).Ordinal, "media/8A7B6C5D/0"), descriptor));
        Assert.Equal("AmbiguousBankContainer", error.Code);
    }

    [Fact]
    public async Task DescriptorFromOtherBankCannotHydrateSelectedContainer()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var descriptor = snapshot.Records.First(row => row.Type == 1022);
        var otherOwner = snapshot.Records.Where(row => row.Type == 29).Skip(1).First();
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, otherOwner.Ordinal, "media/10203040/0"), new(snapshot.IndexContentId, descriptor.Ordinal)));
        Assert.Equal("BankOwnerMismatch", error.Code);
    }

    [Fact]
    public async Task ExternalDeclarationIsNotHydratedFromConvenientSameIdCandidate()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var descriptor = snapshot.Records.Where(row => row.Type == 1022).Skip(1).First();
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, descriptor.Ordinal, "source/10203040/0"), new(snapshot.IndexContentId, descriptor.Ordinal)));
        Assert.Equal("SelectedMediaUnsupported", error.Code);
    }

    [Fact]
    public async Task ChangedSourceIsRejectedBeforeSelectedPayloadPublication()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var path = snapshot.Volumes.Single().FilePath;
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, 0, "wem")));
        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task CancelledSelectionDoesNotReadOrPublishMedia()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, 0, "wem"), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task BankGroupKeyResolvesOnlyExactDescriptorInCurrentGeneration()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var row = snapshot.Records.Single(item => item.Type == 1022);
        var prefix = snapshot.IndexContentId + ":resource:";
        Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, row.Ordinal),
            DetroitAudioSnapshotReader.ResolveBankDescriptorIdentity(snapshot, prefix + row.Ordinal));
        foreach (var key in new[] { "other:resource:" + row.Ordinal, prefix + "0" + row.Ordinal, prefix + "0", prefix + "-1" })
            Assert.Equal("InvalidBankDescriptor", Assert.Throws<ArchiveException>(() =>
                DetroitAudioSnapshotReader.ResolveBankDescriptorIdentity(snapshot, key)).Code);
    }

    [Fact]
    public async Task EmbeddedSelectionWithoutWorkspaceIndexReportsUnavailable()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var descriptor = snapshot.Records.Single(item => item.Type == 1022);
        var container = snapshot.Records.Single(item => item.Type == 29);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, container.Ordinal, "media/8A7B6C5D/0"), new(snapshot.IndexContentId, descriptor.Ordinal)));
        Assert.Equal("SourceIndexUnavailable", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteOrAmbiguousBankClassificationDoesNotPublishSelectedMedia(bool duplicateChunk)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint mediaId = 41;
        const string name = "generated_scope_bank";
        var bank = FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(name),
            [(mediaId, FormatsFixtureBuilder.MakeWem([1, 2]))], [(mediaId, (byte)0, 0u, 0x00040001u)],
            bankVersion: duplicateChunk ? 120u : 121u);
        if (duplicateChunk) bank = bank.Concat(new byte[] { 68, 73, 68, 88, 0, 0, 0, 0 }).ToArray();
        fixture.AddBankFixture(1, name, bank); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var owner = snapshot.Records.Single(row => row.Type == 29);
        var descriptor = snapshot.Records.Single(row => row.Type == 1022);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(snapshot,
            new(snapshot.IndexContentId, owner.Ordinal, $"media/{mediaId:X8}/0"), new(snapshot.IndexContentId, descriptor.Ordinal)));
        Assert.Equal("SelectedClassificationUnavailable", error.Code);
    }
}
