using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Export.Audio;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioExportServiceTests
{
    [Fact]
    public async Task OriginalFragmentNeedsExplicitOptInAndThenCopiesTheDeclaredExtent()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var playable = result.Catalog.Media.Where(MediaPolicy.HasBytes).ToArray();
        Assert.NotEmpty(playable);
        var media = playable[0];
        media.Completeness = MediaCompleteness.Fragment;
        media.DisplayLabel = "Synthetic clip";
        var identity = Assert.IsType<Deviant.Assets.ResourceIdentity>(media.Identity);
        var request = new AudioExportRequest(snapshot, [new AudioExportSource(identity, media)], ClassificationComplete: true);
        var output = UniqueSibling(fixture.DirectoryPath, "export");
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        try
        {
            var skipped = await service.ExportAsync(request, new AudioExportOptions(output, AudioExportFormat.Original));
            Assert.Equal(AudioExportStatus.Skipped, Assert.Single(skipped.Manifest.Entries).Status);

            var completed = await service.ExportAsync(request, new AudioExportOptions(output, AudioExportFormat.Original, AllowPartialOriginal: true));
            var entry = Assert.Single(completed.Manifest.Entries);
            Assert.Equal(AudioExportStatus.Completed, entry.Status);
            Assert.Equal(media.OriginalSlice?.Offset ?? media.DidxExtent?.Offset ?? media.Slice.Offset, entry.SourceOffset);
            Assert.Equal(media.OriginalSlice?.Length ?? media.DidxExtent?.Length ?? media.Slice.Length, entry.SourceLength);
            Assert.NotNull(entry.SourceSha256);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, entry.RelativeOutput));
            Assert.Equal(entry.SourceLength, bytes.Length);
            Assert.Equal(entry.SourceSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public async Task OriginalExportSeparatesLogicalDeclarationFromConcretePayloadOwner()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var sourceMedia = Assert.Single(result.Catalog.Media, media => media.Language == "ENG");
        var bank = Assert.Single(result.Catalog.Banks);
        var logicalIdentity = Assert.IsType<Deviant.Assets.ResourceIdentity>(bank.Identity) with
        { MemberPath = $"source/test/media/{sourceMedia.Id:X8}" };
        var payloadIdentity = Assert.IsType<Deviant.Assets.ResourceIdentity>(sourceMedia.PayloadIdentity);
        Assert.NotEqual(logicalIdentity.Ordinal, payloadIdentity.Ordinal);
        var media = new MediaEntry
        {
            Identity = logicalIdentity,
            PayloadIdentity = payloadIdentity,
            Key = "synthetic:resolved-audio-source",
            Id = sourceMedia.Id,
            Name = sourceMedia.Name,
            DisplayLabel = "Synthetic resolved source",
            Language = sourceMedia.Language,
            Category = sourceMedia.Category,
            Codec = sourceMedia.Codec,
            SampleRate = sourceMedia.SampleRate,
            Channels = sourceMedia.Channels,
            Duration = sourceMedia.Duration,
            Slice = sourceMedia.Slice,
            OriginalSlice = sourceMedia.OriginalSlice,
            State = sourceMedia.State,
            Completeness = sourceMedia.Completeness,
            ContainerValidity = sourceMedia.ContainerValidity,
            Availability = sourceMedia.Availability,
            IsRiff = sourceMedia.IsRiff
        };
        var request = new AudioExportRequest(snapshot, [new AudioExportSource(logicalIdentity, media)], ClassificationComplete: true);
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        foreach (var format in new[] { AudioExportFormat.Original, AudioExportFormat.Wav, AudioExportFormat.Ogg })
        {
            var output = UniqueSibling(fixture.DirectoryPath, "payload-owner-" + format.ToString().ToLowerInvariant());
            try
            {
                var report = await service.ExportAsync(request, new AudioExportOptions(output, format));
                var entry = Assert.Single(report.Manifest.Entries);
                Assert.Equal(2, report.Manifest.SchemaVersion);
                Assert.Equal(logicalIdentity.Key, entry.Identity);
                Assert.Equal(logicalIdentity.Ordinal, entry.Ordinal);
                Assert.Equal(payloadIdentity.Key, entry.PayloadIdentity);
                Assert.Equal(payloadIdentity.Ordinal, entry.PayloadOrdinal);
                Assert.Equal(payloadIdentity.MemberPath, entry.PayloadMemberPath);
                var expected = format == AudioExportFormat.Original ? sourceMedia.OriginalSlice! : sourceMedia.Slice;
                Assert.Equal(expected.Offset, entry.SourceOffset);
                Assert.Equal(expected.Length, entry.SourceLength);
                if (format == AudioExportFormat.Original)
                {
                    Assert.Equal(AudioExportStatus.Completed, entry.Status);
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(output, entry.RelativeOutput));
                    Assert.Equal(entry.SourceLength, bytes.Length);
                    Assert.Equal(entry.SourceSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                }
            }
            finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
        }
    }

    [Fact]
    public async Task ExternalPayloadOnAnotherVolumeExportsFromThatPackageAndRejectsForgedBindings()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(dialoguePackage: 2);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var sourceMedia = Assert.Single(result.Catalog.Media, media => media.Language == "ENG");
        var bank = Assert.Single(result.Catalog.Banks);
        var logicalIdentity = Assert.IsType<Deviant.Assets.ResourceIdentity>(bank.Identity) with
        { MemberPath = $"source/test/media/{sourceMedia.Id:X8}" };
        var payloadIdentity = Assert.IsType<Deviant.Assets.ResourceIdentity>(sourceMedia.PayloadIdentity);
        Assert.Equal(2u, snapshot.Records[(int)payloadIdentity.Ordinal].Package);
        Assert.Equal(0u, snapshot.Records[(int)logicalIdentity.Ordinal].Package);
        var media = new MediaEntry
        {
            Identity = logicalIdentity,
            PayloadIdentity = payloadIdentity,
            Key = "synthetic:cross-volume-source",
            Id = sourceMedia.Id,
            Name = sourceMedia.Name,
            DisplayLabel = "Synthetic cross-volume source",
            Language = sourceMedia.Language,
            Category = sourceMedia.Category,
            Codec = sourceMedia.Codec,
            SampleRate = sourceMedia.SampleRate,
            Channels = sourceMedia.Channels,
            Duration = sourceMedia.Duration,
            Slice = sourceMedia.Slice,
            OriginalSlice = sourceMedia.OriginalSlice,
            State = sourceMedia.State,
            Completeness = sourceMedia.Completeness,
            ContainerValidity = sourceMedia.ContainerValidity,
            Availability = sourceMedia.Availability,
            IsRiff = sourceMedia.IsRiff
        };
        var binding = AudioResourceBinding.Resolve(snapshot, media, original: true);
        Assert.Equal("BigFile_PC.d02", binding.Volume.RelativeName);

        var forgedVolume = binding.Volume with { RelativeName = "BigFile_PC.dat" };
        var forgedSnapshot = snapshot with
        {
            Volumes = snapshot.Volumes.Select(volume => volume.Package == 2 ? forgedVolume : volume).ToArray()
        };
        var forgedError = Assert.Throws<Deviant.Core.ArchiveException>(() =>
            AudioResourceBinding.Resolve(forgedSnapshot, media, original: true));
        Assert.Equal("InvalidPayloadSource", forgedError.Code);

        var forgedIdentityMedia = new MediaEntry
        {
            Identity = media.Identity,
            PayloadIdentity = payloadIdentity with { MemberPath = "media/forged" },
            Slice = media.Slice,
            OriginalSlice = media.OriginalSlice,
            Completeness = media.Completeness,
            Availability = media.Availability
        };
        var identityError = Assert.Throws<Deviant.Core.ArchiveException>(() =>
            AudioResourceBinding.Resolve(snapshot, forgedIdentityMedia));
        Assert.Equal("InvalidPayloadIdentity", identityError.Code);

        var outOfBoundsMedia = new MediaEntry
        {
            Identity = media.Identity,
            PayloadIdentity = media.PayloadIdentity,
            Id = media.Id,
            Slice = media.Slice with
            {
                Offset = snapshot.Records[(int)payloadIdentity.Ordinal].Offset + snapshot.Records[(int)payloadIdentity.Ordinal].StoredSize - 1,
                Length = 2
            },
            OriginalSlice = media.OriginalSlice,
            Completeness = media.Completeness,
            Availability = media.Availability
        };
        var rangeError = Assert.Throws<Deviant.Core.ArchiveException>(() =>
            AudioResourceBinding.Resolve(snapshot, outOfBoundsMedia));
        Assert.Equal("InvalidPayloadExtent", rangeError.Code);

        var request = new AudioExportRequest(snapshot, [new AudioExportSource(logicalIdentity, media)], ClassificationComplete: true);
        var output = UniqueSibling(fixture.DirectoryPath, "cross-volume-export");
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        try
        {
            var report = await service.ExportAsync(request, new AudioExportOptions(output, AudioExportFormat.Original));
            var entry = Assert.Single(report.Manifest.Entries);
            Assert.Equal(AudioExportStatus.Completed, entry.Status);
            Assert.Equal("BigFile_PC.d02", entry.SourceVolume);
            Assert.Equal(2L, entry.PayloadOrdinal is not null ? snapshot.Records[(int)entry.PayloadOrdinal.Value].Package : -1L);
            Assert.StartsWith("audio/p02/", entry.RelativeOutput);
            Assert.Equal(binding.Extent.Offset, entry.SourceOffset);
            Assert.Equal(binding.Extent.Length, entry.SourceLength);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, entry.RelativeOutput));
            Assert.Equal(entry.SourceSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public void StagedUnclassifiedMediaCapabilitiesDoNotExposeOriginalOrConversionActions()
    {
        var identity = new Deviant.Assets.ResourceIdentity("content", 0, "media/1/0");
        var media = new MediaEntry
        {
            Identity = identity, Id = 1, Completeness = MediaCompleteness.Unverified,
            Availability = SourceAvailability.Available, State = MediaState.CompleteEmbedded,
            ContainerValidity = ContainerValidity.Valid, IsRiff = true, Slice = new DataSlice("source.dat", 0, 4)
        };
        var descriptor = new Deviant.Assets.AssetDescriptor(identity, new Deviant.Assets.SourceRevision("content", "physical"),
            "audio.media", "Vorbis clip 1", "bank", Deviant.Assets.AssetAvailability.Available,
            Deviant.Assets.AssetCompleteness.Unverified, Deviant.Assets.AssetCapabilities.None, [], []);
        var row = new AudioExplorerRow(descriptor, media, null);

        Assert.False(row.CanExportOriginal);
        Assert.False(row.CanPreview);
        Assert.False(row.CanPreviewPartial);
        Assert.False(row.CanConvert);
        Assert.Empty(row.CompletenessBadge);
    }

    [Fact]
    public async Task ExportRequestRejectsUnclassifiedSnapshotsBeforeCreatingOutputs()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var request = new AudioExportRequest(snapshot, [], ClassificationComplete: false);
        var output = UniqueSibling(fixture.DirectoryPath, "classification-pending");
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));

        var error = await Assert.ThrowsAsync<Deviant.Core.ArchiveException>(() =>
            service.ExportAsync(request, new AudioExportOptions(output, AudioExportFormat.Original)));

        Assert.Equal("ClassificationPending", error.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CancellationAfterOneCommitKeepsItsStatusAndWritesTheFinalManifest()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var media = result.Catalog.Media.Where(MediaPolicy.HasBytes).Take(2).ToArray();
        Assert.Equal(2, media.Length);
        media[0].DisplayLabel = "Synthetic first clip";
        media[1].DisplayLabel = "Synthetic second clip";
        var request = new AudioExportRequest(snapshot,
            media.Select(item => new AudioExportSource(Assert.IsType<Deviant.Assets.ResourceIdentity>(item.Identity), item)).ToArray(), ClassificationComplete: true);
        var output = UniqueSibling(fixture.DirectoryPath, "cancelled-export");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<AudioProgress>(value =>
        {
            if (value.Phase == "Exporting audio" && value.Detail == "Synthetic first clip") cancellation.Cancel();
        });
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        try
        {
            var report = await service.ExportAsync(request,
                new AudioExportOptions(output, AudioExportFormat.Original, ConversionWorkers: 1), progress, cancellation.Token);
            Assert.True(report.Manifest.Cancelled);
            Assert.Equal(2, report.Manifest.Entries.Count);
            Assert.Equal(AudioExportStatus.Completed, report.Manifest.Entries[0].Status);
            Assert.Equal(AudioExportStatus.Cancelled, report.Manifest.Entries[1].Status);
            Assert.True(File.Exists(report.ManifestPath));
            Assert.True(File.Exists(Path.Combine(output, report.Manifest.Entries[0].RelativeOutput)));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public async Task CodecStartFailureIsRecordedForEveryEntryAndStillWritesTheReport()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var media = result.Catalog.Media.Where(MediaPolicy.HasBytes).Take(2).ToArray();
        Assert.Equal(2, media.Length);
        foreach (var item in media)
        {
            item.State = MediaState.CompleteEmbedded;
            item.Completeness = MediaCompleteness.Complete;
            item.Availability = SourceAvailability.Available;
            item.ContainerValidity = ContainerValidity.Valid;
            item.IsRiff = true;
        }
        var request = new AudioExportRequest(snapshot,
            media.Select(item => new AudioExportSource(Assert.IsType<Deviant.Assets.ResourceIdentity>(item.Identity), item)).ToArray(), ClassificationComplete: true);
        var output = UniqueSibling(fixture.DirectoryPath, "codec-failure-export");
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        try
        {
            var report = await service.ExportAsync(request, new AudioExportOptions(output, AudioExportFormat.Wav, ConversionWorkers: 1));
            Assert.All(report.Manifest.Entries, entry => Assert.Equal(AudioExportStatus.Failed, entry.Status));
            Assert.All(report.Manifest.Entries, entry => Assert.Equal("AudioToolException", entry.ErrorCode));
            Assert.True(File.Exists(report.ManifestPath));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public async Task OriginalBankExportUsesContainerExtentAndRetainsDescriptorIdentitySeparately()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks, item => item.ContainerIdentity is not null);
        Assert.NotNull(bank.Identity);
        Assert.NotEqual(bank.Identity!.Key, bank.ContainerIdentity!.Key);
        var output = UniqueSibling(fixture.DirectoryPath, "bank-export");
        var service = new AudioExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        try
        {
            var report = await service.ExportBankAsync(snapshot, bank, output);
            var entry = Assert.Single(report.Manifest.Entries);
            Assert.Equal(AudioExportStatus.Completed, entry.Status);
            Assert.Equal(bank.ContainerIdentity.Key, entry.Identity);
            Assert.Equal(bank.Identity.Key, entry.RelatedDescriptorIdentity);
            Assert.Equal("bank", entry.MemberPath);
            Assert.Equal(bank.Slice.Offset, entry.SourceOffset);
            Assert.Equal(bank.Slice.Length, entry.SourceLength);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, entry.RelativeOutput));
            Assert.Equal(entry.SourceLength, bytes.Length);
            Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    private static string UniqueSibling(string source, string suffix) =>
        Path.Combine(Path.GetDirectoryName(source)!, "deviant-audio-test-" + suffix + "-" + Guid.NewGuid().ToString("N"));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
