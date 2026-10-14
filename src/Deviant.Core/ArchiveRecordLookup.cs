namespace Deviant.Core;

public static class ArchiveRecordLookup
{
    public static ResourceRecord? FindRecord(this ArchiveSnapshot snapshot, long ordinal)
    {
        if (ordinal >= 0 && ordinal < snapshot.Records.Count &&
            snapshot.Records[(int)ordinal] is { } row && row.Ordinal == ordinal) return row;
        return snapshot.Records.SingleOrDefault(item => item.Ordinal == ordinal);
    }

    public static ResourceRecord RequireRecord(this ArchiveSnapshot snapshot, long ordinal)
        => snapshot.FindRecord(ordinal) ?? throw new ArchiveException("MissingResource", "Resource is missing.");
}
