using System.Globalization;
using Deviant.Core;

namespace Deviant.Catalog;

internal sealed class InMemoryResourceCatalog : IResourceCatalog
{
    private readonly IReadOnlyList<ResourceRecord> records;
    private readonly int[] ordinalOrder;
    private readonly int[] typeOrder;
    private readonly int[] idOrder;
    private readonly int[] packageOrder;
    private int disposed;

    public InMemoryResourceCatalog(ArchiveSnapshot snapshot, string cacheKey, CancellationToken token)
    {
        Snapshot = snapshot;
        CacheKey = cacheKey;
        records = snapshot.Records;
        ordinalOrder = Enumerable.Range(0, records.Count).ToArray();
        typeOrder = (int[])ordinalOrder.Clone();
        idOrder = (int[])ordinalOrder.Clone();
        packageOrder = (int[])ordinalOrder.Clone();
        Array.Sort(ordinalOrder, (left, right) => Compare(records[left].Ordinal, records[right].Ordinal, left, right));
        token.ThrowIfCancellationRequested();
        Array.Sort(typeOrder, (left, right) => Compare(records[left].Type, records[right].Type, records[left].Ordinal, records[right].Ordinal, left, right));
        token.ThrowIfCancellationRequested();
        Array.Sort(idOrder, (left, right) => Compare(records[left].Id, records[right].Id, records[left].Ordinal, records[right].Ordinal, left, right));
        token.ThrowIfCancellationRequested();
        Array.Sort(packageOrder, (left, right) => Compare(records[left].Package, records[right].Package, records[left].Ordinal, records[right].Ordinal, left, right));
        token.ThrowIfCancellationRequested();
    }

    public ArchiveSnapshot Snapshot { get; }
    public string CacheKey { get; }
    public bool ReusedIndexRows => false;

    public long Count(ResourceQuery query) => Count(query, CancellationToken.None);

    public long Count(ResourceQuery query, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Validate(query);
        var filter = SearchFilter.Create(query.Search);
        var range = FindCandidates(query);
        long count = 0;
        for (var position = range.Start; position < range.End; position++)
        {
            if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (Matches(records[range.Order[position]], query, filter)) count++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    public IReadOnlyList<ResourceRecord> Query(ResourceQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(query);
        var filter = SearchFilter.Create(query.Search);
        var range = FindCandidates(query);
        var result = new List<ResourceRecord>(Math.Min(query.Limit, 256));
        long matched = 0;
        for (var position = range.Start; position < range.End; position++)
        {
            if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var record = records[range.Order[position]];
            if (!Matches(record, query, filter)) continue;
            if (matched++ < query.Offset) continue;
            result.Add(record);
            if (result.Count == query.Limit) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    private CandidateRange FindCandidates(ResourceQuery query)
    {
        var best = new CandidateRange(ordinalOrder, 0, ordinalOrder.Length);
        if (query.Type is uint type) Consider(typeOrder, type, value => records[value].Type, ref best);
        if (query.Id is uint id) Consider(idOrder, id, value => records[value].Id, ref best);
        if (query.Package is uint package) Consider(packageOrder, package, value => records[value].Package, ref best);
        return best;
    }

    private static void Consider(int[] order, uint key, Func<int, uint> select, ref CandidateRange best)
    {
        var start = LowerBound(order, key, select);
        var end = UpperBound(order, key, select, start);
        if (end - start < best.End - best.Start) best = new(order, start, end);
    }

    private static int LowerBound(int[] order, uint key, Func<int, uint> select)
    {
        var low = 0;
        var high = order.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (select(order[middle]) < key) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(int[] order, uint key, Func<int, uint> select, int low)
    {
        var high = order.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (select(order[middle]) <= key) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int Compare(long first, long second, int firstIndex, int secondIndex)
    {
        var value = first.CompareTo(second);
        return value != 0 ? value : firstIndex.CompareTo(secondIndex);
    }

    private static int Compare(uint first, uint second, long firstOrdinal, long secondOrdinal, int firstIndex, int secondIndex)
    {
        var value = first.CompareTo(second);
        return value != 0 ? value : Compare(firstOrdinal, secondOrdinal, firstIndex, secondIndex);
    }

    private static bool Matches(ResourceRecord record, ResourceQuery query, SearchFilter filter) =>
        (query.Type is null || record.Type == query.Type) &&
        (query.Id is null || record.Id == query.Id) &&
        (query.Package is null || record.Package == query.Package) &&
        filter.Matches(record);

    private static void Validate(ResourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query), "Query offset must be zero or greater.");
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query), "Query limit must be between 1 and 10000.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private readonly record struct CandidateRange(int[] Order, int Start, int End);

    private readonly record struct SearchFilter(SearchKind Kind, string Text, uint? Numeric = null, long? Ordinal = null)
    {
        public static SearchFilter Create(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return new(SearchKind.None, "");
            var text = input.Trim();
            var colon = text.IndexOf(':');
            var recognizedKind = colon > 0 && new[] { "id", "type", "package", "volume", "ordinal" }
                .Contains(text[..colon], StringComparer.OrdinalIgnoreCase)
                    ? text[..colon].ToLowerInvariant() : "";
            var searchable = recognizedKind.Length == 0 ? text : text[(colon + 1)..].Trim();
            if ((recognizedKind is "id" or "type" or "package") && TryUInt(searchable, out var exact))
                return new(recognizedKind switch
                {
                    "id" => SearchKind.Id,
                    "type" => SearchKind.Type,
                    _ => SearchKind.Package
                }, "", exact);
            if (recognizedKind == "ordinal" && long.TryParse(searchable, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                return new(SearchKind.Ordinal, "", Ordinal: ordinal);
            if (recognizedKind == "volume") return new(SearchKind.Volume, searchable);
            var numeric = TryUInt(searchable, out var value) ? value : (uint?)null;
            return new(SearchKind.Any, text, numeric);
        }

        public bool Matches(ResourceRecord record) => Kind switch
        {
            SearchKind.None => true,
            SearchKind.Id => record.Id == Numeric,
            SearchKind.Type => record.Type == Numeric,
            SearchKind.Package => record.Package == Numeric,
            SearchKind.Ordinal => record.Ordinal == Ordinal,
            SearchKind.Volume => ContainsLike(record.VolumeName, Text),
            SearchKind.Any => (Numeric is uint number &&
                    (record.Id == number || record.Type == number || record.Package == number)) ||
                ContainsLike(record.VolumeName, Text) || ContainsLike(record.Id.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike(record.Id.ToString("X8", CultureInfo.InvariantCulture), Text) ||
                ContainsLike(record.Type.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike(record.Type.ToString("X8", CultureInfo.InvariantCulture), Text) ||
                ContainsLike(record.Package.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike(record.Ordinal.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike("id:" + record.Id.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike("type:" + record.Type.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike("package:" + record.Package.ToString(CultureInfo.InvariantCulture), Text) ||
                ContainsLike("volume:" + record.VolumeName, Text),
            _ => false
        };
    }

    private enum SearchKind { None, Id, Type, Package, Ordinal, Volume, Any }

    private static bool TryUInt(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (text.Any(c => (c is >= 'A' and <= 'F') || (c is >= 'a' and <= 'f')))
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool ContainsLike(string value, string needle)
    {
        if (needle.Length == 0) return true;
        for (var start = 0; start <= value.Length - needle.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (FoldAscii(value[start + offset]) == FoldAscii(needle[offset])) continue;
                matched = false;
                break;
            }
            if (matched) return true;
        }
        return false;
    }

    private static char FoldAscii(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
}
