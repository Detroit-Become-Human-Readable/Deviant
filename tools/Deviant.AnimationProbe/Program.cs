using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Animation;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Deviant.AnimationProbe --input <index> --out <report.json> [--ordinals <n,n,...>] [--limit <n>] [--profile <framed|v14-tables|v14-blocks>]");
    return 0;
}
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i] is not ("--input" or "--out" or "--ordinals" or "--limit" or "--profile") ||
            !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Invalid or duplicate command-line option.");
    }
    if (!options.TryGetValue("--input", out var input) || !options.TryGetValue("--out", out var output))
        throw new ArgumentException("Input index and output report are required.");
    var limit = options.TryGetValue("--limit", out var limitText) ? int.Parse(limitText, CultureInfo.InvariantCulture) : 8;
    if (limit is < 1 or > 1000) throw new ArgumentException("Limit must be between 1 and 1000.");
    var ordinals = options.TryGetValue("--ordinals", out var ordinalText)
        ? ordinalText.Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToHashSet() : null;
    if (ordinals?.Any(n => n < 0) == true) throw new ArgumentException("Ordinals must be non-negative.");
    var profile = options.TryGetValue("--profile", out var profileText) ? profileText switch
    {
        "framed" => AnimationReadProfile.FramedRecords,
        "v14-tables" => AnimationReadProfile.Version14Tables,
        "v14-blocks" => AnimationReadProfile.Version14Blocks,
        _ => throw new ArgumentException("Profile must be framed, v14-tables or v14-blocks.")
    } : AnimationReadProfile.FramedRecords;
    var snapshot = await new ArchiveReader().OpenAsync(input);
    output = Path.GetFullPath(output);
    if (DirectoryBoundary.IsWithin(output, snapshot.SourceRoot))
        throw new ArgumentException("Choose an output report outside the archive directory.");
    if (File.Exists(output)) throw new ArgumentException("Output report already exists.");
    var rows = snapshot.Records.Where(r => r.Type == AnimationDataReader.ResourceType &&
        (ordinals is null || ordinals.Contains(r.Ordinal))).Take(limit).ToArray();
    if (ordinals is not null && !ordinals.SetEquals(rows.Select(r => r.Ordinal)))
        throw new ArgumentException("One or more requested ordinals are absent, have a different type, or exceed the limit.");
    var resolver = new ArchiveResourceResolver(snapshot); var reader = new AnimationDataReader();
    var reports = new List<object>();
    var failureCount = 0;
    foreach (var row in rows)
    {
        if (row.Validity != ResourceValidity.Valid || row.StoredSize > AnimationDataReader.MaximumResourceBytes)
        {
            failureCount++;
            reports.Add(new { row.Ordinal, Status = row.Validity != ResourceValidity.Valid ? row.Validity.ToString() : "OverLimit" });
            continue;
        }
        var identity = new ResourceIdentity(snapshot.IndexContentId, row.Ordinal);
        var bytes = await resolver.ReadAsync(identity, 0, checked((int)row.StoredSize));
        var result = reader.Decode(bytes, identity, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            new(row.VolumeName, row.Offset, row.StoredSize), profile);
        var exactByteMatch = result.Value?.EncodedResource.Span.SequenceEqual(bytes) == true;
        var tableFailure = profile is AnimationReadProfile.Version14Tables or AnimationReadProfile.Version14Blocks &&
            result.Value is not null && result.Value.Version14Layout is null;
        var blockFailure = profile == AnimationReadProfile.Version14Blocks &&
            result.Value?.Version14Layout?.PostTableDiagnostics.Count > 0;
        if (!exactByteMatch || tableFailure || blockFailure) failureCount++;
        reports.Add(new
        {
            Status = !exactByteMatch ? "Failed" : tableFailure ? "TableFailed" : blockFailure ? "BlocksIncomplete" : "Inspected",
            row.Ordinal, row.Type, row.Id, row.Package, row.Offset, row.StoredSize,
            Identity = identity,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            result.Diagnostics,
            result.UnknownFields,
            HeaderWords = result.Value?.HeaderWords,
            Version14Layout = result.Value?.Version14Layout,
            Sections = result.Value?.Sections.Select(s => new
            {
                s.Tag, s.Version, s.ResourceOffset, s.BodyOffset, s.BodyLength,
                BodySha256 = Convert.ToHexString(SHA256.HashData(s.Body.Span)).ToLowerInvariant()
            }).ToArray(),
            PreservedByteCount = result.Value?.EncodedResource.Length,
            ExactByteMatch = exactByteMatch
        });
    }
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(file, new
    {
        Format = "deviant-animation-probe-v1", snapshot.IndexContentId, ReaderVersion = reader.Version, Profile = profile.ToString(),
        AvailableResources = snapshot.Records.Count(r => r.Type == AnimationDataReader.ResourceType),
        SelectedResources = reports.Count, FailedResources = failureCount, Resources = reports
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine($"Inspected {reports.Count - failureCount} animation resources; {failureCount} failed.");
    return failureCount > 0 ? 1 : reports.Count > 0 ? 0 : 2;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
