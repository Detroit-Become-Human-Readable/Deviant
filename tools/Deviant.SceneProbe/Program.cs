using System.Globalization;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Deviant.Formats.Containers;
using Deviant.Core;

if (args.Length == 3 && args[0] is "structure" or "section" && long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var structureOrdinal))
    return await InspectStructureAsync(args[1], structureOrdinal, args[0] == "section");

if (args.Length == 3 && args[0] is "directory" or "node-tags")
    return await InspectDirectoryAsync(args[1], args[2], inventory: args[0] == "node-tags");
if (args.Length == 3 && args[0] == "correlate")
    return await CorrelateAsync(args[1], args[2]);
if (args.Length is 4 or 5 && args[0] == "node-window" && int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var candidateOffset))
    return await InspectNodeWindowAsync(args[1], args[2], candidateOffset, args.Length == 5 ? args[4] : null);

if (args.Length != 3 || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
{
    Console.Error.WriteLine("Usage: Deviant.SceneProbe <index> <dependencies> <owner-ordinal>");
    Console.Error.WriteLine("       Deviant.SceneProbe structure|section <index> <owner-ordinal>");
    Console.Error.WriteLine("       Deviant.SceneProbe directory|node-tags <decoded-prefix> <context.json>");
    Console.Error.WriteLine("       Deviant.SceneProbe correlate <dependency-result.json> <directory-result.json>");
    Console.Error.WriteLine("       Deviant.SceneProbe node-window <decoded-prefix> <context.json> <candidate-offset> [directory-result.json]");
    return 2;
}
try
{
    var archive = await new ArchiveReader().OpenAsync(args[0]);
    var dependencyPath = Path.GetFullPath(args[1]);
    var limits = new SceneDependencyLimits();
    using var stream = new FileStream(dependencyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (stream.Length > limits.MaximumBytes) throw new InvalidDataException("Dependency file exceeds the inspection limit.");
    var revisionProvider = new FileRevisionProvider();
    var dependencyStamp = revisionProvider.Capture(stream.SafeFileHandle, dependencyPath);
    var bytes = new byte[checked((int)stream.Length)];
    await stream.ReadExactlyAsync(bytes);
    var result = new SceneDependencyReader().Inspect(bytes, archive,
        new ResourceIdentity(archive.IndexContentId, ordinal), Path.GetFileName(dependencyPath), limits);
    SourceIdentity.EnsureUnchanged(revisionProvider, stream.SafeFileHandle, dependencyPath, dependencyStamp);
    if (revisionProvider.Capture(archive.IndexPath) != archive.IndexStamp)
        throw new ArchiveException("SourceChanged", "Archive index changed during dependency inspection.");
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return result.Value is null ? 1 : 0;
}
catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> InspectStructureAsync(string input, long ordinal, bool section)
{
    try
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var token = cancellation.Token;
            var snapshot = await new ArchiveReader().OpenAsync(input, cancellationToken: token);
            if (ordinal < 0 || ordinal >= snapshot.Records.Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
            var source = snapshot.Records[checked((int)ordinal)];
            var container = await new SegsContainerReader().ReadAsync(snapshot, source,
                new SegsDecodeLimits(16 * 1024 * 1024, 64 * 1024 * 1024), token);
            var directory = new NativeObjectDirectoryLoader().Read(container, token);
            if (directory.Value is not { } document)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { directory.Identity, directory.Revision, directory.Extents, directory.Steps, directory.Diagnostics }));
                return 1;
            }
            if (section)
            {
                var inspected = new SceneSectionReader().Read(directory, token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    inspected.Identity, inspected.Revision, inspected.Extents, inspected.Steps, inspected.Diagnostics,
                    inspected.UnresolvedReferences,
                    Section = inspected.Value is { } value ? new
                    {
                        value.SourceContainer, Nodes = value.Hierarchy.Nodes.Count, value.Hierarchy.Roots,
                        value.CatalogInstances, value.Entities, Environment = new { value.Environment.Identity, value.Environment.Version, value.Environment.Extent },
                        Objects = value.Objects.Count, value.Diagnostics
                    } : null
                }, new JsonSerializerOptions { WriteIndented = true }));
                return inspected.Value is null ? 1 : 0;
            }
            var reader = new NodeRecordReader();
            var parsedNodes = document.Objects.Where(frame => frame.NativeType == NodeRecordReader.NativeType)
                .Select(frame => reader.Read(frame, directory.Revision, directory.Extents, directory.Steps, token)).ToArray();
            var records = parsedNodes.Where(result => result.Value is not null).Select(result => result.Value!).ToArray();
            var graph = new SceneHierarchyBuilder().Build(document, records, token: token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Format = "deviant-node-structure-v1", directory.Identity, directory.Revision, directory.Extents, directory.Steps,
                ObjectCount = document.Objects.Count, NodeCount = graph.Nodes.Count, RootCount = graph.Roots.Count,
                UnsupportedNodes = parsedNodes.Where(result => result.Value is null).Select(result => new { result.Identity, result.Diagnostics }),
                graph.Diagnostics, graph.Roots,
                Nodes = graph.Nodes.Select(node => new
                {
                    node.Identity, node.Record.NativeId, node.Record.Flags, Pivot = new[] { node.Record.Pivot.X, node.Record.Pivot.Y, node.Record.Pivot.Z },
                    node.Record.Parent, node.Record.FirstChild, node.Record.NextSibling, node.Record.ParentAttachmentIndex,
                    node.Record.StoredLocalTransform, node.Record.StoredWorldTransform, node.LocalTransform, node.WorldTransform,
                    node.TransformStatus, node.ParentIdentity, node.Children, node.Record.FieldExtents,
                    node.Record.Word172, node.Record.Word176
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
    catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
    { Console.Error.WriteLine(error.Message); return 1; }
}


static async Task<int> InspectDirectoryAsync(string prefixInput, string contextInput, bool inventory = false)
{
    try
    {
        var prefixPath = Path.GetFullPath(prefixInput); var contextPath = Path.GetFullPath(contextInput);
        using var prefix = new FileStream(prefixPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var contextStream = new FileStream(contextPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var limits = new DcInfoDirectoryLimits();
        if (prefix.Length > limits.MaximumBytes || contextStream.Length > 64 * 1024)
            throw new InvalidDataException("Directory input exceeds the inspection limit.");
        var provider = new FileRevisionProvider();
        var prefixStamp = provider.Capture(prefix.SafeFileHandle, prefixPath);
        var contextStamp = provider.Capture(contextStream.SafeFileHandle, contextPath);
        var context = await JsonSerializer.DeserializeAsync<DirectoryProbeContext>(contextStream)
            ?? throw new InvalidDataException("Directory context is missing.");
        if (context.Container is null || context.Revision is null || context.OriginalExtents is null)
            throw new InvalidDataException("Directory context requires container, revision and original extents.");
        var bytes = new byte[checked((int)prefix.Length)];
        await prefix.ReadExactlyAsync(bytes);
        object result; bool succeeded;
        if (inventory)
        {
            var found = new NodeTagInventoryReader().Scan(bytes, context.Container, context.Revision,
                context.OriginalExtents, context.DecodedOffset, precedingSteps: context.Steps);
            result = found; succeeded = found.Value is not null;
        }
        else
        {
            var found = new DcInfoDirectoryReader().Inspect(bytes, context.Container, context.Revision,
                context.OriginalExtents, context.DecodedOffset, limits, context.Steps);
            result = found; succeeded = found.Value is not null;
        }
        SourceIdentity.EnsureUnchanged(provider, prefix.SafeFileHandle, prefixPath, prefixStamp);
        SourceIdentity.EnsureUnchanged(provider, contextStream.SafeFileHandle, contextPath, contextStamp);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return succeeded ? 0 : 1;
    }
    catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or JsonException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}




static async Task<int> CorrelateAsync(string dependencyPath, string directoryPath)
{
    try
    {
        var dependencies = await ReadCheckedJsonAsync<DecodeResult<SceneDependencyInspection>>(dependencyPath);
        var directory = await ReadCheckedJsonAsync<DecodeResult<DcInfoDirectoryInspection>>(directoryPath);
        var result = new DependencyDirectoryCorrelator().Compare(dependencies, directory);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or JsonException)
    { Console.Error.WriteLine(ex.Message); return 1; }
}

static async Task<T> ReadCheckedJsonAsync<T>(string input, int maximumBytes = 16 * 1024 * 1024)
{
    var path = Path.GetFullPath(input); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (stream.Length > maximumBytes) throw new InvalidDataException("Structured input exceeds the read limit.");
    var provider = new FileRevisionProvider(); var stamp = provider.Capture(stream.SafeFileHandle, path);
    var value = await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new InvalidDataException("Correlation input is empty.");
    SourceIdentity.EnsureUnchanged(provider, stream.SafeFileHandle, path, stamp); return value;
}




static async Task<int> InspectNodeWindowAsync(string prefixPath, string contextPath, int candidateOffset, string? directoryPath)
{
    try
    {
        var context = await ReadCheckedJsonAsync<DirectoryProbeContext>(contextPath, 64 * 1024);
        var directory = directoryPath is null ? null : await ReadCheckedJsonAsync<DecodeResult<DcInfoDirectoryInspection>>(directoryPath);
        var full = Path.GetFullPath(prefixPath); using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Decoded prefix exceeds the read limit.");
        var provider = new FileRevisionProvider(); var stamp = provider.Capture(stream.SafeFileHandle, full);
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes);
        var result = new NodeFixedReadWindowInspector().Inspect(bytes, candidateOffset, context.Container, context.Revision,
            context.OriginalExtents, directory, context.DecodedOffset, context.Steps);
        SourceIdentity.EnsureUnchanged(provider, stream.SafeFileHandle, full, stamp);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Value is null ? 1 : 0;
    }
    catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or JsonException)
    { Console.Error.WriteLine(ex.Message); return 1; }
}

internal sealed record DirectoryProbeContext(ResourceIdentity Container, SourceRevision Revision,
    IReadOnlyList<SourceExtent> OriginalExtents, long DecodedOffset = 0, IReadOnlyList<DecodeStep>? Steps = null);
