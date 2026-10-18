using System.Buffers.Binary;
using System.Text;
using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Audio;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class MidiTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 2)]
    public async Task ArchiveMidiKeepsDuplicateIdsDistinctAndExportsStandardBytes(int format, int tracks)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        using var destination = FormatsFixtureBuilder.CreateEmpty();
        byte[] midi = MakeMidi(format, tracks);
        fixture.AddResource(65, 123, Wrap(midi)); fixture.AddResource(65, 123, Wrap(midi)); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        AudioCatalog? published = null;
        var progress = new ImmediateProgress<AudioReadProgress>(value => published = value.Snapshot ?? published);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot, progress);
        Assert.Equal(2, result.Catalog.Media.Count);
        Assert.Equal(2, result.Catalog.Media.Select(media => media.Identity).Distinct().Count());
        Assert.All(result.Catalog.Media, media =>
        {
            Assert.Equal("midi", media.Identity!.MemberPath); Assert.Equal(media.Identity, media.PayloadIdentity);
            Assert.Equal("MIDI", media.Category); Assert.Equal("MIDI", media.Codec); Assert.Equal("Cue / test", media.DisplayName);
            Assert.Equal(new MidiMetadata((ushort)format, (ushort)tracks, 480), media.Midi);
            Assert.Equal(midi.Length, media.Slice.Length); Assert.Equal(media.Slice, media.OriginalSlice);
            Assert.True(MediaPolicy.IsComplete(media)); Assert.False(MediaPolicy.CanPreview(media)); Assert.False(MediaPolicy.CanConvert(media));
            Assert.True(media.MetadataReadCoverageComplete); Assert.Equal(2, media.MetadataReadDigests.Count);
            Assert.All(media.MetadataReadDigests, digest => Assert.Equal(media.Identity with { MemberPath = null }, digest.Owner));
        });
        Assert.NotNull(published); Assert.All(published!.Media, media => Assert.NotNull(media.Midi));
        var request = new AudioExportRequest(snapshot, result.Catalog.Media.Select(media => new AudioExportSource(media.Identity!, media)).ToArray(), true);
        var service = new AudioExportService(new AudioService(new(null, null, null, null, null, null, null)));
        var output = Path.Combine(destination.DirectoryPath, "output");
        var report = await service.ExportAsync(request, new(output, AudioExportFormat.Original));
        Assert.Equal(2, report.Manifest.Entries.Select(entry => entry.RelativeOutput).Distinct().Count());
        Assert.All(report.Manifest.Entries, entry =>
        {
            Assert.Equal(AudioExportStatus.Completed, entry.Status); Assert.EndsWith(".mid", entry.RelativeOutput);
            Assert.Equal(midi, File.ReadAllBytes(Path.Combine(output, entry.RelativeOutput)));
        });
        foreach (var exportFormat in new[] { AudioExportFormat.Wav, AudioExportFormat.Ogg })
        {
            var rejected = await service.ExportAsync(request, new(Path.Combine(destination.DirectoryPath, exportFormat.ToString()), exportFormat));
            Assert.All(rejected.Manifest.Entries, entry => Assert.Equal("ConversionUnavailable", entry.ErrorCode));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MidiIdsDoNotEnterWwiseStreamCandidateResolution(bool includeWem)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); const uint sharedId = 0x12345678;
        fixture.AddBankFixture(1, "stream_bank", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("stream_bank"), [],
            [(sharedId, (byte)2, 0u, 0x00040001u)]));
        if (includeWem) fixture.AddBankFixture(2, "source_bank", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("source_bank"),
            [(sharedId, FormatsFixtureBuilder.MakeWem([1, 2, 3]))], [(sharedId, (byte)0, 0u, 0x00040001u)]));
        fixture.AddResource(65, sharedId, Wrap(MakeMidi(0, 1))); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        var sequence = Assert.Single(result.Catalog.Media, media => media.Midi is not null);
        var stream = Assert.Single(result.Catalog.Media, media => media.BankName == "stream_bank");
        Assert.Equal(includeWem ? MediaState.CompleteExternal : MediaState.MissingExternal, stream.State);
        Assert.DoesNotContain(stream.Candidates, candidate => candidate == sequence.Slice);
        Assert.DoesNotContain(stream.CandidateRejections, candidate => candidate.Slice == sequence.Slice);
        Assert.Empty(sequence.EventKeys);
    }

    [Fact]
    public void MidiPolicyRejectsPlaybackEvenWhenRiffFlagsArePresent()
    {
        var media = new MediaEntry
        {
            Codec = "MIDI", Midi = new(0, 1, 480), IsRiff = true,
            Slice = new("sequence.mid", 0, 26), Availability = SourceAvailability.Available,
            Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
            State = MediaState.CompleteEmbedded
        };
        Assert.True(MediaPolicy.IsComplete(media));
        Assert.False(MediaPolicy.CanPreview(media)); Assert.False(MediaPolicy.CanConvert(media));
        Assert.Throws<InvalidOperationException>(() => MediaPolicy.RequireConversion(media));
        media.Midi = null;
        Assert.False(MediaPolicy.CanPreview(media));
    }

    [Fact]
    public async Task OldSnapshotHeadRebuildKeepsVerifiedMidiCheckpointAndAliases()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); using var work = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(65, 1, Wrap(MakeMidi(0, 1))); fixture.WriteIndex();
        using var cache = new CacheManager(Path.Combine(work.DirectoryPath, "settings"), new CacheSettings(Path.Combine(work.DirectoryPath, "cache")));
        var aliases = new AliasStore(Path.Combine(work.DirectoryPath, "library"));
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        ArchiveSnapshot snapshot;
        AudioCatalog catalog;
        ResourceIdentity identity;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools))
        {
            snapshot = initial.Snapshot; catalog = initial.Catalog;
            identity = Assert.Single((await initial.QueryAsync(new(Codec: "MIDI"))).Rows).Identity;
            await initial.SetAliasAsync(identity, "Saved sequence");
        }
        using (var store = FeatureStore.Open(snapshot, cache.Location))
        {
            const string oldVersion = "audio-snapshot-m1.4-v2";
            store.SaveItems("audio.media", oldVersion, Array.Empty<MediaEntry>());
            var key = (string)typeof(AudioExplorerSession).GetMethod("BuildSnapshotFeatureKey", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [snapshot])!;
            store.Save(key, oldVersion, new
            {
                catalog.SourcePath, catalog.Fingerprint, catalog.IndexContentId, catalog.PhysicalSourceStamp,
                catalog.IndexStamp, catalog.SourceVolumes, catalog.IndexedResourceCount, catalog.EventMediaVersion
            });
        }
        var phases = new List<string>();
        await using var reopened = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, cache, aliases, tools,
            new ImmediateProgress<AudioExplorerProgress>(progress => phases.Add(progress.Phase)), CancellationToken.None, startEnrichment: false);
        var checkpoint = Assert.Single((await reopened.QueryAsync(new(Codec: "MIDI"))).Rows);
        Assert.Equal(identity, checkpoint.Identity);
        Assert.Equal("Saved sequence", checkpoint.UserAlias);
        Assert.Equal(new MidiMetadata(0, 1, 480), checkpoint.Media.Midi);
        Assert.False(reopened.IsEnrichmentComplete);
        await reopened.EnrichmentTask;
        var row = Assert.Single((await reopened.QueryAsync(new(Codec: "MIDI"))).Rows);
        Assert.Contains("Reading MIDI", phases);
        Assert.Equal(identity, row.Identity);
        Assert.Equal("Saved sequence", row.UserAlias);
        Assert.Equal(new MidiMetadata(0, 1, 480), row.Media.Midi);
    }

    [Fact]
    public async Task CacheReloadPreservesMidiMetadataFilteringAndAliases()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); using var work = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(65, 1, Wrap(MakeMidi(1, 2))); fixture.WriteIndex();
        var cache = new CacheManager(Path.Combine(work.DirectoryPath, "settings"), new CacheSettings(Path.Combine(work.DirectoryPath, "cache")));
        var aliases = new AliasStore(Path.Combine(work.DirectoryPath, "library"));
        ResourceIdentity identity;
        await using (var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, new(null, null, null, null, null, null, null)))
        {
            var row = Assert.Single((await session.QueryAsync(new(Codec: "MIDI", Search: "Cue"))).Rows);
            identity = row.Identity;
            Assert.True(row.CanExportOriginal); Assert.False(row.CanConvert); Assert.False(row.CanPreview);
            await session.SetAliasAsync(identity, "Sequence alias");
            Assert.Equal(identity, Assert.Single((await session.QueryAsync(new(Codec: "MIDI", Search: "Sequence alias"))).Rows).Identity);
        }
        await using var reopened = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, new(null, null, null, null, null, null, null));
        var restored = Assert.Single((await reopened.QueryAsync(new(Codec: "MIDI", Search: "Sequence alias"))).Rows);
        Assert.Equal(identity, restored.Identity); Assert.Equal("Sequence alias", restored.UserAlias);
        Assert.Equal(new MidiMetadata(1, 2, 480), restored.Media.Midi);
        Assert.True(restored.Media.MetadataReadCoverageComplete); Assert.Equal(2, restored.Media.MetadataReadDigests.Count);
    }

    [Fact]
    public async Task InvalidMidiResourcesDoNotHideValidNeighbours()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); byte[] midi = MakeMidi(1, 2);
        var truncated = Wrap(midi); BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(29), (uint)midi.Length + 1);
        var oversized = midi.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(18), uint.MaxValue);
        fixture.AddResource(65, 1, truncated); fixture.AddResource(65, 2, Wrap(oversized)); fixture.AddResource(65, 3, Wrap(midi)); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot);
        Assert.Equal(3u, Assert.Single(result.Catalog.Media).Id);
        Assert.Equal(2, result.Diagnostics.Count(item => item.Code == "MalformedMidiResource"));
    }

    [Fact]
    public async Task ChangedMidiWrapperCannotBeExportedFromAnOldClassification()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); using var destination = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(65, 1, Wrap(MakeMidi(0, 1))); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var media = Assert.Single((await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog.Media);
        var volume = Assert.Single(snapshot.Volumes);
        var bytes = File.ReadAllBytes(volume.FilePath); bytes[29] ^= 1; File.WriteAllBytes(volume.FilePath, bytes);
        var output = Path.Combine(destination.DirectoryPath, "output");
        var service = new AudioExportService(new AudioService(new(null, null, null, null, null, null, null)));
        var report = await service.ExportAsync(new(snapshot, [new(media.Identity!, media)], true), new(output, AudioExportFormat.Original));
        Assert.Equal("SourceChanged", Assert.Single(report.Manifest.Entries).ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(output, "*.mid", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExtendedHeaderVlqNameAndSmpteDivisionArePreserved()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var name = Encoding.UTF8.GetBytes(new string('N', 130));
        var events = new List<byte> { 0, 0xFF, 3, 0x81, 2 }; events.AddRange(name); events.AddRange([0, 0xFF, 0x2F, 0]);
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write("MThd"u8); Write32(writer, 8); Write16(writer, 0); Write16(writer, 1); Write16(writer, 0xE728); writer.Write((ushort)0);
            writer.Write("MTrk"u8); Write32(writer, events.Count); writer.Write(events.ToArray());
        }
        var bytes = stream.ToArray(); var path = Path.Combine(fixture.DirectoryPath, "extended.mid"); File.WriteAllBytes(path, bytes);
        var document = MidiReader.ReadStandard(new(path, 0, bytes.Length));
        Assert.Equal(0xE728, document.Metadata.Division); Assert.Equal(130, Assert.Single(document.Names).Text.Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MidiReader.ReadWrapped(new(path, 0, bytes.Length), cancelled.Token));
    }

    [Theory]
    [InlineData("running")] [InlineData("vlq")] [InlineData("end")] [InlineData("division")] [InlineData("tracks")]
    public void MalformedMidiEventsAreRejected(string kind)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var bytes = MakeMidi(0, 1);
        if (kind == "running") bytes[23] = 1;
        if (kind == "vlq") Array.Fill(bytes, (byte)0x80, 22, 4);
        if (kind == "end") bytes[^2] = 1;
        if (kind == "division") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0xE100);
        if (kind == "tracks") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 2);
        var path = Path.Combine(fixture.DirectoryPath, "malformed.mid"); File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => MidiReader.ReadStandard(new(path, 0, bytes.Length)));
    }

    private static byte[] Wrap(byte[] midi)
    {
        var result = new byte[33 + midi.Length]; "QZIP\0RAW_FILE"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(13), 2); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(17), (uint)midi.Length + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(21), 4); "MIDI"u8.CopyTo(result.AsSpan(25));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(29), (uint)midi.Length); midi.CopyTo(result, 33); return result;
    }
    private static byte[] MakeMidi(int format, int tracks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("MThd"u8); Write32(writer, 6); Write16(writer, format); Write16(writer, tracks); Write16(writer, 480);
        for (int track = 0; track < tracks; track++)
        {
            var name = Encoding.UTF8.GetBytes(track == 0 ? "Cue / test" : "Layer");
            var events = new List<byte> { 0, 0xFF, 3, (byte)name.Length }; events.AddRange(name);
            events.AddRange([0, 0xC0, 0, 0, 1, 0, 0xF0, 6, 45, 45, 45, 45, 45, 45, 0, 0xFF, 0x2F, 0]);
            writer.Write("MTrk"u8); Write32(writer, events.Count); writer.Write(events.ToArray());
        }
        return stream.ToArray();
    }
    private static void Write32(BinaryWriter writer, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value); writer.Write(bytes); }
    private static void Write16(BinaryWriter writer, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value); writer.Write(bytes); }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
