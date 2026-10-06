using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Core;

public static class DirectoryBoundary
{
    public static bool IsWithin(string path, string root)
    {
        var candidate = Path.TrimEndingDirectorySeparator(CanonicalPath(path));
        var parent = Path.TrimEndingDirectorySeparator(CanonicalPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return string.Equals(candidate, parent, comparison) || candidate.StartsWith(prefix, comparison);
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        var existing = full;
        while (!Directory.Exists(existing) && !File.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new ArchiveException("UnsafeOutput", "The directory boundary cannot be resolved.");
        using var handle = CreateFileW(existing, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new ArchiveException("UnsafeOutput", "The physical directory boundary cannot be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        // Volume GUID paths make drive letters, extended DOS paths and drive aliases comparable.
        var physical = FinalPath(handle, 1) ?? FinalPath(handle, 0) ??
            throw new ArchiveException("UnsafeOutput", "The physical directory boundary cannot be resolved.");
        var remainder = Path.GetRelativePath(existing, full);
        return remainder == "." ? physical : Path.GetFullPath(Path.Combine(physical, remainder));
    }
    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(512); var size = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
        if (size == 0) return null;
        if (size >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)size + 1));
            size = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
            if (size == 0 || size >= buffer.Capacity) return null;
        }
        return buffer.ToString();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint length, uint flags);
}
