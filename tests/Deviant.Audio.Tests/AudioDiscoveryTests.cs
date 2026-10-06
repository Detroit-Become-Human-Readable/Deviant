using System.Security.Cryptography;
using System.Diagnostics;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using AudioNameKind = Deviant.Formats.Audio.Model.NameKind;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioDiscoveryTests
{
    [Fact]
    public async Task GroupsAudioByStoredBankAndLanguageFieldsWithStableTargets()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var discovery = new AudioDiscovery(snapshot, result.Catalog);

        var bank = Assert.Single(discovery.GetBanks());
        Assert.Equal(snapshot.Records.Single(row => row.Type == 1022).Ordinal, bank.DescriptorIdentity!.Ordinal);
        Assert.Equal(snapshot.Records.Single(row => row.Type == 29).Ordinal, bank.ContainerIdentity!.Ordinal);
        Assert.Equal(AudioRelationshipStatus.Resolved, bank.ContainerStatus);
        Assert.Equal(1, bank.MediaCount);

        var language = Assert.Single(discovery.GetLanguages(), group => group.LanguageCode == "ENG");
        var dialogue = Assert.Single(result.Catalog.Media, media => media.Language == "ENG");
        Assert.Contains(language.Media, identity => identity.Key == dialogue.Identity!.Key);
        Assert.Contains(language.Evidence, value => value.Contains("Media language field 'ENG'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LazyBankRelationshipsNavigateFromContainerEventAndHircSourceToMedia()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key);
        var report = await new AudioRelationships(snapshot, result.Catalog).ResolveBankAsync(bank.Identity!);

        var container = Assert.Single(report.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Container);
        Assert.Equal(ReferenceStatus.Unique, container.Resolution.Status);
        Assert.Equal(bank.ContainerIdentity!.Key, container.NavigationTarget!.Key);
        Assert.NotNull(container.Evidence.DecodedExtent);

        var sourceObject = Assert.Single(report.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Media &&
            relationship.Reference.Namespace == IdentifierNamespace.Media && relationship.NavigationTarget?.Key == media.Identity!.Key &&
            relationship.Label.StartsWith("Media 0x", StringComparison.Ordinal) &&
            relationship.Source.MemberPath?.StartsWith("hirc/", StringComparison.Ordinal) == true);
        Assert.True(sourceObject.Evidence.Verified);

        var playback = Assert.Single(report.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Media &&
            relationship.Reference.Namespace == IdentifierNamespace.Media && relationship.NavigationTarget?.Key == media.Identity!.Key &&
            relationship.Source.MemberPath?.StartsWith("hirc/", StringComparison.Ordinal) == true &&
            relationship.Label.StartsWith("Event 0x", StringComparison.Ordinal));
        Assert.Contains("HIRC event", playback.Evidence.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateContainerAndHircIdsRemainAmbiguousInsteadOfChoosingFirst()
    {
        using (var fixture = FormatsFixtureBuilder.CreateAudioCatalog(duplicateContainer: true))
        {
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
            var discovery = new AudioDiscovery(snapshot, result.Catalog);
            var bank = Assert.Single(discovery.GetBanks());
            Assert.Equal(AudioRelationshipStatus.Ambiguous, bank.ContainerStatus);

            var report = await discovery.Relationships.ResolveBankAsync(bank.DescriptorIdentity!);
            var container = Assert.Single(report.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Container);
            Assert.Equal(ReferenceStatus.Ambiguous, container.Resolution.Status);
            Assert.Null(container.NavigationTarget);
        }

        using (var fixture = FormatsFixtureBuilder.CreateDuplicateHircObjectCatalog())
        {
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
            var bank = Assert.Single(result.Catalog.Banks);
            var report = await new AudioRelationships(snapshot, result.Catalog).ResolveBankAsync(bank.Identity!);
            var duplicate = Assert.Single(report.Relationships, relationship => relationship.Reference.Namespace == IdentifierNamespace.Hirc &&
                relationship.Reference.Id == "60000000" && relationship.Resolution.Status == ReferenceStatus.Ambiguous);
            Assert.Null(duplicate.NavigationTarget);
        }
    }

    [Fact]
    public async Task MissingDialogueEventKeyIsReportedWithoutGuessingAnotherNumericId()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var media = Assert.Single(result.Catalog.Media, item => item.Language == "ENG");
        media.EventKeys.Clear();
        media.EventKeys.Add("event:dialogue-resource:missing");

        var relationships = await new AudioRelationships(snapshot, result.Catalog)
            .ResolveForMediaAsync(media.Identity!);
        var missing = Assert.Single(relationships, relationship => relationship.Kind == AssetRelationshipKind.Event);
        Assert.Equal(IdentifierNamespace.Archive, missing.Reference.Namespace);
        Assert.Equal(1031u, missing.Reference.ResourceType);
        Assert.Equal(ReferenceStatus.Missing, missing.Resolution.Status);
        Assert.Null(missing.NavigationTarget);
    }

    [Fact]
    public async Task ConflictingVerifiedBankNamesFallBackToBankIdInsteadOfPickingTheFirst()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var original = Assert.Single(result.Catalog.Names, name => name.Namespace == "WwiseBank" && name.Kind == AudioNameKind.Verified);
        result.Catalog.Names.Add(original with { Text = "Conflicting stored label" });

        var bank = Assert.Single(new AudioDiscovery(snapshot, result.Catalog).GetBanks());

        Assert.Equal($"Bank 0x{bank.BankId:X8}", bank.Label);
        Assert.Contains("no verified bank name", bank.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnnamedMediaAndBanksUseLiteralFallbackLabels()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        bank.Name = "";
        var descriptorRow = snapshot.Records[(int)bank.Identity!.Ordinal];
        var descriptor = Assert.Single(result.Catalog.Resources, item => item.IndexOrdinal == descriptorRow.Ordinal);
        result.Catalog.Names.RemoveAll(name => name.Namespace == "WwiseBank" && name.Source == descriptor.Key);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key);
        media.Language = "";
        media.RawLanguageId = null;

        var discovery = new AudioDiscovery(snapshot, result.Catalog);
        var group = Assert.Single(discovery.GetBanks());
        var language = Assert.Single(discovery.GetLanguages(), item => item.LanguageCode is null);

        Assert.Equal($"Bank 0x{bank.Id:X8}", group.Label);
        Assert.Null(language.LanguageCode);
        Assert.Equal("No language metadata", language.Label);
        Assert.Contains(media.Identity!.Key, language.Media.Select(identity => identity.Key));
    }

    [Fact]
    public async Task StaleCatalogMediaIdentityCannotBecomeANavigationTarget()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key);
        media.Identity = media.Identity! with { IndexContentId = "previous-index-revision" };

        var relationships = new AudioRelationships(snapshot, result.Catalog);
        var report = await relationships.ResolveBankAsync(bank.Identity!);
        var link = Assert.Single(report.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Media &&
            relationship.Label == $"Media 0x{media.Id:X8}");
        Assert.Equal(ReferenceStatus.Unresolved, link.Resolution.Status);
        Assert.Null(link.NavigationTarget);
        Assert.Contains("stale", link.Resolution.Reason, StringComparison.OrdinalIgnoreCase);

        var error = await Assert.ThrowsAsync<ArchiveException>(() => relationships.ResolveForMediaAsync(media.Identity!));
        Assert.Equal("InvalidIdentity", error.Code);
    }

    [Fact]
    public async Task SelectedMediaRelationshipsIncludeItsOwnersAndOnlyLinksThatReachThatIdentity()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key);

        var relationships = await new AudioRelationships(snapshot, result.Catalog).ResolveForMediaAsync(media.Identity!);

        Assert.Contains(relationships, link => link.Kind == AssetRelationshipKind.Member &&
            link.Reference.Namespace == IdentifierNamespace.Bank && link.NavigationTarget?.Key == bank.Identity!.Key);
        Assert.Contains(relationships, link => link.Kind == AssetRelationshipKind.Container &&
            link.NavigationTarget?.Key == bank.ContainerIdentity!.Key);
        Assert.Contains(relationships, link => link.Kind == AssetRelationshipKind.Member && link.Label == "Payload owner" &&
            link.NavigationTarget?.Key == media.PayloadIdentity!.Key);
        Assert.Contains(relationships, link => link.Reference.Namespace == IdentifierNamespace.Media &&
            link.NavigationTarget?.Key == media.Identity!.Key);
        Assert.DoesNotContain(relationships, link => link.Resolution.Candidates.Any(identity => identity.Ordinal == snapshot.Records.Single(row => row.Type == 1033).Ordinal));
    }

    [Fact]
    public async Task MatchingMediaIdsInOtherBanksDoNotStealTheOwningHircReference()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks, item => item.Name == "source_unique");
        var ownedMedia = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key && item.Id == 0x10203040u);
        Assert.NotEqual(bank.Key, Assert.Single(result.Catalog.Media, item => item.Id == 0x10203040u && item.BankKey != bank.Key).BankKey);

        var report = await new AudioRelationships(snapshot, result.Catalog).ResolveBankAsync(bank.Identity!);
        var link = Assert.Single(report.Relationships, relationship => relationship.Reference.Namespace == IdentifierNamespace.Media &&
            relationship.Reference.Id == "10203040" && relationship.Source.MemberPath?.StartsWith("hirc/", StringComparison.Ordinal) == true);
        Assert.Equal(ReferenceStatus.Unique, link.Resolution.Status);
        Assert.Equal(ownedMedia.Identity!.Key, link.NavigationTarget!.Key);
        Assert.Equal(bank.Key, ownedMedia.BankKey);
    }

    [Fact]
    public async Task ConcurrentRelatedRequestsShareOneBankGraphRead()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var reads = 0;
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            Interlocked.Increment(ref reads);
            await Task.Delay(60, token);
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var relationships = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);

        var results = await Task.WhenAll(
            relationships.ResolveBankAsync(bank.Identity!),
            relationships.ResolveBankAsync(bank.Identity!));

        Assert.Equal(1, reads);
        Assert.All(results, report => Assert.Contains(report.Relationships, link => link.Kind == AssetRelationshipKind.Container));
    }

    [Fact]
    public async Task CancelledBankGraphReadIsRemovedAndCanBeRetried()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var reads = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            }
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var relationships = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);
        using var cancellation = new CancellationTokenSource();
        var first = relationships.ResolveBankAsync(bank.Identity!, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var retry = await relationships.ResolveBankAsync(bank.Identity!);

        Assert.Equal(2, reads);
        Assert.Contains(retry.Relationships, link => link.Kind == AssetRelationshipKind.Container && link.NavigationTarget is not null);
    }

    [Fact]
    public async Task CachedGraphRejectsSameStampBankPayloadEditsByHash()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var stableStamps = new FixedRevisionProvider(snapshot);
        var reader = new DetroitAudioSnapshotReader(stableStamps);
        var relationships = new AudioRelationships(snapshot, result.Catalog, reader, revisionProvider: stableStamps);
        _ = await relationships.ResolveBankAsync(bank.Identity!);

        using (var stream = new FileStream(bank.Slice.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var offset = bank.Slice.Offset + bank.Slice.Length - 1;
            stream.Position = offset;
            var original = stream.ReadByte();
            Assert.NotEqual(-1, original);
            stream.Position = offset;
            stream.WriteByte((byte)(original ^ 0x01));
            stream.Flush(flushToDisk: true);
        }

        var error = await Assert.ThrowsAsync<ArchiveException>(() => relationships.ResolveBankAsync(bank.Identity!));
        Assert.Equal("SourceChanged", error.Code);
        var retry = await Assert.ThrowsAsync<ArchiveException>(() => relationships.ResolveBankAsync(bank.Identity!));
        Assert.Equal("SourceChanged", retry.Code);
    }

    [Fact]
    public async Task SameStampMediaEditBeforeFirstRelationshipReadCanOutdateCatalogCompleteness()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var stamps = new FixedRevisionProvider(snapshot);
        var reader = new DetroitAudioSnapshotReader(stamps);
        var descriptorIdentity = new ResourceIdentity(snapshot.IndexContentId,
            snapshot.Records.Single(row => row.Type == 1022).Ordinal);
        // Normalize the synthetic fixture's declared memory size before creating the catalog,
        // so its embedded media starts with a Complete classification.
        var initialGraph = await reader.ReadBankGraphAsync(snapshot, descriptorIdentity);
        var sound = Assert.Single(initialGraph.Objects.Values, node => node.Type == 2);
        using (var stream = new FileStream(sound.BodySlice!.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = sound.BodySlice.Offset + 9; // Wwise source in-memory size.
            stream.Write(new byte[4]);
            stream.Flush(flushToDisk: true);
        }
        var result = await reader.ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey == bank.Key);
        Assert.Equal(MediaCompleteness.Complete, media.Completeness);
        Assert.True(bank.MetadataReadCoverageComplete);
        Assert.Contains(bank.MetadataReadDigests, digest => digest.Role == "HIRC");
        Assert.True(media.MetadataReadCoverageComplete);
        Assert.Contains(media.MetadataReadDigests, digest => digest.Role == "WEM");
        var baselineHash = await HashSliceAsync(media.Slice);

        using (var stream = new FileStream(media.Slice.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = media.Slice.Offset;
            var original = stream.ReadByte();
            Assert.Equal((byte)'R', (byte)original);
            stream.Position = media.Slice.Offset;
            stream.WriteByte((byte)'B');
            stream.Flush(flushToDisk: true);
        }
        Assert.NotEqual(baselineHash, await HashSliceAsync(media.Slice));

        var relationships = new AudioRelationships(snapshot, result.Catalog, reader, revisionProvider: stamps);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => relationships.ResolveBankAsync(bank.Identity!));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(MediaCompleteness.Complete, media.Completeness);
        Assert.True(media.IsRiff);
        var retry = await Assert.ThrowsAsync<ArchiveException>(() => relationships.ResolveBankAsync(bank.Identity!));
        Assert.Equal("SourceChanged", retry.Code);
    }

    [Fact]
    public async Task LegacyMediaWithoutDigestBaselineIsAllowedForReliableUnchangedStamps()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        Assert.True(snapshot.ReliableVolumeStamps);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var parsed = Assert.Single(result.Catalog.Media, item => item.BankKey.Length > 0);
        var legacy = new MediaEntry { Identity = parsed.Identity, PayloadIdentity = parsed.PayloadIdentity, BankKey = parsed.BankKey };

        await new AudioRelationships(snapshot, result.Catalog).VerifyMediaBaselineAsync(legacy);

        var dialogue = Assert.Single(result.Catalog.Media, item => item.Language == "ENG");
        Assert.True(dialogue.MetadataReadCoverageComplete);
        foreach (var sourceRow in snapshot.Records.Where(row => row.Type is 4091 or 1031))
        {
            var volume = snapshot.Volumes.Single(item => item.Package == sourceRow.Package);
            Assert.Contains(dialogue.MetadataReadDigests, digest =>
                Path.GetFullPath(digest.Slice.FilePath) == Path.GetFullPath(volume.FilePath) &&
                digest.Slice.Offset == sourceRow.Offset && digest.Slice.Length == sourceRow.StoredSize);
        }
    }

    [Fact]
    public async Task IncompleteMediaReadBaselineIsRejectedWhenSourceStampsAreUnreliable()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var original = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var volumes = original.Volumes.Select(volume => volume with { Stamp = volume.Stamp with { Reliable = false } }).ToArray();
        var snapshot = original with
        {
            Volumes = volumes,
            IndexStamp = original.IndexStamp with { Reliable = false },
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(volumes)
        };
        var result = await new DetroitAudioSnapshotReader(new FixedRevisionProvider(snapshot)).ReadSnapshotAsync(snapshot);
        var media = Assert.Single(result.Catalog.Media, item => item.BankKey.Length > 0);
        media.MetadataReadDigests.Clear();
        media.MetadataReadCoverageComplete = false;

        var verifier = new AudioMetadataVerifier(snapshot, new FixedRevisionProvider(snapshot));
        var error = await Assert.ThrowsAsync<ArchiveException>(() => verifier.VerifyMediaAsync(media));

        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task RepeatedMediaVerificationReportsSharedBatchDigestWork()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const int mediaCount = 24;
        var actualBankId = FormatsFixtureBuilder.Fnv1("digest_batch");
        var media = Enumerable.Range(0, mediaCount)
            .Select(index => (Id: 0x10000000u + (uint)index, Data: FormatsFixtureBuilder.MakeWem([(byte)index, 2, 3, 4])))
            .ToArray();
        var sources = media.Select(item => (item.Id, (byte)0, (uint)item.Data.Length, 0x00040001u)).ToArray();
        fixture.AddBankFixture(1, "digest_batch", FormatsFixtureBuilder.MakeBankForSources(actualBankId, media, sources));
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var entries = catalog.Media.Where(item => item.BankName == "digest_batch").ToArray();
        Assert.Equal(mediaCount, entries.Length);
        Assert.All(entries, item => Assert.True(item.MetadataReadCoverageComplete));

        static string DigestKey(AudioMetadataReadDigest digest) =>
            $"{Path.GetFullPath(digest.Slice.FilePath)}|{digest.Slice.Offset}|{digest.Slice.Length}|{digest.Sha256}";
        var perFileBytes = entries.Sum(item => item.MetadataReadDigests.Sum(digest => digest.Slice.Length));
        var sharedDigests = entries.SelectMany(item => item.MetadataReadDigests).DistinctBy(DigestKey).ToArray();
        var uniqueBytes = sharedDigests.Sum(digest => digest.Slice.Length);
        var verifier = new AudioMetadataVerifier(snapshot);
        var timer = Stopwatch.StartNew();
        foreach (var entry in entries) await verifier.VerifyMediaAsync(entry);
        timer.Stop();
        var lease = verifier.CreateLease();
        var leaseTimer = Stopwatch.StartNew();
        foreach (var entry in entries) await lease.VerifyMediaAsync(entry);
        leaseTimer.Stop();
        lease.Dispose();

        Assert.True(perFileBytes > uniqueBytes * 10, $"Expected shared-bank digests to dominate: per-file={perFileBytes}, unique={uniqueBytes}.");
        Console.WriteLine($"Media={entries.Length}; per-file verification bytes={perFileBytes}; unique baseline bytes={uniqueBytes}; repeated verifier elapsed={timer.Elapsed.TotalMilliseconds:F2} ms; lease elapsed={leaseTimer.Elapsed.TotalMilliseconds:F2} ms.");
    }

    [Fact]
    public async Task VerificationLeaseRejectsExistingWritersPinsSourcesAndUnlocksOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var media = Assert.Single(catalog.Media, item => item.BankKey.Length > 0);
        var path = snapshot.Volumes.Single(volume => volume.Package == 0).FilePath;
        var verifier = new AudioMetadataVerifier(snapshot);

        using (var existingWriter = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (var blockedLease = verifier.CreateLease())
        {
            var busy = await Assert.ThrowsAsync<ArchiveException>(() => blockedLease.VerifyMediaAsync(media));
            Assert.Equal("SourceBusy", busy.Code);
        }

        using (var lease = verifier.CreateLease())
        {
            await lease.VerifyMediaAsync(media);
            Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            var movedPath = path + ".lease-probe";
            Assert.ThrowsAny<IOException>(() => File.Move(path, movedPath));
            Assert.False(File.Exists(movedPath));
        }

        using var writerAfterDispose = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task VerificationLeaseRejectsSameStampChangeOnTheNextFileVolume()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(dialoguePackage: 2);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var stamps = new FixedRevisionProvider(snapshot);
        var catalog = (await new DetroitAudioSnapshotReader(stamps).ReadSnapshotAsync(snapshot)).Catalog;
        var bankMedia = Assert.Single(catalog.Media, item => item.BankKey.Length > 0);
        var dialogue = Assert.Single(catalog.Media, item => item.Language == "ENG");
        var laterVolume = snapshot.Volumes.Single(volume => volume.Package == 2);
        using var lease = new AudioMetadataVerifier(snapshot, stamps).CreateLease();

        await lease.VerifyMediaAsync(bankMedia);
        var completed = new List<string> { bankMedia.Identity!.Key };
        var laterDigest = dialogue.MetadataReadDigests.First(digest =>
            Path.GetFullPath(digest.Slice.FilePath) == Path.GetFullPath(laterVolume.FilePath));
        using (var writer = new FileStream(laterVolume.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            writer.Position = laterDigest.Slice.Offset;
            var original = writer.ReadByte();
            Assert.NotEqual(-1, original);
            writer.Position = laterDigest.Slice.Offset;
            writer.WriteByte((byte)(original ^ 0x01));
            writer.Flush(flushToDisk: true);
        }

        var changed = await Assert.ThrowsAsync<ArchiveException>(() => lease.VerifyMediaAsync(dialogue));
        Assert.Equal("SourceChanged", changed.Code);
        Assert.Single(completed);
    }

    [Fact]
    public async Task VerificationLeaseCancellationAndConflictChecksDoNotCacheUnverifiedExtents()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var media = Assert.Single(catalog.Media, item => item.BankKey.Length > 0);
        var lease = new AudioMetadataVerifier(snapshot).CreateLease();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.VerifyMediaAsync(media, cancelled.Token));
        await lease.VerifyMediaAsync(media);
        lease.Dispose();

        var conflict = new MediaEntry
        {
            Identity = media.Identity, PayloadIdentity = media.PayloadIdentity, BankKey = media.BankKey,
            MetadataReadCoverageComplete = true,
            MetadataReadDigests = [media.MetadataReadDigests[0], media.MetadataReadDigests[0] with { Sha256 = new string('0', 64) }]
        };
        using var conflictLease = new AudioMetadataVerifier(snapshot).CreateLease();
        var mismatch = await Assert.ThrowsAsync<ArchiveException>(() => conflictLease.VerifyMediaAsync(conflict));
        Assert.Equal("SourceChanged", mismatch.Code);

        var oversized = new MediaEntry
        {
            Identity = media.Identity, PayloadIdentity = media.PayloadIdentity, BankKey = media.BankKey,
            MetadataReadCoverageComplete = true,
            MetadataReadDigests = [media.MetadataReadDigests[0] with
            {
                Slice = media.MetadataReadDigests[0].Slice with { Length = 4L * 1024 * 1024 * 1024 + 1 }
            }]
        };
        using var boundedLease = new AudioMetadataVerifier(snapshot).CreateLease();
        var overBudget = await Assert.ThrowsAsync<ArchiveException>(() => boundedLease.VerifyMediaAsync(oversized));
        Assert.Equal("SourceChanged", overBudget.Code);
    }

    [Fact]
    public async Task VerificationLeaseEvictsAtCacheLimitAndKeepsPerCallChecksBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var media = Assert.Single(catalog.Media, item => item.BankKey.Length > 0);
        var path = snapshot.Volumes.Single(volume => volume.Package == 0).FilePath;
        byte firstByte, secondByte;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Position = 0; firstByte = (byte)stream.ReadByte();
            stream.Position = 1; secondByte = (byte)stream.ReadByte();
        }
        var firstDigest = new AudioMetadataReadDigest(new DataSlice(path, 0, 1), Convert.ToHexString(SHA256.HashData([firstByte])).ToLowerInvariant(), "descriptor", media.Identity);
        var secondDigest = new AudioMetadataReadDigest(new DataSlice(path, 1, 1), Convert.ToHexString(SHA256.HashData([secondByte])).ToLowerInvariant(), "descriptor", media.Identity);
        MediaEntry WithDigest(AudioMetadataReadDigest digest) => new()
        {
            Identity = media.Identity, PayloadIdentity = media.PayloadIdentity, BankKey = media.BankKey,
            MetadataReadCoverageComplete = true, MetadataReadDigests = [digest]
        };
        var first = WithDigest(firstDigest);
        var second = WithDigest(secondDigest);
        using var lease = new AudioMetadataVerifier(snapshot).CreateLease(maximumCachedExtents: 1, maximumCachedBytes: 1);

        await lease.VerifyMediaAsync(first);
        Assert.Equal(1, lease.CachedExtentCount);
        await lease.VerifyMediaAsync(second);
        Assert.Equal(1, lease.CachedExtentCount);
        Assert.Equal(1, lease.CachedBytes);
        await lease.VerifyMediaAsync(first);
        Assert.Equal(1, lease.CachedExtentCount);

        var conflict = WithDigest(firstDigest with { Sha256 = new string('0', 64) });
        var changed = await Assert.ThrowsAsync<ArchiveException>(() => lease.VerifyMediaAsync(conflict));
        Assert.Equal("SourceChanged", changed.Code);
    }

    [Fact]
    public async Task ConcurrentSameBankRequestsUseOneLazyGraphLoad()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var readCount = 0;
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            Interlocked.Increment(ref readCount);
            await Task.Delay(60, token);
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var service = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);

        var reports = await Task.WhenAll(service.ResolveBankAsync(bank.Identity!), service.ResolveBankAsync(bank.Identity!));

        Assert.Equal(1, readCount);
        Assert.All(reports, report => Assert.Contains(report.Relationships, link => link.Kind == AssetRelationshipKind.Container));
    }

    [Fact]
    public async Task RapidSelectedMediaRequestsFromOneBankReuseItsLazyGraph()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint firstId = 0x11223344;
        const uint secondId = 0x55667788;
        var bankBytes = FormatsFixtureBuilder.MakeBankForSources(Fnv("multi_media"),
            [(firstId, FormatsFixtureBuilder.MakeWem([1, 2, 3])), (secondId, FormatsFixtureBuilder.MakeWem([4, 5, 6]))],
            [(firstId, (byte)0, 0u, 0x00040001u), (secondId, (byte)0, 0u, 0x00040001u)]);
        fixture.AddBankFixture(1, "multi_media", bankBytes);
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var media = result.Catalog.Media.Where(item => item.BankKey == bank.Key).OrderBy(item => item.Id).ToArray();
        Assert.Equal(new[] { firstId, secondId }, media.Select(item => item.Id));
        var reader = new DetroitAudioSnapshotReader();
        var readCount = 0;
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            Interlocked.Increment(ref readCount);
            await Task.Delay(60, token);
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var service = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);

        var links = await Task.WhenAll(media.Select(item => service.ResolveForMediaAsync(item.Identity!)));

        Assert.Equal(1, readCount);
        Assert.All(links, items => Assert.Contains(items, item => item.Kind == AssetRelationshipKind.Container));
    }

    [Fact]
    public async Task ClearingSessionGraphCacheForcesTheNextRequestToReload()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var reads = 0;
        async Task<BankInfo> Load(ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token)
        { Interlocked.Increment(ref reads); return await reader.ReadBankGraphAsync(source, identity, token); }
        var relationships = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: Load);
        _ = await relationships.ResolveBankAsync(bank.Identity!);
        relationships.ClearBankGraphCache();

        _ = await relationships.ResolveBankAsync(bank.Identity!);

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ClearingSessionCacheDrainsAnInFlightGraphReadAndAllowsRetry()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var reads = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            }
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var service = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);
        var pending = service.ResolveBankAsync(bank.Identity!);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ClearBankGraphCache();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var retry = await service.ResolveBankAsync(bank.Identity!);

        Assert.Equal(2, reads);
        Assert.Contains(retry.Relationships, link => link.Kind == AssetRelationshipKind.Container && link.NavigationTarget is not null);
    }

    [Fact]
    public async Task CancelledFirstGraphRequestCanBeRetried()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reader = new DetroitAudioSnapshotReader();
        var readCount = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = async (ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token) =>
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            }
            return await reader.ReadBankGraphAsync(source, identity, token);
        };
        var service = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: loader);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ResolveBankAsync(bank.Identity!, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var retry = await service.ResolveBankAsync(bank.Identity!);

        Assert.Equal(2, readCount);
        Assert.Contains(retry.Relationships, link => link.Kind == AssetRelationshipKind.Container && link.NavigationTarget is not null);
    }

    [Fact]
    public async Task MalformedLazyBankGraphIsReportedAndNotCached()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var bank = Assert.Single(result.Catalog.Banks);
        var reads = 0;
        Task<BankInfo> RejectMalformedGraph(ArchiveSnapshot _, ResourceIdentity __, CancellationToken ___)
        {
            Interlocked.Increment(ref reads);
            throw new ArchiveException("MalformedBankPayload", "The synthetic bank payload is malformed.");
        }
        var service = new AudioRelationships(snapshot, result.Catalog, bankGraphLoader: RejectMalformedGraph);

        var first = await service.ResolveBankAsync(bank.Identity!);
        var retry = await service.ResolveBankAsync(bank.Identity!);

        Assert.Equal(2, reads);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Contains("MalformedBankPayload", StringComparison.Ordinal));
        Assert.Contains(retry.Diagnostics, diagnostic => diagnostic.Contains("MalformedBankPayload", StringComparison.Ordinal));
        Assert.Contains(retry.Relationships, relationship => relationship.Kind == AssetRelationshipKind.Container &&
            relationship.NavigationTarget?.Key == bank.ContainerIdentity!.Key);
    }

    private sealed class FixedRevisionProvider : ISourceRevisionProvider
    {
        private readonly Dictionary<string, FileStamp> stamps;
        private readonly FileRevisionProvider fallback = new();

        public FixedRevisionProvider(ArchiveSnapshot snapshot)
        {
            stamps = snapshot.Volumes.ToDictionary(volume => Path.GetFullPath(volume.FilePath), volume => volume.Stamp,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            stamps[Path.GetFullPath(snapshot.IndexPath)] = snapshot.IndexStamp;
        }

        public FileStamp Capture(string path) => stamps.TryGetValue(Path.GetFullPath(path), out var stamp) ? stamp : fallback.Capture(path);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) =>
            stamps.TryGetValue(Path.GetFullPath(path), out var stamp) ? stamp : fallback.Capture(handle, path);
    }

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant())) hash = (hash ^ value) * 16777619u;
        return hash;
    }

    private static async Task<string> HashSliceAsync(DataSlice slice)
    {
        await using var stream = new FileStream(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = slice.Offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long remaining = slice.Length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
