using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Audio;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;

var options = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++) if (args[i].StartsWith("--", StringComparison.Ordinal))
    options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
if (!options.TryGetValue("input", out var input) || !options.TryGetValue("out", out var output))
{ Console.Error.WriteLine("Deviant.Calibrate --input <index> --out <private results directory> [--tools <directory>] [--profile <name>] [--convert]"); return 1; }
output = Path.GetFullPath(output); Directory.CreateDirectory(output);
var timer = Stopwatch.StartNew(); var metrics = new Dictionary<string, object>();
long peak = 0; using var sample = new Timer(_ => { using var process = Process.GetCurrentProcess(); InterlockedExtensions.Max(ref peak, process.WorkingSet64); }, null, 0, 25);
try
{
    var watch = Stopwatch.StartNew(); var snapshot = await new ArchiveReader().OpenAsync(input);
    metrics["indexMilliseconds"] = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var result = await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot, new Progress<AudioReadProgress>(p => Console.WriteLine($"{p.Phase}: {p.Completed}/{p.Total}")));
    var catalog = result.Catalog; metrics["coldAudioMetadataMilliseconds"] = watch.Elapsed.TotalMilliseconds;
    metrics["records"] = snapshot.Records.Count; metrics["volumes"] = snapshot.Volumes.Count;
    metrics["banks"] = catalog.Banks.Count; metrics["media"] = catalog.Media.Count;
    metrics["completeness"] = catalog.Media.GroupBy(m => m.Completeness.ToString()).ToDictionary(g => g.Key, g => g.Count());
    metrics["availability"] = catalog.Media.GroupBy(m => m.Availability.ToString()).ToDictionary(g => g.Key, g => g.Count());
    metrics["languages"] = catalog.Media.GroupBy(m => m.Language).ToDictionary(g => g.Key, g => g.Count());
    using var store = FeatureStore.Open(snapshot, Path.Combine(output, "cache"));
    watch.Restart();
    store.SaveItems("banks", "1", catalog.Banks); store.SaveItems("media", "1", catalog.Media);
    store.SaveItems("events", "1", catalog.Events); store.SaveItems("names", "1", catalog.Names);
    store.SaveItems("localization", "1", catalog.Localization); store.SaveItems("declarations", "1", catalog.SourceDeclarations);
    store.SaveItems("diagnostics", "1", catalog.Diagnostics); store.Checkpoint();
    metrics["metadataWriteMilliseconds"] = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var warmCount = store.LoadItems<BankInfo>("banks", "1").Count + store.LoadItems<MediaEntry>("media", "1").Count;
    metrics["warmAudioMetadataMilliseconds"] = watch.Elapsed.TotalMilliseconds; metrics["warmRecords"] = warmCount;
    metrics["metadataBytes"] = Directory.EnumerateFiles(Path.Combine(output,"cache")).Sum(p => new FileInfo(p).Length);
    metrics["unnamedPlayable"] = catalog.Media.Count(m => MediaPolicy.CanPreview(m) && !m.Names.Any(n => n.Kind is NameKind.Stored or NameKind.Verified));
    if (options.ContainsKey("convert"))
    {
        var service = new AudioService(AudioToolPaths.Discover(options.GetValueOrDefault("tools")));
        var candidates = catalog.Media.Where(MediaPolicy.CanConvert).Where(m => m.Slice.Length <= 64 * 1024 * 1024 && m.Duration is > 0 and <= 120).Take(12).ToArray();
        var trials = new List<object>();
        foreach (var workers in new[] { 1, 2, 4 })
        {
            var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            watch.Restart();
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (media, token) =>
            {
                var elapsed = Stopwatch.StartNew();
                var destination = Path.Combine(output, $"trial-{workers}-{Array.IndexOf(candidates, media)}.wav");
                await service.DecodeToWavAsync(catalog, media, destination, WavFormat.Pcm16, cancellationToken: token);
                latencies.Add(elapsed.Elapsed.TotalMilliseconds);
            });
            trials.Add(new { Workers = workers, Samples = candidates.Length, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, Latency = Percentiles(latencies) });
        }
        metrics["conversionTrials"] = trials;
        var selections = new[] {
            catalog.Media.Where(MediaPolicy.CanConvert).Where(m => m.Duration is > 120 and <= 1200).OrderByDescending(m => m.Duration).FirstOrDefault(),
            catalog.Media.Where(MediaPolicy.CanConvert).FirstOrDefault(m => m.Completeness == MediaCompleteness.Unverified),
            catalog.Media.FirstOrDefault(m => MediaPolicy.CanPreview(m) && m.Completeness == MediaCompleteness.Fragment)
        };
        var special = new List<object>();
        for (var i = 0; i < selections.Length; i++) if (selections[i] is { } media)
        {
            var completeness = media.Completeness; watch.Restart();
            var decoded = await service.DecodeToWavAsync(catalog, media, Path.Combine(output,$"representative-{i}.wav"), WavFormat.Pcm16,
                purpose: completeness == MediaCompleteness.Fragment ? DecodePurpose.PartialPreview : DecodePurpose.Conversion);
            special.Add(new { media.Identity, Completeness = completeness.ToString(), Retained = media.Completeness == completeness, decoded.Channels, decoded.SampleRate, decoded.SampleFrames, Milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        metrics["representativeDecodes"] = special;
    }
    metrics["profile"] = options.GetValueOrDefault("profile") ?? "Current workstation";
    metrics["runtime"] = RuntimeInformation.FrameworkDescription; metrics["os"] = RuntimeInformation.OSDescription;
    metrics["logicalProcessors"] = Environment.ProcessorCount; metrics["peakWorkingSetBytes"] = peak;
    metrics["totalMilliseconds"] = timer.Elapsed.TotalMilliseconds;
    await File.WriteAllTextAsync(Path.Combine(output,"calibration.json"), JsonSerializer.Serialize(metrics,new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Calibration saved."); return 0;
}
catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(output,"error.txt"),error.ToString()); Console.Error.WriteLine(error); return 2; }

static object Percentiles(IEnumerable<double> values)
{ var sorted = values.Order().ToArray(); return sorted.Length == 0 ? new { P50 = 0d, P95 = 0d, Max = 0d } : new { P50 = sorted[(int)((sorted.Length-1)*.5)], P95 = sorted[(int)Math.Ceiling((sorted.Length-1)*.95)], Max = sorted[^1] }; }
static class InterlockedExtensions
{ public static void Max(ref long target,long value) { long old; do { old=Interlocked.Read(ref target); if (old>=value) return; } while (Interlocked.CompareExchange(ref target,value,old)!=old); } }
