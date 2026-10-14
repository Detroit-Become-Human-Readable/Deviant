using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Localization;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationReaderTests
{
    [Fact]
    public async Task ReadsV5AndV6TextAndPreservesDuplicateOccurrencesAndExactExtents()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(5,
            ("ENG", [LocalizationFixture.DirectText("DUP_KEY", "First line"), LocalizationFixture.DirectText("DUP_KEY", "Second line")]),
            ("FRE", [LocalizationFixture.DirectText("DUP_KEY", "Voix de la rue")]))));
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("JAP", [LocalizationFixture.DirectText("UNICODE_KEY", "街の声")]))));
        var snapshot = await fixture.BuildAsync();
        var reader = new LocalizationSnapshotReader();
        var results = new List<LocalizationRecord>();
        foreach (var resource in snapshot.Records) results.AddRange((await reader.ReadResourceAsync(snapshot, resource)).Records);

        var duplicates = results.Where(row => row.Key == "DUP_KEY" && row.RawLanguageCode == "ENG").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.Equal(new[] { "First line", "Second line" }, duplicates.Select(row => row.Text));
        Assert.NotEqual(duplicates[0].Identity.Key, duplicates[1].Identity.Key);
        Assert.All(duplicates, row => Assert.Equal(LocalizationRecordStatus.Complete, row.Status));
        Assert.Equal("JAP", Assert.Single(results, row => row.Key == "UNICODE_KEY").RawLanguageCode);
        Assert.Equal("JPN", Assert.Single(results, row => row.Key == "UNICODE_KEY").Language);
        var textRow = duplicates[0];
        var textExtent = Assert.Single(textRow.FieldExtents, field => field.Field == "Text");
        var file = await File.ReadAllBytesAsync(fixture.VolumePath);
        var utf16 = file.AsSpan(checked((int)textExtent.Offset), checked((int)textExtent.Length));
        Assert.Equal(textRow.Text, new UnicodeEncoding(false, false, true).GetString(utf16));
        Assert.Equal(textRow.RecordOffset, textRow.Identity.MemberPath is not null ? ParseRecordOffset(textRow.Identity.MemberPath) : -1);
    }

    [Fact]
    public async Task PreservesNullAndOpaqueNineBytePointerRecordsAsUnresolved()
    {
        using var fixture = new LocalizationFixture();
        var pointer = LocalizationFixture.PointerRecord(
            ("NULL_KEY", 0, []),
            ("OPAQUE_KEY", 1, [0, 1, 2, 3, 4, 5, 6, 7, 8]));
        var payload = LocalizationFixture.MakeLocalization(6, ("ENG", [pointer]));
        fixture.AddResource(LocalizationFixture.Wrap(payload, referenceCount: 2));
        var snapshot = await fixture.BuildAsync();
        var parsed = await new LocalizationSnapshotReader().ReadResourceAsync(snapshot, Assert.Single(snapshot.Records));

        Assert.Equal(2, parsed.Records.Count);
        Assert.All(parsed.Records, row =>
        {
            Assert.Equal(LocalizationRecordKind.Pointer, row.Kind);
            Assert.Equal(LocalizationRecordStatus.Unresolved, row.Status);
            Assert.Equal("Pointer target is not interpreted.", row.Diagnostic);
            Assert.Equal(row.RecordExtent.Length, parsed.Records[0].RecordExtent.Length);
        });
        Assert.Equal(0, parsed.Records[0].PointerKind);
        Assert.Null(parsed.Records[0].PointerPayloadBase64);
        Assert.Equal(1, parsed.Records[1].PointerKind);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], Convert.FromBase64String(parsed.Records[1].PointerPayloadBase64!));
        Assert.Contains(parsed.Records[1].FieldExtents, extent => extent.Field == "PointerPayload" && extent.Length == 9);
    }

    [Fact]
    public async Task KeepsUnknownMalformedAndAmbiguousPayloadsOpaqueWithPreciseSourceRanges()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.UnknownRecord(0xABCDEF01, 300, [1, 2, 3, 4])]))));
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.PointerRecord(("BAD_KEY", 2, []) )])), referenceCount: 1));
        var direct = LocalizationFixture.MakeLocalization(6, ("ENG", [LocalizationFixture.DirectText("TRUNC", "Short")]));
        var marker = "LOCALIZ_"u8;
        var localizationOffset = LocalizationFixture.FindSequence(direct, marker);
        var bodyLengthOffset = localizationOffset + 8 + 4;
        var oldBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(direct.AsSpan(bodyLengthOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(direct.AsSpan(bodyLengthOffset, 4), oldBodyLength + 128);
        fixture.AddResource(LocalizationFixture.Wrap(direct));
        var one = LocalizationFixture.MakeLocalization(6, ("ENG", [LocalizationFixture.DirectText("A", "a")]));
        var two = LocalizationFixture.MakeLocalization(6, ("FRE", [LocalizationFixture.DirectText("B", "b")]));
        fixture.AddResource(LocalizationFixture.Wrap(one.Concat(two).ToArray()));
        var invalidUtf16 = LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectTextBytes("BAD_UTF16", [0, 0xD8])])));
        fixture.AddResource(invalidUtf16);
        var snapshot = await fixture.BuildAsync();
        var reader = new LocalizationSnapshotReader();
        var results = new List<LocalizationResourceResult>();
        foreach (var resource in snapshot.Records) results.Add(await reader.ReadResourceAsync(snapshot, resource));

        Assert.Equal("UnsupportedLocalizationRecord", Assert.Single(results[0].Records).UnknownFields["diagnosticCode"]);
        Assert.Equal(LocalizationRecordStatus.Malformed, Assert.Single(results[1].Records).Status);
        Assert.Equal("LocalizationBodyTruncated", Assert.Single(results[2].Diagnostics).Code);
        Assert.Equal("MultipleLocalizationPayloadCandidates", Assert.Single(results[3].Diagnostics).Code);
        Assert.Equal("MalformedLocalizationUtf16", Assert.Single(results[4].Diagnostics).Code);
        Assert.All(results.SelectMany(result => result.Records), row =>
        {
            Assert.True(row.RecordExtent.Offset >= 0);
            Assert.True(row.RecordExtent.Length >= 0);
        });
    }

    [Fact]
    public async Task SessionPagesSearchesAndExportsDuplicateRowsAndFinalizesCancellation()
    {
        using var fixture = new LocalizationFixture();
        var direct = Enumerable.Range(1, 20).Select(index => LocalizationFixture.DirectText("DUP_KEY", $"街の声 {index}")).ToArray();
        direct = direct.Concat([
            LocalizationFixture.DirectText("DUP_KEY", "Voice in ENG"),
            LocalizationFixture.DirectText("DUP_KEY_SUFFIX", "Text that only mentions DUP_KEY")
        ]).ToArray();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", direct), ("FRE", [LocalizationFixture.DirectText("DUP_KEY", "Voix française")]))));
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("DUP_KEY", "Same key in another resource")]))));
        var snapshot = await fixture.BuildAsync();
        var outputRoot = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "deviant-localization-output-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
            await session.EnrichmentTask;
            Assert.True(session.IsEnrichmentComplete);
            Assert.Contains("ENG", session.Languages);
            var page = await session.QueryAsync(new LocalizationQuery(Search: "街の声", Language: "ENG", Limit: 5));
            Assert.Equal(20, page.Total);
            Assert.Equal(5, page.Records.Count);
            var variants = await session.GetVariantsAsync(page.Records[0].Identity);
            Assert.Equal(22, variants.Count);
            var otherOccurrences = await session.GetOtherOccurrencesAsync(page.Records[0].Identity);
            Assert.Single(otherOccurrences);
            Assert.NotEqual(page.Records[0].ResourceIdentity.Ordinal, otherOccurrences[0].ResourceIdentity.Ordinal);
            Assert.Equal(2, session.Sources.Count);
            Assert.Equal(24, session.RecordCount);
            Assert.Equal(24, (await session.QueryAsync(new LocalizationQuery(Search: "DUP_KEY", Limit: 100))).Total);
            Assert.Equal(23, (await session.QueryAsync(new LocalizationQuery(ExactKey: "DUP_KEY", Limit: 100))).Total);

            var jsonPath = Path.Combine(outputRoot, "selected.json");
            var jsonReport = await session.ExportAsync(LocalizationExportSelection.Selected(page.Records.Take(2).Select(row => row.Identity)),
                LocalizationExportFormat.Json, jsonPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, Encoding.UTF8));
            Assert.Equal(2, document.RootElement.GetProperty("entries").GetArrayLength());
            Assert.Equal(2, jsonReport.Manifest.CompletedCount);
            Assert.True(File.Exists(jsonReport.ManifestPath));
            Assert.Contains("街の声", await File.ReadAllTextAsync(jsonPath, Encoding.UTF8));
            Assert.DoesNotContain(fixture.Root, await File.ReadAllTextAsync(jsonReport.ManifestPath, Encoding.UTF8), StringComparison.OrdinalIgnoreCase);

            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress<LocalizationReadProgress>(value => { if (value.Phase == "Exporting localization CSV" && value.Records >= 1) cancel.Cancel(); });
            var csvPath = Path.Combine(outputRoot, "matching.csv");
            var csvReport = await session.ExportAsync(LocalizationExportSelection.Matching(new LocalizationQuery(Search: "DUP_KEY", Limit: 10_000)),
                LocalizationExportFormat.Csv, csvPath, progress, cancel.Token);
            Assert.True(csvReport.Manifest.Cancelled);
            Assert.True(csvReport.Manifest.CompletedCount >= 1);
            Assert.True(csvReport.Manifest.CancelledCount > 0);
            Assert.Contains("街の声", await File.ReadAllTextAsync(csvPath, Encoding.UTF8));
            Assert.True(File.Exists(csvReport.ManifestPath));

            await session.DisposeAsync();
            using (var cached = Deviant.Catalog.FeatureStore.Open(snapshot, fixture.CachePath))
            {
                using var generation = cached.OpenLocalizationGeneration<LocalizationGenerationHead>(
                    LocalizationCacheContract.RecordStoreVersion, LocalizationCacheContract.SearchProjectionVersion,
                    HeadIdentity, head => head.IndexContentId == snapshot.IndexContentId);
                Assert.NotNull(generation);
                Assert.Equal(24, generation!.Head.RecordCount);
                Assert.Equal(24, cached.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(
                    generation, new AssetQuery(Family: LocalizationSchema.AssetFamily, Limit: 1)).Total);
            }
            await using var warmed = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
            Assert.True(warmed.IsEnrichmentComplete);
            Assert.Equal(24, warmed.RecordCount);
            Assert.Equal(24, (await warmed.QueryAsync(new LocalizationQuery(Limit: 100))).Total);
        }
        finally { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true); }
    }

    private static LocalizationGenerationIdentity HeadIdentity(LocalizationGenerationHead head) => new(
        head.InputSnapshotId, head.ParsedResultId, head.RecordStoreVersion, head.SearchProjectionVersion,
        head.JsonPolicyVersion, head.RecordCount);

    [Fact]
    public async Task UnresolvedPointersAreFilterableAsMissingAssets()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.PointerRecord(("NULL_TARGET", 0, []), ("RAW_TARGET", 1, [1, 2, 3, 4, 5, 6, 7, 8, 9]))])), referenceCount: 2));
        var snapshot = await fixture.BuildAsync();
        await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
        await session.EnrichmentTask;

        var allRows = await session.QueryAsync(new LocalizationQuery(Limit: 10));
        Assert.Equal(2, allRows.Total);
        Assert.All(allRows.Records, row => Assert.Equal(LocalizationRecordStatus.Unresolved, row.Status));
        var missing = await session.QueryAsync(new LocalizationQuery(Availability: AssetAvailability.Missing, Limit: 10));
        Assert.Equal(2, missing.Total);
        Assert.All(missing.Records, row => Assert.Equal(LocalizationRecordStatus.Unresolved, row.Status));

        var outputRoot = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "deviant-localization-pointer-csv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var csvPath = Path.Combine(outputRoot, "pointers.csv");
            var report = await session.ExportAsync(LocalizationExportSelection.Selected(missing.Records.Select(row => row.Identity)),
                LocalizationExportFormat.Csv, csvPath);
            var csv = await File.ReadAllTextAsync(csvPath, Encoding.UTF8);
            Assert.Equal(2, report.Manifest.CompletedCount);
            Assert.Contains(",6,1016,0,0,opaque-pointer,", csv);
            Assert.Contains(",6,1016,0,1,opaque-pointer,", csv);
            Assert.Contains("recordFlags", csv);
            Assert.Contains("pointerCount", csv);
        }
        finally { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); }
    }

    [Fact]
    public async Task ResourceResolverRejectsSourceChangedAfterSnapshot()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("KEY", "Text")]))));
        var snapshot = await fixture.BuildAsync();
        var data = await File.ReadAllBytesAsync(fixture.VolumePath);
        data[^1] ^= 0x4;
        await File.WriteAllBytesAsync(fixture.VolumePath, data);

        var error = await Assert.ThrowsAsync<Deviant.Core.ArchiveException>(() =>
            new LocalizationSnapshotReader().ReadResourceAsync(snapshot, Assert.Single(snapshot.Records)));

        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task ReaderHonorsCancellationBeforeBoundedResourceRead()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("KEY", "Text")]))));
        var snapshot = await fixture.BuildAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalizationSnapshotReader().ReadResourceAsync(snapshot, Assert.Single(snapshot.Records), cancellation.Token));
    }

    private static long ParseRecordOffset(string memberPath)
    {
        var parts = memberPath.Split('/');
        return long.Parse(parts[3][1..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
