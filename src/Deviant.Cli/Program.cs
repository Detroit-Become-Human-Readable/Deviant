using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Archives;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export;
using System.Diagnostics;

namespace Deviant.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        using var timer = new Timer(_ => cancellation.Cancel(), null, Timeout.Infinite, Timeout.Infinite);
        Options? parsedOptions = null;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var options = parsedOptions = Options.Parse(args);
            if (options.CancelAfterMilliseconds is int delay) timer.Change(delay, Timeout.Infinite);
            return await RunAsync(options, cancellation.Token);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (ManifestWriteException ex)
        {
            var failure = new { error = "ManifestWriteFailed", message = "Outputs were retained, but the export manifest could not be finalized.", ex.ManifestPath, ex.Manifest };
            if (parsedOptions?.Json == true) Console.Error.WriteLine(JsonSerializer.Serialize(failure, JsonOptions));
            else
            {
                Console.Error.WriteLine($"Manifest finalization failed at {ex.ManifestPath}; completed outputs were retained.");
                foreach (var entry in ex.Manifest.Entries)
                    Console.Error.WriteLine($"{entry.Ordinal}: {entry.Status} {entry.RelativeOutput}");
            }
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunAsync(Options options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reader = new ArchiveReader();
        var snapshot = await reader.OpenAsync(options.Input!, new ConsoleProgress(), token);
        using var catalog = CatalogService.Open(snapshot, options.Cache, token);
        switch (options.Command)
        {
            case "list":
            {
                var query = options.Query();
                var rows = catalog.Query(query, token);
                Write(options, new { total = catalog.Count(query with { Offset = 0, Limit = 10_000 }), offset = query.Offset, limit = query.Limit, resources = rows },
                    () => PrintRows(rows));
                return rows.Any(r => r.Validity != ResourceValidity.Valid) ? 2 : 0;
            }
            case "inspect":
                return Inspect(options, snapshot, catalog, token);
            case "verify":
                return await VerifyAsync(options, snapshot, catalog, token);
            case "extract":
                return await ExtractAsync(options, snapshot, catalog, token);
            default:
                throw new UsageException("Command must be list, inspect, extract, or verify.");
        }
    }

    private static int Inspect(Options options, ArchiveSnapshot snapshot, IResourceCatalog catalog, CancellationToken token)
    {
        if (options.Ordinals.Count > 0)
        {
            var wanted = options.Ordinals.ToHashSet();
            var rows = QueryAll(catalog, options.Query() with { Limit = 10_000, Offset = 0 }, token)
                .Where(r => wanted.Contains(r.Ordinal)).ToArray();
            if (rows.Length != wanted.Count)
                throw new UsageException("One or more selected ordinals are not present in the current query result.");
            Write(options, new { resources = rows }, () => PrintRows(rows));
            return rows.Any(r => r.Validity != ResourceValidity.Valid) ? 2 : 0;
        }

        var report = new
        {
            index = Path.GetFileName(snapshot.IndexPath), snapshot.IndexContentId, snapshot.Version,
            snapshot.UnknownHeaderWord, snapshot.DeclaredCount, actualRecords = snapshot.Records.Count,
            volumes = snapshot.Volumes.Select(v => new { v.Package, v.RelativeName, exists = v.Stamp.Exists, v.Stamp.Length, v.Stamp.Reliable, v.Stamp.Error }),
            diagnostics = snapshot.Diagnostics,
            catalog = new { catalog.CacheKey, catalog.ReusedIndexRows, count = catalog.Count(new ResourceQuery(Limit: 10_000)) }
        };
        Write(options, report, () =>
        {
            Console.WriteLine($"Index: {Path.GetFileName(snapshot.IndexPath)}");
            Console.WriteLine($"Content ID: {snapshot.IndexContentId}");
            Console.WriteLine($"Version: {snapshot.Version}; declared records: {snapshot.DeclaredCount}; parsed records: {snapshot.Records.Count}");
            foreach (var volume in snapshot.Volumes)
                Console.WriteLine($"Volume {volume.Package}: {volume.RelativeName}; exists={volume.Stamp.Exists}; length={volume.Stamp.Length}; reliable={volume.Stamp.Reliable}");
            foreach (var diagnostic in snapshot.Diagnostics) Console.WriteLine($"Diagnostic {diagnostic.Code}: {diagnostic.Message}");
        });
        return snapshot.Diagnostics.Count > 0 || snapshot.Records.Any(r => r.Validity != ResourceValidity.Valid) ? 2 : 0;
    }

    private static async Task<int> VerifyAsync(Options options, ArchiveSnapshot snapshot, IResourceCatalog catalog, CancellationToken token)
    {
        var rows = QueryAll(catalog, new ResourceQuery(Limit: 10_000), token);
        var invalid = rows.Count(r => r.Validity != ResourceValidity.Valid);
        if (options.FullContent)
        {
            var verified = await ArchiveVerifier.VerifyContentAsync(snapshot, new ConsoleProgress(), token);
            Write(options, new { mode = "full-content", result = verified, invalidResources = invalid }, () =>
            {
                Console.WriteLine($"Full content verification completed for {verified.Files.Count} files.");
                Console.WriteLine($"Index content ID: {verified.IndexContentId}");
                Console.WriteLine($"Archive content ID: {verified.ArchiveContentId}");
            });
        }
        else
        {
            var bounds = new
            {
                mode = "bounds-summary", total = rows.Count, valid = rows.Count - invalid, invalid,
                diagnostics = snapshot.Diagnostics, volumes = snapshot.Volumes.Select(v => new { v.Package, v.RelativeName, exists = v.Stamp.Exists, v.Stamp.Length })
            };
            Write(options, bounds, () => Console.WriteLine($"Bounds summary: {rows.Count - invalid}/{rows.Count} records valid; {snapshot.Diagnostics.Count} diagnostics."));
        }
        return invalid > 0 || snapshot.Diagnostics.Count > 0 ? 2 : 0;
    }

    private static async Task<int> ExtractAsync(Options options, ArchiveSnapshot snapshot, IResourceCatalog catalog, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(options.Output)) throw new UsageException("extract requires --output <directory>.");
        var hasFilters = options.Type is not null || options.Id is not null || options.Package is not null ||
                         !string.IsNullOrWhiteSpace(options.Search) || options.Ordinals.Count > 0;
        if (!options.All && !hasFilters) throw new UsageException("extract requires --all or at least one selection filter.");
        var query = options.Query() with { Offset = options.Ordinals.Count > 0 || options.All ? 0 : options.Offset,
            Limit = options.All || options.Ordinals.Count > 0 ? 10_000 : options.Limit };
        var selected = options.All || options.Ordinals.Count > 0
            ? QueryAll(catalog, query with { Offset = 0, Limit = 10_000 }, token)
            : catalog.Query(query, token);
        if (options.Ordinals.Count > 0)
        {
            var ordinals = options.Ordinals.ToHashSet();
            selected = selected.Where(r => ordinals.Contains(r.Ordinal)).ToArray();
            if (selected.Count != ordinals.Count) throw new UsageException("One or more selected ordinals are not present in the current filtered result.");
        }
        if (selected.Count == 0) throw new UsageException("The selection contains no resources to extract.");
        OutputSafety.ValidateOutputRoot(options.Output, snapshot.SourceRoot);
        var service = new RawExportService();
        var plan = service.Plan(snapshot, new ExportOptions(options.Output, options.Overwrite), selected);
        Console.Error.WriteLine($"Exporting {plan.Items.Count} resources...");
        var report = await service.RunAsync(plan, new ExportConsoleProgress(), token);
        Write(options, new { report.Manifest, report.ManifestPath }, () =>
        {
            Console.WriteLine($"Export manifest: {report.ManifestPath}");
            foreach (var entry in report.Manifest.Entries)
                Console.WriteLine($"{entry.Ordinal}: {entry.Status} {entry.RelativeOutput}{(entry.ErrorCode is null ? "" : " (" + entry.ErrorCode + ")")}");
        });
        return ExportExitCode(report.Manifest);
    }

    public static int ExportExitCode(ExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Cancelled) return 130;
        return manifest.Entries.Any(e => e.Status is ExportStatus.Failed or ExportStatus.Cancelled) ? 2 : 0;
    }

    private static IReadOnlyList<ResourceRecord> QueryAll(IResourceCatalog catalog, ResourceQuery query, CancellationToken token)
    {
        var count = catalog.Count(query with { Limit = 10_000, Offset = 0 });
        var result = new List<ResourceRecord>(checked((int)Math.Min(count, int.MaxValue)));
        for (long offset = 0; offset < count; offset += 10_000)
        {
            token.ThrowIfCancellationRequested();
            result.AddRange(catalog.Query(query with { Limit = 10_000, Offset = offset }, token));
        }
        return result;
    }

    private static void Write<T>(Options options, T value, Action text)
    {
        if (options.Json) Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        else text();
    }

    private static void PrintRows(IEnumerable<ResourceRecord> rows)
    {
        foreach (var row in rows)
            Console.WriteLine($"{row.Ordinal}\ttype=0x{row.Type:X8}\tflags=0x{row.Flags:X8}\tid=0x{row.Id:X8} ({row.Id})\tpackage={row.Package}\toffset={row.Offset}\tstored={row.StoredSize}\talt={row.AlternativeSize}\t{row.VolumeName}\t{row.Validity}");
    }

    private sealed class ConsoleProgress : IProgress<ArchiveProgress>
    {
        private readonly object gate = new();
        private readonly Dictionary<string, (int Percent, long Timestamp)> states = new(StringComparer.Ordinal);
        public void Report(ArchiveProgress value)
        {
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                var percent = value.Total <= 0 ? 100 : (int)Math.Clamp((double)value.Completed * 100d / value.Total, 0, 100);
                if (!states.TryGetValue(value.Phase, out var previous))
                {
                    Console.Error.WriteLine($"{value.Phase}...");
                    states[value.Phase] = (percent, now);
                    return;
                }
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalMilliseconds;
                if (percent < 100 && (percent < previous.Percent + 5 || elapsedMilliseconds < 1000)) return;
                Console.Error.WriteLine($"{value.Phase}: {percent}% ({value.Completed}/{value.Total})");
                states[value.Phase] = (percent, now);
            }
        }
    }

    private sealed class ExportConsoleProgress : IProgress<ExportProgress>
    {
        private readonly object gate = new();
        private int lastPercent;
        private long lastTimestamp = Stopwatch.GetTimestamp();

        public void Report(ExportProgress value)
        {
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                var total = Math.Max(1, value.TotalFiles);
                var fraction = value.Length <= 0 ? 1d : Math.Clamp((double)value.BytesWritten / value.Length, 0d, 1d);
                var position = Math.Clamp((value.CompletedFiles + fraction) / total, 0d, 1d);
                var percent = (int)(position * 100d);
                var complete = value.TotalFiles > 0 && value.CompletedFiles >= value.TotalFiles;
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalMilliseconds;
                if (!complete && (percent < lastPercent + 5 || elapsedMilliseconds < 1000)) return;
                Console.Error.WriteLine($"Export: {value.CompletedFiles}/{value.TotalFiles} files; resource {value.Ordinal}, {value.BytesWritten}/{value.Length} bytes ({percent}%).");
                lastPercent = percent;
                lastTimestamp = now;
            }
        }
    }

    public sealed class Options
    {
        public required string Command { get; init; }
        public string? Input { get; private set; }
        public string? Cache { get; private set; }
        public bool Json { get; private set; }
        public uint? Type { get; private set; }
        public uint? Id { get; private set; }
        public uint? Package { get; private set; }
        public string? Search { get; private set; }
        public long Offset { get; private set; }
        public int Limit { get; private set; } = 100;
        public string? Output { get; private set; }
        public bool All { get; private set; }
        public bool Overwrite { get; private set; }
        public bool FullContent { get; private set; }
        public int? CancelAfterMilliseconds { get; private set; }
        public List<long> Ordinals { get; } = [];

        public ResourceQuery Query() => new(Type, Id, Package, Search, Offset, Limit);

        public static string Usage => "Usage: deviant <list|inspect|extract|verify> --input <index> [--cache <dir>] [--json] [--type <uint>] [--id <uint>] [--package <uint>] [--search <text>] [--offset <n>] [--limit <1..10000>] [--ordinal <n[,n...]>] [--all] [--output <dir>] [--overwrite] [--full-content] [--cancel-after <ms>]";

        public static Options Parse(string[] args)
        {
            if (args.Length == 0) throw new UsageException("A command is required.");
            var command = args[0].ToLowerInvariant();
            if (command is not ("list" or "inspect" or "extract" or "verify")) throw new UsageException("Unknown command: " + args[0]);
            var result = new Options { Command = command };
            for (var i = 1; i < args.Length; i++)
            {
                var option = args[i];
                string Value()
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{option} requires a value.");
                    return args[i];
                }
                switch (option)
                {
                    case "--input": result.Input = Value(); break;
                    case "--cache": result.Cache = Value(); break;
                    case "--json": result.Json = true; break;
                    case "--type": result.Type = ParseUInt(Value(), option); break;
                    case "--id": result.Id = ParseUInt(Value(), option); break;
                    case "--package": result.Package = ParseUInt(Value(), option); break;
                    case "--search": result.Search = Value(); break;
                    case "--offset": result.Offset = ParseLong(Value(), option); if (result.Offset < 0) throw new UsageException("--offset must be zero or greater."); break;
                    case "--limit": result.Limit = ParseBoundedInt(Value(), option, 1, 10_000); break;
                    case "--output": result.Output = Value(); break;
                    case "--ordinal":
                        foreach (var entry in Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var ordinal = ParseLong(entry, option);
                            if (ordinal < 0) throw new UsageException("Ordinals must be zero or greater.");
                            result.Ordinals.Add(ordinal);
                        }
                        break;
                    case "--all": result.All = true; break;
                    case "--overwrite": result.Overwrite = true; break;
                    case "--full-content": result.FullContent = true; break;
                    case "--cancel-after": result.CancelAfterMilliseconds = ParseBoundedInt(Value(), option, 0, int.MaxValue); break;
                    default: throw new UsageException("Unknown option: " + option);
                }
            }
            if (string.IsNullOrWhiteSpace(result.Input)) throw new UsageException("--input is required.");
            if (result.Ordinals.Distinct().Count() != result.Ordinals.Count) throw new UsageException("An ordinal was selected more than once.");
            if (command != "extract" && (result.Output is not null || result.Overwrite || result.All)) throw new UsageException("--output, --overwrite, and --all apply only to extract.");
            if (command == "extract" && string.IsNullOrWhiteSpace(result.Output)) throw new UsageException("extract requires --output <directory>.");
            if (command == "extract" && !result.All && result.Type is null && result.Id is null && result.Package is null &&
                string.IsNullOrWhiteSpace(result.Search) && result.Ordinals.Count == 0)
                throw new UsageException("extract requires --all or at least one selection filter.");
            if (command != "verify" && result.FullContent) throw new UsageException("--full-content applies only to verify.");
            return result;
        }

        private static uint ParseUInt(string text, string option)
        {
            var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : (uint?)null
                : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue : (uint?)null;
            return parsed ?? throw new UsageException($"{option} must be an unsigned 32-bit decimal or 0x-prefixed hexadecimal value.");
        }

        private static long ParseLong(string text, string option) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new UsageException($"{option} must be an integer.");

        private static int ParseBoundedInt(string text, string option, int minimum, int maximum)
        {
            var value = ParseLong(text, option);
            if (value < minimum || value > maximum) throw new UsageException($"{option} must be between {minimum} and {maximum}.");
            return (int)value;
        }
    }

    private sealed class UsageException(string message) : Exception(message);
}
