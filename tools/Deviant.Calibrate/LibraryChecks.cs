using System.Diagnostics;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Audio;
using Deviant.Formats.Audio.Model;

namespace Deviant.Calibrate;
public static class LibraryChecks
{
    public static async Task RunAsync(string input, string output, string? toolsDirectory)
    {
        Directory.CreateDirectory(output);
        var cache = new CacheManager(Path.Combine(output,"settings"), new(Path.Combine(output,"cache"), 4L * 1024 * 1024 * 1024));
        var aliases = new AliasStore(Path.Combine(output,"library"));
        var tools = AudioToolPaths.Discover(toolsDirectory);
        var watch = Stopwatch.StartNew();
        var timings = new Dictionary<string, object>();
        var session = await AudioExplorerSession.OpenAsync(input, cache, aliases, tools);
        timings["metadataWasWarm"] = session.EnrichmentTask.IsCompleted;
        timings["indexReadyMilliseconds"] = watch.Elapsed.TotalMilliseconds;
        var updates = 0; session.Updated += (_, _) => Interlocked.Increment(ref updates);
        while (!session.EnrichmentTask.IsCompleted)
        {
            await session.QueryAsync(new AudioExplorerQuery(Search: "clip", Limit: 20));
            await Task.Delay(100);
        }
        await session.EnrichmentTask; timings["libraryReadyMilliseconds"] = watch.Elapsed.TotalMilliseconds;
        timings["incrementalUpdates"] = updates;
        var queries = new List<double>();
        for (var i=0; i<30; i++)
        {
            watch.Restart();
            await session.QueryAsync((i%3) switch {
                0 => new AudioExplorerQuery(Language:"ENG",Limit:100),
                1 => new AudioExplorerQuery(BankKey:session.Banks.First().Key,Limit:100),
                _ => new AudioExplorerQuery(Search:"clip",Limit:100) });
            queries.Add(watch.Elapsed.TotalMilliseconds);
        }
        var sorted=queries.Order().ToArray(); timings["warmPageP50Milliseconds"]=sorted[14]; timings["warmPageP95Milliseconds"]=sorted[28]; timings["warmPageMaxMilliseconds"]=sorted[^1];
        var page = await session.QueryAsync(new AudioExplorerQuery(Search:"Vorbis clip",Limit:1000));
        await File.WriteAllTextAsync(Path.Combine(output,"selection-debug.json"),JsonSerializer.Serialize(new { page.Total, Rows=page.Rows.Take(10).Select(r=>new {r.Label,r.CanPreview,r.CanConvert,r.Descriptor.Capabilities,r.Media.State,r.Media.Completeness,r.Media.ContainerValidity,r.Media.Availability,r.Media.IsRiff,Names=r.Media.Names.Select(n=>n.Kind.ToString()).ToArray()}) }));
        var chosen = page.Rows.First(r=>r.CanPreview && r.CanConvert && !r.Media.Names.Any(n=>n.Kind is NameKind.Stored or NameKind.Verified));
        await using (var preview = await session.PreviewAsync(chosen.Identity)) { timings["unnamedPreviewBytes"]=new FileInfo(preview.Path).Length; }
        const string alias="Library audition 街";
        await session.SetAliasAsync(chosen.Identity, alias);
        if (!(await session.QueryAsync(new AudioExplorerQuery(Search:alias))).Rows.Any(r=>r.Identity==chosen.Identity)) throw new InvalidOperationException("Alias search failed.");
        var selection=page.Rows.Where(r=>r.CanConvert).Take(3).Select(r=>new AudioExportSource(r.Identity,r.Media)).ToArray();
        var review=session.Review(page.Rows.Where(r=>r.CanConvert).Take(3).ToArray());
        var reports=new List<object>();
        foreach (var format in new[] { AudioExportFormat.Original,AudioExportFormat.Wav,AudioExportFormat.Ogg })
        {
            var report=await session.ExportAsync(review,new(Path.Combine(output,"exports",format.ToString()),format,Overwrite:true));
            if (report.Manifest.Entries.Any(e=>e.Status!=AudioExportStatus.Completed)) throw new InvalidOperationException($"{format} export failed: "+string.Join(";",report.Manifest.Entries.Select(e=>e.Diagnostic)));
            foreach (var entry in report.Manifest.Entries)
            {
                var path=Path.Combine(output,"exports",format.ToString(),entry.RelativeOutput);
                await using var file=File.OpenRead(path);
                var hash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file)).ToLowerInvariant();
                if (entry.Sha256!=hash) throw new InvalidOperationException("Export hash mismatch.");
            }
            reports.Add(new { Format=format.ToString(), Completed=report.Manifest.Entries.Count, Manifest=Path.GetFileName(report.ManifestPath) });
        }
        timings["exports"]=reports;
        await session.DisposeAsync();
        watch.Restart(); session=await AudioExplorerSession.OpenAsync(input,cache,new AliasStore(Path.Combine(output,"library")),tools); await session.EnrichmentTask;
        timings["warmLibraryOpenMilliseconds"]=watch.Elapsed.TotalMilliseconds;
        if (!(await session.QueryAsync(new AudioExplorerQuery(Search:alias))).Rows.Any(r=>r.Identity==chosen.Identity)) throw new InvalidOperationException("Alias restart failed.");
        await session.DisposeAsync();
        await cache.ClearAsync();
        if (new AliasStore(Path.Combine(output,"library")).Get(chosen.Identity)!=alias) throw new InvalidOperationException("Cache clear removed alias.");
        await cache.RelocateAsync(Path.Combine(output,"relocated-cache"));
        if (new AliasStore(Path.Combine(output,"library")).Get(chosen.Identity)!=alias) throw new InvalidOperationException("Cache relocation removed alias.");
        timings["aliasRestartClearRelocationPassed"]=true;
        await File.WriteAllTextAsync(Path.Combine(output,"library-check.json"),JsonSerializer.Serialize(timings,new JsonSerializerOptions { WriteIndented=true }));
    }
}
