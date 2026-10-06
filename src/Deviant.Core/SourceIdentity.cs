using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Core;

public interface ISourceRevisionProvider
{
    FileStamp Capture(string path);
    FileStamp Capture(SafeFileHandle handle, string path);
}

public sealed class FileRevisionProvider : ISourceRevisionProvider
{
    public FileStamp Capture(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Capture(handle, path);
        }
        catch (FileNotFoundException) { return new(false, "", 0, 0, 0, false); }
        catch (DirectoryNotFoundException) { return new(false, "", 0, 0, 0, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(true, "", -1, 0, 0, false, ex.Message); }
    }

    public FileStamp Capture(SafeFileHandle handle, string path)
    {
        var length = RandomAccess.GetLength(handle);
        if (OperatingSystem.IsWindows())
        {
            var hasId = GetFileInformationByHandleEx(handle, 18, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>());
            var hasBasic = GetFileInformationByHandleEx(handle, 0, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>());
            const long epoch = 504911232000000000L;
            var identity = hasId ? $"{id.VolumeSerialNumber:X16}:{id.FileIdHigh:X16}{id.FileIdLow:X16}" : "";
            return new(true, identity, length, hasBasic ? checked(basic.LastWriteTime + epoch) : 0,
                hasBasic ? checked(basic.ChangeTime + epoch) : 0, hasId && hasBasic && basic.ChangeTime > 0);
        }
        return new(true, "", length, File.GetLastWriteTimeUtc(path).Ticks, 0, false);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileBasicInfo info, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong VolumeSerialNumber, FileIdLow, FileIdHigh; }
    [StructLayout(LayoutKind.Sequential)] private struct FileBasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
}

public static class SourceIdentity
{
    public static bool SameStamp(FileStamp a, FileStamp b) => a.Exists == b.Exists && a.FileIdentity == b.FileIdentity &&
        a.Length == b.Length && a.LastWriteUtcTicks == b.LastWriteUtcTicks && a.ChangeTimeUtcTicks == b.ChangeTimeUtcTicks &&
        a.Reliable == b.Reliable && a.Error == b.Error;

    public static string PhysicalStamp(IEnumerable<VolumeSource> volumes)
    {
        var fields = new List<string>();
        foreach (var volume in volumes.OrderBy(v => v.Package))
        {
            var s = volume.Stamp;
            fields.AddRange([volume.Package.ToString(CultureInfo.InvariantCulture), volume.RelativeName,
                s.Exists ? "present" : "absent", s.FileIdentity, s.Length.ToString(CultureInfo.InvariantCulture),
                s.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture), s.ChangeTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                s.Reliable ? "reliable" : "unreliable", s.Error is null ? "available" : "unavailable"]);
        }
        return HashFields("deviant-physical-stamp-v1", fields);
    }
    public static string IndexCacheKey(string indexContentId) =>
        HashFields("deviant-index-cache-v1", [IndexLayout.ParserVersion, indexContentId]);
    public static string DerivedCacheKey(string indexContentId, string physicalSourceStamp) =>
        HashFields("deviant-catalog-cache-v1", [IndexLayout.ParserVersion, indexContentId, physicalSourceStamp]);
    public static string ContentId(IEnumerable<ContentFileDigest> files) => HashFields("deviant-archive-content-v1",
        files.OrderBy(f => f.Role, StringComparer.Ordinal).ThenBy(f => f.Package).ThenBy(f => f.RelativeName, StringComparer.Ordinal)
            .SelectMany(f => new[] { f.Role, f.Package?.ToString(CultureInfo.InvariantCulture) ?? "", f.RelativeName,
                f.Length.ToString(CultureInfo.InvariantCulture), f.Sha256 }));
    public static string HashFields(string domain, IEnumerable<string> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var prefix = Encoding.UTF8.GetBytes(domain + "\0"); hash.AppendData(prefix);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static SafeFileHandle OpenPinned(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ex is FileNotFoundException or DirectoryNotFoundException ? "SourceChanged" :
                (ex.HResult & 0xffff) is 32 or 33 ? "SourceBusy" : "SourceUnavailable";
            throw new ArchiveException(code, "The source cannot be opened with read-only sharing.", ex);
        }
    }

    public static void EnsureUnchanged(ISourceRevisionProvider provider, SafeFileHandle handle, string path, FileStamp expected)
    {
        var held = provider.Capture(handle, path); var named = provider.Capture(path);
        if (!SameStamp(expected, held) || !SameStamp(held, named))
            throw new ArchiveException("SourceChanged", "The source identity or change stamp differs from the selected catalog revision.");
    }

    public static async Task<string> HashAsync(SafeFileHandle handle, long length, CancellationToken token,
        IProgress<ArchiveProgress>? progress = null, string phase = "Hashing")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; long offset = 0;
        while (offset < length)
        {
            token.ThrowIfCancellationRequested();
            var count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), offset, token);
            if (count == 0) throw new ArchiveException("SourceChanged", "The source ended during hashing.");
            hash.AppendData(buffer.AsSpan(0, count)); offset += count; progress?.Report(new(phase, offset, length));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
