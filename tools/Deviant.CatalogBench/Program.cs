using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        throw new ArgumentException("Options require a name and value.");
    options.Add(args[i][2..], args[++i]);
}
if (!options.TryGetValue("input", out var input) || !options.TryGetValue("cache", out var cache) ||
    !options.TryGetValue("out", out var output) || !options.TryGetValue("family", out var family))
{
    Console.Error.WriteLine("Deviant.CatalogBench --input <index> --cache <existing cache> --out <results directory> --family <family> [--repeats <count>] [--footprint true|false] [--only <scenario prefix>] [--projection compact|payload] [--timeout-ms <milliseconds>]");
    return 1;
}
var repeats = int.Parse(options.GetValueOrDefault("repeats", "3"), System.Globalization.CultureInfo.InvariantCulture);
if (repeats is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repeats));
var projection = options.GetValueOrDefault("projection", "compact");
if (projection is not "compact" and not "payload") throw new ArgumentException("Unknown paging projection.");
var timeoutMs = int.Parse(options.GetValueOrDefault("timeout-ms", "15000"), System.Globalization.CultureInfo.InvariantCulture);
if (timeoutMs is < 25 or > 90000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
output = Path.GetFullPath(output);
var timer = Stopwatch.StartNew();
var snapshot = await new ArchiveReader().OpenAsync(input);
var sourceOpenMs = timer.Elapsed.TotalMilliseconds;
if (DirectoryBoundary.IsWithin(output, snapshot.SourceRoot))
    throw new ArgumentException("The results directory must be outside the archive directory.");
Directory.CreateDirectory(output);
var database = File.Exists(cache) ? Path.GetFullPath(cache) : Directory.GetFiles(cache, "catalog-*.sqlite3").Single();
using var connection = Open(database);
connection.CreateFunction<string, string, bool>("deviant_contains", static (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), true);
connection.CreateFunction<string,string>("deviant_natural", NaturalOrder.Key, true);
connection.CreateFunction<string,long>("deviant_ordinal", IdentityOrdinal, true);
connection.CreateFunction<string,string>("deviant_member", IdentityMember, true);
var hasNameIndex = ScalarLong("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='index' AND name='assets_v2_name_v1')") != 0;
var revision = ScalarString("SELECT revision FROM assets_v2 WHERE family=$family LIMIT 1", ("$family", family));
var matchingSource = ScalarLong("SELECT COUNT(*) FROM assets_v2 WHERE revision=$revision AND family=$family AND identity LIKE $identity",
    ("$revision", revision), ("$family", family), ("$identity", snapshot.IndexContentId + ":%"));
var total = ScalarLong("SELECT COUNT(*) FROM assets_v2 WHERE revision=$revision AND family=$family", ("$revision", revision), ("$family", family));
if (total == 0 || matchingSource != total) throw new InvalidOperationException("The family cache does not match the supplied index.");
bool hasLookup;
using (var columns = Command("PRAGMA table_info(assets_v2)"))
using (var reader = columns.ExecuteReader())
{
    hasLookup = false;
    while (reader.Read()) hasLookup |= reader.GetString(1) == "lookup_key";
}
var samples = new List<(string Label, string Search, string Language, string Bank, string? LookupKey)>();
for (var i = 0; i < 3; i++)
{
    using var command = Command($"SELECT label,search,COALESCE(language,''),COALESCE(bank,''),{(hasLookup ? "lookup_key" : "NULL")} FROM assets_v2 WHERE revision=$revision AND family=$family ORDER BY sort_key,identity LIMIT 1 OFFSET $offset",
        ("$revision", revision), ("$family", family), ("$offset", total * (i + 1) / 4));
    using var rows = command.ExecuteReader();
    if (rows.Read()) samples.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4)));
}
var scenarios = new List<Scenario>
{
    new("first-page", new(Family: family)),
    new("middle-page", new(Family: family, Offset: total / 2)),
    new("last-page", new(Family: family, Offset: Math.Max(0, total - 100)))
};
var termNumber = 0;
foreach (var sample in samples)
{
    scenarios.Add(new($"sample-label-{++termNumber}", new(Search: sample.Label, Family: family)));
    if (sample.LookupKey is { Length: > 0 } lookup) scenarios.Add(new($"sample-exact-{termNumber}", new(ExactKey: lookup, Family: family)));
}
var words = samples.SelectMany(s => Regex.Matches(s.Search, @"[\p{L}\p{N}_]{3,}").Select(m => m.Value))
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(word => word.Length).Take(3).ToArray();
for (var i = 0; i < words.Length; i++) scenarios.Add(new($"sample-word-{i + 1}", new(Search: words[i], Family: family)));
var first = samples[0];
scenarios.Add(new("key-default", new(Family: family)));
scenarios.Add(new("key-global", new(Family: family, Sort: AssetSort.Name)));
scenarios.Add(new("key-global-descending", new(Family: family, Sort: AssetSort.Name, Descending: true)));
if (first.Bank.Length > 0)
{
    scenarios.Add(new("key-source-default", new(GroupKey: first.Bank, Family: family)));
    scenarios.Add(new("key-source", new(GroupKey: first.Bank, Family: family, Sort: AssetSort.Name)));
}
if (first.Language.Length > 0)
{
    scenarios.Add(new("key-language", new(Language: first.Language, Family: family, Sort: AssetSort.Name)));
    if (first.Bank.Length > 0)
        scenarios.Add(new("key-source-language", new(GroupKey: first.Bank, Language: first.Language, Family: family, Sort: AssetSort.Name)));
}
if (first.Language.Length > 0) scenarios.Add(new("language-term", new(Search: first.Language, Family: family)));
if (first.Bank.Length > 0)
{
    scenarios.Add(new("group-page", new(GroupKey: first.Bank, Family: family)));
    scenarios.Add(new("group-previous-plan", new(GroupKey: first.Bank, Family: family), "previous-group-plan"));
    scenarios.Add(new("group-label-search", new(Search: first.Label, GroupKey: first.Bank, Family: family)));
    if (words.Length > 0) scenarios.Add(new("group-word-search", new(Search: words[0], GroupKey: first.Bank, Family: family)));
}
var runes = first.Label.EnumerateRunes().Take(2).ToArray();
if (runes.Length > 0) scenarios.Add(new("one-character", new(Search: runes[0].ToString(), Family: family)));
if (runes.Length > 1) scenarios.Add(new("two-characters", new(Search: string.Concat(runes.Select(r => r.ToString())), Family: family)));
var results = new List<object>();
foreach (var scenario in scenarios)
{
    if (options.TryGetValue("only", out var only) && !scenario.Name.StartsWith(only, StringComparison.Ordinal)) continue;
    var countPlan = Explain(scenario.Query, false, scenario.IndexHint);
    var pagePlan = Explain(scenario.Query, true, scenario.IndexHint);
    foreach (var page in new[] { false, true })
    {
        var timings = new List<object>();
        for (var repetition = 0; repetition < repeats; repetition++)
        {
            using var command = Build(scenario.Query, page, scenario.IndexHint);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            timer.Restart();
            long rowCount = 0;
            var status = "completed";
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var interrupt = timeout.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            try
            {
                if (page)
                {
                    using var rows = command.ExecuteReader();
                    while (rows.Read()) { timeout.Token.ThrowIfCancellationRequested(); _ = JsonSerializer.Deserialize<AssetDescriptor>(rows.GetString(0)); rowCount++; }
                }
                else rowCount = (long)command.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (timeout.IsCancellationRequested && ex.SqliteErrorCode == 9) { status = "timed-out"; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { status = "timed-out"; }
            timeout.CancelAfter(Timeout.Infinite);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            process.Refresh();
            timings.Add(new { Repetition = repetition, Status = status, CacheState = repetition == 0 ? "first-observed" : "warm", Milliseconds = elapsed,
                CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                WorkingSetBytes = process.WorkingSet64, PeakWorkingSetBytes = process.PeakWorkingSet64, Rows = rowCount });
        }
        results.Add(new { scenario.Name, Operation = page ? "page" : "count", scenario.Query, scenario.IndexHint, Plan = page ? pagePlan : countPlan, Timings = timings });
    }
    Console.WriteLine($"Measured {scenario.Name}");
}
var cancellation = new List<object>();
for (var i = 0; i < 3; i++)
{
    var keyCancellation = options.GetValueOrDefault("only") == "key";
    using var command = keyCancellation ? Build(new(Family: family, Sort: AssetSort.Name), true) : Build(scenarios.Single(s => s.Name == "one-character").Query, false);
    using var tokenSource = new CancellationTokenSource();
    using var registration = tokenSource.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
    timer.Restart();
    tokenSource.CancelAfter(25);
    var status = "completed";
    try { _ = command.ExecuteScalar(); }
    catch (SqliteException ex) when (tokenSource.IsCancellationRequested && ex.SqliteErrorCode == 9) { status = "interrupted"; }
    var milliseconds = timer.Elapsed.TotalMilliseconds;
    tokenSource.CancelAfter(Timeout.Infinite);
    cancellation.Add(new { Operation = keyCancellation ? "key-sort-page" : "short-term-count", Status = status, Milliseconds = milliseconds, RequestedAfterMilliseconds = 25,
        Recovery = ScalarLong("SELECT 1") == 1 });
}
object? footprint = null;
if (!options.GetValueOrDefault("footprint", "true").Equals("false", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Measuring storage footprint");
    var assetFields = new Dictionary<string, long>();
    using (var command = Command("SELECT COUNT(*),SUM(length(CAST(payload AS BLOB))),SUM(length(CAST(search AS BLOB))),SUM(length(CAST(search_fold AS BLOB))),SUM(length(CAST(sort_key AS BLOB))),SUM(length(CAST(identity AS BLOB))),SUM(length(CAST(revision AS BLOB))) FROM assets_v2 WHERE revision=$revision AND family=$family",
        ("$revision", revision), ("$family", family)))
    using (var rows = command.ExecuteReader())
    {
        rows.Read();
        var names = new[] { "Rows", "PayloadBytes", "SearchBytes", "SearchFoldBytes", "SortKeyBytes", "IdentityBytes", "RevisionBytes" };
        for (var i = 0; i < names.Length; i++) assetFields.Add(names[i], rows.IsDBNull(i) ? 0 : rows.GetInt64(i));
    }
    var featureFields = new List<object>();
    using (var command = Command("SELECT CASE WHEN instr(feature,'/item/')>0 THEN substr(feature,1,instr(feature,'/item/')-1) ELSE '<summary>' END,COUNT(*),SUM(length(CAST(payload AS BLOB))) FROM asset_features GROUP BY 1"))
    using (var rows = command.ExecuteReader())
        while (rows.Read()) featureFields.Add(new { Collection = rows.GetString(0), Rows = rows.GetInt64(1), PayloadBytes = rows.GetInt64(2) });
    var pageGroups = new List<object>();
    string? dbstatError = null;
    try
    {
        using var command = Command("SELECT name,COUNT(*),SUM(pgsize),SUM(payload),SUM(unused) FROM dbstat GROUP BY name");
        using var rows = command.ExecuteReader();
        while (rows.Read()) pageGroups.Add(new { Name = rows.GetString(0), Pages = rows.GetInt64(1), PageBytes = rows.GetInt64(2), PayloadBytes = rows.GetInt64(3), UnusedBytes = rows.GetInt64(4) });
    }
    catch (SqliteException ex) { dbstatError = ex.Message; }
    footprint = new { AssetFields = assetFields, FeatureFields = featureFields, PageGroups = pageGroups, DbstatError = dbstatError };
}
var report = new { TimestampUtc = DateTimeOffset.UtcNow, Family = family, TotalRows = total, HasLookupKey = hasLookup,
    HasNameIndex = hasNameIndex,
    Projection = projection, QueryTimeoutMilliseconds = timeoutMs,
    SourceOpenMilliseconds = sourceOpenMs, SqliteVersion = ScalarString("SELECT sqlite_version()"),
    MainBytes = new FileInfo(database).Length, WalBytes = File.Exists(database + "-wal") ? new FileInfo(database + "-wal").Length : 0,
    PageSize = ScalarLong("PRAGMA page_size"), FreePages = ScalarLong("PRAGMA freelist_count"), Footprint = footprint,
    Measurement = "Read-only SQL matching FeatureStore filtering and paging; first-observed results retain OS cache state; counts bypass the FeatureStore memoization cache.",
    Results = results, Cancellation = cancellation };
await File.WriteAllTextAsync(Path.Combine(output, "catalog-benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Saved results for {total:N0} rows.");
return 0;

SqliteCommand Command(string sql, params (string Name, object Value)[] values)
{
    var command = connection.CreateCommand(); command.CommandText = sql;
    foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
    return command;
}
long ScalarLong(string sql, params (string Name, object Value)[] values)
{ using var command = Command(sql, values); return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
string ScalarString(string sql, params (string Name, object Value)[] values)
{ using var command = Command(sql, values); return (string?)command.ExecuteScalar() ?? throw new InvalidOperationException("No matching cache rows."); }
SqliteCommand Build(AssetQuery query, bool page, string? indexHint = null)
{
    // Keep this SQL aligned with FeatureStore.AssetCommand when changing the production planner.
    var trigram = query.GroupKey is null && !string.IsNullOrWhiteSpace(query.Search) && query.Search.EnumerateRunes().Count() >= 3;
    if (indexHint is not null and not "previous-group-plan") throw new ArgumentException("Unsupported diagnostic index.");
    var hint = indexHint == "previous-group-plan"
        ? " INDEXED BY " + (page ? "assets_v2_family_sort" : "assets_v2_codec")
        : query.GroupKey is not null && query.ExactKey is null ? " INDEXED BY assets_v2_bank" : trigram ? " NOT INDEXED" : "";
    if (!trigram && query.GroupKey is null && query.Sort == AssetSort.Name && query.Language is not null && query.Family is not null)
        hint = " INDEXED BY assets_v2_language";
    if (page && hasNameIndex && query.Sort == AssetSort.Name && query.Family is not null && query.GroupKey is null &&
        string.IsNullOrWhiteSpace(query.Search) && query.ExactKey is null && query.Language is null && query.Codec is null &&
        query.Channels is null && query.Availability is null && query.Completeness is null && query.MinimumDuration is null && query.MaximumDuration is null)
        hint = " INDEXED BY assets_v2_name_v1";
    var command = Command($"SELECT {(page ? projection == "compact" ? "row_id" : "payload" : "COUNT(*)")} FROM assets_v2{hint} WHERE revision=$revision", ("$revision", revision));
    void Filter(string column, object? value)
    {
        if (value is null) return;
        command.CommandText += $" AND {column}=${column}";
        command.Parameters.AddWithValue("$" + column, value);
    }
    if (!string.IsNullOrWhiteSpace(query.Search))
    {
        command.CommandText += " AND deviant_contains(search,$search)";
        command.Parameters.AddWithValue("$search", query.Search);
        if (trigram)
        {
            command.CommandText += " AND row_id IN (SELECT rowid FROM assets_search_v2 WHERE assets_search_v2 MATCH $term)";
            command.Parameters.AddWithValue("$term", "\"" + query.Search.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }
    }
    Filter("bank", query.GroupKey); Filter("language", query.Language); Filter("codec", query.Codec);
    Filter("lookup_key", query.ExactKey); Filter("family", query.Family);
    Filter("channels", query.Channels); Filter("availability", query.Availability is { } availability ? (int)availability : null);
    Filter("completeness", query.Completeness is { } completeness ? (int)completeness : null);
    if (query.MinimumDuration is { } minimum)
    { command.CommandText += " AND duration >= $minimum"; command.Parameters.AddWithValue("$minimum", minimum); }
    if (query.MaximumDuration is { } maximum)
    { command.CommandText += " AND duration <= $maximum"; command.Parameters.AddWithValue("$maximum", maximum); }
    if (page)
    {
        var order = SortOrder(query);
        command.CommandText += " ORDER BY " + order + " LIMIT $limit OFFSET $offset";
        if (projection == "compact")
            command.CommandText = "WITH selected AS MATERIALIZED (" + command.CommandText + ") SELECT a.payload FROM selected s JOIN assets_v2 a ON a.row_id=s.row_id ORDER BY " + order;
        command.Parameters.AddWithValue("$limit", query.Limit); command.Parameters.AddWithValue("$offset", query.Offset);
    }
    return command;
}
static string SortOrder(AssetQuery query)
{
    var field = query.Sort switch
    {
        AssetSort.Default => "sort_key", AssetSort.Name => "deviant_natural(label)",
        AssetSort.Language => "language", AssetSort.Codec => "codec", AssetSort.Duration => "duration",
        AssetSort.Channels => "channels", AssetSort.Availability => "availability", AssetSort.Completeness => "completeness",
        _ => throw new ArgumentOutOfRangeException(nameof(query.Sort))
    };
    return field + (query.Descending ? " DESC" : " ASC") + " NULLS LAST," +
        (field == "sort_key" ? "" : "sort_key ASC,") + "deviant_ordinal(identity) ASC,deviant_member(identity) ASC,identity ASC";
}
static long IdentityOrdinal(string key)
{
    var span = key.AsSpan(); var split = span.IndexOf(':'); if (split < 0) return long.MaxValue;
    span = span[(split + 1)..]; split = span.IndexOf(':'); if (split >= 0) span = span[..split];
    return long.TryParse(span, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ordinal) ? ordinal : long.MaxValue;
}
static string IdentityMember(string key)
{
    var first = key.IndexOf(':'); var second = first < 0 ? -1 : key.IndexOf(':', first + 1);
    return second < 0 ? "" : NaturalOrder.Key(key[(second + 1)..]);
}
string[] Explain(AssetQuery query, bool page, string? indexHint = null)
{
    using var command = Build(query, page, indexHint); command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
    using var rows = command.ExecuteReader(); var plan = new List<string>();
    while (rows.Read()) plan.Add(rows.GetString(3));
    return plan.ToArray();
}
static SqliteConnection Open(string path)
{
    var result = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    result.Open(); return result;
}
sealed record Scenario(string Name, AssetQuery Query, string? IndexHint = null);
