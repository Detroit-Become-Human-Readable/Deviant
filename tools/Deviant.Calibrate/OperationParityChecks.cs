using System.Diagnostics;
using System.Text.Json;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Audio;
using Deviant.Formats.Audio.Model;
namespace Deviant.Calibrate;

public static class OperationParityChecks
{
 public static async Task RunAsync(string input,string output,string? toolsDirectory,long? ordinal,uint? mediaId)
 {
  Directory.CreateDirectory(output);var cache=new CacheManager(Path.Combine(output,"settings"),new(Path.Combine(output,"cache")));
  var aliases=new AliasStore(Path.Combine(output,"library"));var clock=Stopwatch.StartNew();
  await using var session=await AudioExplorerSession.OpenStagedAsync(input,cache,aliases,AudioToolPaths.Discover(toolsDirectory));
  var indexTime=clock.Elapsed.TotalMilliseconds;await session.EnrichmentTask;var completeTime=clock.Elapsed.TotalMilliseconds;
  var external=session.Catalog.Media.Where(m=>m.State==MediaState.CompleteExternal).ToArray();
  var matches=external.Where(m=>(ordinal is null||m.Identity?.Ordinal==ordinal)&&(mediaId is null||m.Id==mediaId)).Take(ordinal is null&&mediaId is null?3:int.MaxValue).ToArray();
  if(matches.Length==0)throw new InvalidOperationException("No matching resolved external media was found.");
  var rows=session.ResolveSelectionKeys(matches.Select(m=>m.Identity!.Key));var results=new List<object>();
  foreach(var row in rows)
  {
   await using var preview=await session.PreviewAsync(row.Identity);
   var formats=new List<object>();
   foreach(var format in Enum.GetValues<AudioExportFormat>())
   {
    var report=await session.ExportAsync(session.Review([row]),new(Path.Combine(output,"exports",format.ToString()),format,Overwrite:true));
    var entry=report.Manifest.Entries.Single();if(entry.Status!=AudioExportStatus.Completed)throw new InvalidOperationException($"{format}: {entry.ErrorCode} {entry.Diagnostic}");
    formats.Add(new {Format=format.ToString(),entry.Status,entry.Sha256,entry.OutputLength,entry.Completeness});
   }
   results.Add(new {row.Identity,row.Media.Id,row.Media.PayloadIdentity,PreviewBytes=new FileInfo(preview.Path).Length,Formats=formats});
  }
  await File.WriteAllTextAsync(Path.Combine(output,"operation-parity.json"),JsonSerializer.Serialize(new {IndexReadyMilliseconds=indexTime,MetadataReadyMilliseconds=completeTime,ExternalCount=external.Length,Selected=results},new JsonSerializerOptions {WriteIndented=true}));
 }
}
