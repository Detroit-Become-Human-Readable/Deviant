namespace Deviant.Desktop;

public sealed record ViewportResourceCacheStatistics(int Entries, long CpuBytes, long GpuReservedBytes,
    int UploadedEntries, long NativeBufferBytes, long ReturnHits, long Evictions);

/// <summary>Owns only unused resource entries; active consumer ownership stays with the viewport service.</summary>
internal sealed class ViewportResourceCache(long maximumCpuBytes = 64L * 1024 * 1024,
    long maximumGpuBytes = 64L * 1024 * 1024, int maximumEntries = 4096)
{
    private readonly Dictionary<string, Item> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = [];
    public int Count => entries.Count;
    public long CpuBytes { get; private set; }
    public long GpuBytes { get; private set; }
    public long Hits { get; private set; }
    public long Evictions { get; private set; }

    public bool Retain(string key, long cpuBytes, long gpuBytes, Action release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key); ArgumentNullException.ThrowIfNull(release);
        if (maximumCpuBytes < 0 || maximumGpuBytes < 0 || maximumEntries < 1 || cpuBytes < 0 || gpuBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(cpuBytes));
        if (entries.ContainsKey(key)) throw new InvalidOperationException("The resource is already unused.");
        if (cpuBytes > maximumCpuBytes || gpuBytes > maximumGpuBytes) return false;
        while (order.First is not null && (entries.Count >= maximumEntries ||
            CpuBytes > maximumCpuBytes - cpuBytes || GpuBytes > maximumGpuBytes - gpuBytes)) EvictOldest();
        var node = order.AddLast(key); entries.Add(key, new(cpuBytes, gpuBytes, release, node));
        CpuBytes += cpuBytes; GpuBytes += gpuBytes; return true;
    }
    public bool Take(string key)
    {
        if (!entries.Remove(key, out var item)) return false;
        RemoveAccounting(item); Hits++; return true;
    }
    public bool EvictOldest()
    {
        if (order.First is not { } first) return false;
        var item = entries[first.Value]; entries.Remove(first.Value); RemoveAccounting(item); Evictions++; item.Release(); return true;
    }
    public void TrimUntil(Func<bool> hasRoom)
    {
        ArgumentNullException.ThrowIfNull(hasRoom);
        while (!hasRoom() && EvictOldest()) { }
    }
    public void Clear()
    {
        List<Exception>? failures = null;
        while (order.First is not null)
            try { EvictOldest(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException("Some unused viewport resources could not be released.", failures);
    }
    private void RemoveAccounting(Item item)
    { order.Remove(item.Node); CpuBytes -= item.Cpu; GpuBytes -= item.Gpu; }
    private sealed record Item(long Cpu, long Gpu, Action Release, LinkedListNode<string> Node);
}
