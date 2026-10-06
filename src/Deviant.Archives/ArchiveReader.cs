using System.Buffers.Binary;
using Deviant.Core;

namespace Deviant.Archives;

public sealed class ArchiveReader(ISourceRevisionProvider? revisionProvider = null) : IArchiveReader
{
    private readonly ISourceRevisionProvider provider = revisionProvider ?? new FileRevisionProvider();
    public async Task<ArchiveSnapshot> OpenAsync(string input, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var indexPath = ResolveIndex(input);
        using var handle = SourceIdentity.OpenPinned(indexPath);
        var indexStamp = provider.Capture(handle, indexPath);
        if (indexStamp.Length < IndexLayout.HeaderLength) throw new ArchiveException("TruncatedHeader", "The index header is truncated.");
        var indexId = await SourceIdentity.HashAsync(handle, indexStamp.Length, cancellationToken, progress, "Hashing index");
        var header = new byte[IndexLayout.HeaderLength];
        await ReadExactlyAsync(handle, header, 0, cancellationToken);
        if (!header.AsSpan(0, 20).SequenceEqual(IndexLayout.Magic)) throw new ArchiveException("InvalidSignature", "The index signature is not recognized.");
        var version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        if (version != IndexLayout.SupportedVersion) throw new ArchiveException("UnsupportedVersion", $"PC index version {version} is unsupported; expected version 18.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(101));
        var expectedLength = checked(IndexLayout.HeaderLength + (long)count * IndexLayout.RecordLength);
        if (indexStamp.Length != expectedLength) throw new ArchiveException("RecordCountMismatch", "The declared record count does not match the index length.");
        var stem = Path.GetFileNameWithoutExtension(indexPath); var root = Path.GetDirectoryName(indexPath)!;
        // Buffered sequential table reading avoids a disk request for every small row.
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = IndexLayout.HeaderLength;
        var records = new List<ResourceRecord>(); var packages = new HashSet<uint>(); var row = new byte[IndexLayout.RecordLength];
        for (long ordinal = 0; ordinal < count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(row, cancellationToken);
            var record = IndexLayout.ReadRecord(row, ordinal, stem); records.Add(record); packages.Add(record.Package);
            if ((ordinal & 8191) == 0) progress?.Report(new("Reading index", ordinal, count));
        }
        var volumes = packages.Order().Select(package =>
        {
            var name = IndexLayout.VolumeName(stem, package); var path = Path.Combine(root, name);
            return new VolumeSource(package, name, path, provider.Capture(path));
        }).ToArray();
        var byPackage = volumes.ToDictionary(v => v.Package); var diagnostics = new List<SourceDiagnostic>();
        foreach (var volume in volumes)
        {
            if (!volume.Stamp.Exists) diagnostics.Add(new("MissingVolume", "A referenced volume is absent.", Package: volume.Package));
            else if (volume.Stamp.Error is not null) diagnostics.Add(new("SourceUnavailable", "A referenced volume could not be inspected.", Package: volume.Package));
            else if (!volume.Stamp.Reliable) diagnostics.Add(new("UnreliableSourceStamp", "Persistent volume-dependent metadata must be revalidated.", Package: volume.Package));
        }
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i]; var stamp = byPackage[record.Package].Stamp;
            var validity = !stamp.Exists ? ResourceValidity.MissingVolume : stamp.Error is not null ? ResourceValidity.SourceUnavailable :
                IndexLayout.InBounds(record.Offset, record.StoredSize, stamp.Length) ? ResourceValidity.Valid : ResourceValidity.InvalidRange;
            if (validity != ResourceValidity.Valid)
            {
                records[i] = record with { Validity = validity, Diagnostic = validity.ToString() };
                if (validity == ResourceValidity.InvalidRange) diagnostics.Add(new("InvalidRange", "The indexed range exceeds its volume.", record.Ordinal, record.Package));
            }
        }
        SourceIdentity.EnsureUnchanged(provider, handle, indexPath, indexStamp);
        foreach (var volume in volumes)
            if (!SourceIdentity.SameStamp(volume.Stamp, provider.Capture(volume.FilePath)))
                throw new ArchiveException("SourceChanged", "A referenced volume changed while opening the catalog.");
        progress?.Report(new("Reading index", count, count));
        return new(indexPath, indexId, version, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24)), header[28..101], count,
            records.AsReadOnly(), volumes, indexStamp, SourceIdentity.PhysicalStamp(volumes), diagnostics.AsReadOnly());
    }
    public static string ResolveIndex(string input)
    {
        var path = Path.GetFullPath(input);
        if (!Directory.Exists(path))
        {
            if (!Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase))
                throw new ArchiveException("InvalidInput", "Select an index (.idx) or a directory containing one.");
            return path;
        }
        var candidates = Directory.GetFiles(path, "*.idx");
        if (candidates.Length != 1) throw new ArchiveException("AmbiguousInput", "The directory must contain exactly one index; select a specific .idx instead.");
        return candidates[0];
    }
    public static async Task ReadExactlyAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Memory<byte> bytes, long offset, CancellationToken token)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await RandomAccess.ReadAsync(handle, bytes[read..], checked(offset + read), token);
            if (n == 0) throw new ArchiveException("SourceChanged", "The source ended during a bounded read.");
            read += n;
        }
    }
}
