using System.Buffers.Binary;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationLanguageQualificationTests
{
    [Fact]
    public async Task TextSelectsOnlyThePointerFromItsExactStoredLanguageBlock()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildAsync(fixture,
            ("ENG", TextAndPointers("SHARED_KEY", 1)),
            ("FRE", TextAndPointers("SHARED_KEY", 1)));
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = await FindTextAsync(session, snapshot, "SHARED_KEY", languageIndex: 0);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);

            Assert.Single(result.PointerOccurrenceIdentities);
            Assert.Empty(result.Relationships);
            Assert.NotEmpty(result.Diagnostics);
            Assert.Equal(2, result.PointerCandidates.Count);
            var selectedCandidate = Assert.Single(result.PointerCandidates, candidate => candidate.IsSelectedStoredBlock);
            Assert.Equal(text.LanguageIndex, selectedCandidate.LanguageIndex);
            Assert.Equal(text.RawLanguageCode, selectedCandidate.RawLanguageCode);
            Assert.Contains(result.PointerCandidates, candidate => !candidate.IsSelectedStoredBlock &&
                candidate.LanguageIndex == 1 && candidate.RawLanguageCode == "FRE");
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task DuplicatePointersInOneStoredBlockRemainAmbiguous()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildAsync(fixture,
            ("ENG", TextAndPointers("DUPLICATE_KEY", 2)));
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = await FindTextAsync(session, snapshot, "DUPLICATE_KEY", languageIndex: 0);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);

            Assert.Empty(result.Relationships);
            Assert.Equal("MultipleSameKeyPointers", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(2, result.PointerOccurrenceIdentities.Count);
            Assert.Equal(2, result.PointerCandidates.Count);
            Assert.All(result.PointerCandidates, candidate =>
            {
                Assert.True(candidate.IsSelectedStoredBlock);
                Assert.Equal(0, candidate.LanguageIndex);
                Assert.Equal("ENG", candidate.RawLanguageCode);
            });
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task EqualRawCodesInDifferentBlocksDoNotAliasTheirIndices()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildAsync(fixture,
            ("ENG", TextAndPointers("SAME_CODE_KEY", 1)),
            ("ENG", TextAndPointers("SAME_CODE_KEY", 1)));
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = await FindTextAsync(session, snapshot, "SAME_CODE_KEY", languageIndex: 1);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);

            Assert.Single(result.PointerOccurrenceIdentities);
            Assert.Equal(2, result.PointerCandidates.Count);
            Assert.Contains(result.PointerCandidates, candidate => candidate.IsSelectedStoredBlock &&
                candidate.LanguageIndex == 1 && candidate.RawLanguageCode == "ENG");
            Assert.Contains(result.PointerCandidates, candidate => !candidate.IsSelectedStoredBlock &&
                candidate.LanguageIndex == 0 && candidate.RawLanguageCode == "ENG");
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task UnmatchedStoredContextReturnsEveryCandidateForAnExplicitChoice()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildAsync(fixture,
            ("ENG", [LocalizationFixture.DirectText("CONTEXT_KEY", "English")]),
            ("FRE", [LocalizationFixture.PointerRecord(("CONTEXT_KEY", 1, new byte[9]))]));
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = await FindTextAsync(session, snapshot, "CONTEXT_KEY", languageIndex: 0);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);

            Assert.Empty(result.Relationships);
            Assert.Empty(result.PointerOccurrenceIdentities);
            Assert.Equal("NoSameLanguagePointer", Assert.Single(result.Diagnostics).Code);
            var candidate = Assert.Single(result.PointerCandidates);
            Assert.False(candidate.IsSelectedStoredBlock);
            Assert.Equal(1, candidate.LanguageIndex);
            Assert.Equal("FRE", candidate.RawLanguageCode);
            Assert.Equal(candidate.Identity, Assert.Single(Assert.Single(result.Diagnostics).CandidatePointerOccurrences));
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task DirectPointerSelectionStillUsesThatOccurrence()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildAsync(fixture,
            ("ENG", TextAndPointers("DIRECT_KEY", 1)),
            ("FRE", TextAndPointers("DIRECT_KEY", 1)));
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var pointer = await FindPointerAsync(session, snapshot, "DIRECT_KEY", languageIndex: 1);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, pointer.Identity);

            Assert.Equal(pointer.Identity, Assert.Single(result.PointerOccurrenceIdentities));
            var candidate = Assert.Single(result.PointerCandidates);
            Assert.Equal(pointer.Identity, candidate.Identity);
            Assert.True(candidate.IsSelectedStoredBlock);
            Assert.Equal(pointer.LanguageIndex, candidate.LanguageIndex);
            Assert.Equal(pointer.RawLanguageCode, candidate.RawLanguageCode);
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task ExactKeyOccurrenceLimitStillReturnsBoundedContextCandidates()
    {
        using var fixture = new LocalizationFixture();
        var pointers = Enumerable.Range(0, LocalizationRelationshipCoordinator.MaximumExactKeyOccurrences + 1)
            .Select(_ => ("LIMITED_KEY", (byte)1, new byte[9])).ToArray();
        var pointerRecords = Enumerable.Range(0, 5)
            .Select(group => LocalizationFixture.PointerRecord(pointers
                .Skip(group * 64).Take(64).ToArray())).ToArray();
        var records = new[] { LocalizationFixture.DirectText("LIMITED_KEY", "line") }
            .Concat(pointerRecords).ToArray();
        var localized = LocalizationFixture.MakeLocalization(6, ("ENG", records));
        fixture.AddResource(LocalizationFixture.Wrap(localized, referenceCount: pointers.Length + 11));
        var snapshot = await fixture.BuildAsync();
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = await FindTextAsync(session, snapshot, "LIMITED_KEY", languageIndex: 0);

            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);

            Assert.Empty(result.Relationships);
            Assert.Equal("ExactKeyOccurrenceLimit", Assert.Single(result.Diagnostics).Code);
            Assert.InRange(result.PointerCandidates.Count, 1,
                LocalizationRelationshipCoordinator.MaximumExactKeyOccurrences + 1);
            Assert.All(result.PointerCandidates, candidate => Assert.Equal(0, candidate.LanguageIndex));
        }
        finally { DeleteCache(fixture); }
    }

    private static byte[][] TextAndPointers(string key, int pointerCount) =>
    [
        LocalizationFixture.DirectText(key, "stored text"),
        LocalizationFixture.PointerRecord(Enumerable.Range(0, pointerCount)
            .Select(_ => (key, (byte)1, new byte[9])).ToArray())
    ];

    private static async Task<ArchiveSnapshot> BuildAsync(LocalizationFixture fixture,
        params (string Language, byte[][] Records)[] languages)
    {
        var localized = LocalizationFixture.MakeLocalization(6, languages);
        var pointerCount = languages.SelectMany(language => language.Records)
            .Where(record => record.Length >= 8)
            .Sum(record => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4))));
        fixture.AddResource(LocalizationFixture.Wrap(localized, referenceCount: Math.Max(1, pointerCount)));
        return await fixture.BuildAsync();
    }

    private static async Task<LocalizationSession> OpenSessionAsync(ArchiveSnapshot snapshot,
        LocalizationFixture fixture)
    {
        var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
        await session.EnrichmentTask;
        return session;
    }

    private static async Task<LocalizationRecord> FindTextAsync(LocalizationSession session,
        ArchiveSnapshot snapshot, string key, int languageIndex)
    {
        var page = await session.QueryAsync(new LocalizationQuery(Source: new(snapshot.IndexContentId, 0),
            ExactKey: key, Kind: LocalizationRecordKind.Text, Limit: 10));
        return Assert.Single(page.Records, record => record.LanguageIndex == languageIndex);
    }

    private static async Task<LocalizationRecord> FindPointerAsync(LocalizationSession session,
        ArchiveSnapshot snapshot, string key, int languageIndex)
    {
        var page = await session.QueryAsync(new LocalizationQuery(Source: new(snapshot.IndexContentId, 0),
            ExactKey: key, Kind: LocalizationRecordKind.Pointer, Limit: 10));
        return Assert.Single(page.Records, record => record.LanguageIndex == languageIndex);
    }

    private static void DeleteCache(LocalizationFixture fixture)
    {
        if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
    }
}
