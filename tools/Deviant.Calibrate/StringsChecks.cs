using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Browser;
using Deviant.Export.Localization;
using Deviant.Formats.Localization.Model;
namespace Deviant.Calibrate;
public static class StringsChecks
{
 public static async Task RunAsync(string input,string output)
 {
  Directory.CreateDirectory(output);var timer=Stopwatch.StartNew();var snapshot=await new ArchiveReader().OpenAsync(input);
  var indexMilliseconds=timer.Elapsed.TotalMilliseconds;
  var progress=new Progress<LocalizationReadProgress>(value=>Console.WriteLine($"{value.Phase}: {value.Completed}/{value.Total} sources · {value.Records} records · {timer.Elapsed.TotalSeconds:F1}s"));
  await using var session=await LocalizationSession.OpenAsync(snapshot,Path.Combine(output,"cache"),progress);await session.EnrichmentTask;
  var load=timer.Elapsed.TotalMilliseconds;var page=await session.QueryAsync(new(Kind:LocalizationRecordKind.Text,Limit:100));
  if(page.Records.Count==0)
  {
   var diagnosticPage=await session.QueryAsync(new(Limit:1000));
   await File.WriteAllTextAsync(Path.Combine(output,"diagnostics.json"),JsonSerializer.Serialize(diagnosticPage.Records.Select(r=>new{r.ResourceOrdinal,r.Version,r.Kind,r.Status,r.Diagnostic,r.UnknownFields,r.RecordExtent}),new JsonSerializerOptions{WriteIndented=true}));
   throw new InvalidOperationException("No readable localization rows were found.");
  }
  foreach(var record in page.Records.Take(20))
  {
   var extent=record.FieldExtents.First(e=>e.Field=="Text");var volume=snapshot.Volumes.Single(v=>v.RelativeName==extent.RelativeSource);
   using var handle=File.OpenHandle(volume.FilePath);var bytes=new byte[checked((int)extent.Length)];var count=0;
   while(count<bytes.Length){var n=RandomAccess.Read(handle,bytes.AsSpan(count),extent.Offset+count);if(n==0)throw new EndOfStreamException();count+=n;}
   if(new UnicodeEncoding(false,false,true).GetString(bytes)!=record.Text)throw new InvalidOperationException("Decoded text does not match its source extent.");
  }
  var results=new List<object>();var selected=LocalizationExportSelection.Selected(page.Records.Take(20).Select(r=>r.Identity));
  foreach(var format in Enum.GetValues<LocalizationExportFormat>())
  {
   var report=await session.ExportAsync(selected,format,Path.Combine(output,"strings."+(format==LocalizationExportFormat.Json?"json":"csv")));
   if(report.Manifest.CompletedCount!=20||report.Manifest.FailedCount!=0)throw new InvalidOperationException("Strings export failed.");
   results.Add(new{Format=format.ToString(),report.Manifest.CompletedCount,report.Manifest.DataSha256});
  }
  var samples=new List<double>();for(var i=0;i<20;i++){timer.Restart();await session.QueryAsync(new(Search:page.Records[i%page.Records.Count].Key,Limit:100));samples.Add(timer.Elapsed.TotalMilliseconds);}
  await File.WriteAllTextAsync(Path.Combine(output,"strings-check.json"),JsonSerializer.Serialize(new{session.RecordCount,Resources=session.Resources.Count,Languages=session.Languages,session.Summary,
   IndexMilliseconds=indexMilliseconds,LoadMilliseconds=load,CacheBytes=Directory.EnumerateFiles(Path.Combine(output,"cache"),"*",SearchOption.AllDirectories).Sum(path=>new FileInfo(path).Length),SourceExtentChecks=20,QueryP95Milliseconds=samples.Order().ElementAt(18),Exports=results},new JsonSerializerOptions{WriteIndented=true}));
 }
}
