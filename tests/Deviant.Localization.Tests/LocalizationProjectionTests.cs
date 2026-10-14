using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization.Model;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationProjectionTests
{
    [Fact]
    public async Task PagesAndExportsUseFullTypedRecordsWithoutDescriptorDeserialization()
    {
        using var fixture=new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG",Enumerable.Range(1,25).Select(index=>LocalizationFixture.DirectText("KEY_"+index,"街の声 "+index)).ToArray()),
            ("FRE",[LocalizationFixture.DirectText("KEY_2","Voix française")]))));
        var snapshot=await fixture.BuildAsync();
        await using var session=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);await session.EnrichmentTask;
        var query=new LocalizationQuery(Language:"ENG",Sort:AssetSort.Name,Descending:true,Offset:5,Limit:4);
        var before=await session.QueryAsync(query);
        var cacheKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion+"\n"+SourceIdentity.IndexCacheKey(snapshot.IndexContentId)))).ToLowerInvariant();
        var revision=SourceIdentity.DerivedCacheKey(snapshot.IndexContentId,snapshot.PhysicalSourceStamp);
        using(var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(fixture.CachePath,"catalog-"+cacheKey+".sqlite3"),Pooling=false}.ToString()))
        {
            database.Open();using var command=database.CreateCommand();command.CommandText="UPDATE localization_generation_rows SET payload='not-json' WHERE revision=$revision AND collection='search-assets' AND generation=(SELECT generation FROM localization_generation_active WHERE revision=$revision AND record_store_version=$version)";
            command.Parameters.AddWithValue("$revision",revision);command.Parameters.AddWithValue("$version",LocalizationCacheContract.RecordStoreVersion);
            Assert.Equal(26,command.ExecuteNonQuery());
        }
        var after=await session.QueryAsync(query);
        Assert.Equal(before.Total,after.Total);
        Assert.Equal(JsonSerializer.Serialize(before.Records),JsonSerializer.Serialize(after.Records));
        var variants=await session.GetVariantsAsync((await session.QueryAsync(new(ExactKey:"KEY_2",Language:"ENG"))).Records.Single().Identity);
        Assert.Equal(2,variants.Count);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.QueryAsync(query,cancelled.Token));
        Assert.Equal(after.Records.Select(row=>row.Identity),(await session.QueryAsync(query)).Records.Select(row=>row.Identity));
        var output=Path.Combine(fixture.CachePath,"exports","selected.json");
        var report=await session.ExportAsync(LocalizationExportSelection.Selected(after.Records.Select(row=>row.Identity)),LocalizationExportFormat.Json,output);
        Assert.Equal(4,report.Manifest.CompletedCount);
        Assert.Contains("街の声",await File.ReadAllTextAsync(output,Encoding.UTF8));
    }
}
