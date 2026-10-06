using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Deviant.Core;

namespace Deviant.Export;

public static class OutputSafety
{
    public static void ValidateOutputRoot(string outputRoot, string sourceRoot)
    {
        var output = Path.GetFullPath(outputRoot); var source = Path.GetFullPath(sourceRoot);
        if (IsWithin(output, source)) throw new ArchiveException("UnsafeOutput", "Outputs must be outside the source directory.");
        RejectReparsePoints(output);
        if (File.Exists(output)) throw new ArchiveException("UnsafeOutput", "The output directory names an existing file.");
    }
    public static void ValidateFilePath(string fullPath, string sourceRoot)
    {
        var full = Path.GetFullPath(fullPath); ValidateOutputRoot(Path.GetDirectoryName(full)!, sourceRoot);
        RejectReparsePoints(full);
        if (Directory.Exists(full)) throw new ArchiveException("UnsafeOutput", "The output file names an existing directory.");
    }
    public static string ResolveOutputPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArchiveException("UnsafeOutput", "An output name is not a safe relative path.");
        var pieces = relative.Split(['/', '\\']);
        if (pieces.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArchiveException("UnsafeOutput", "An output name contains an unsafe path component.");
        foreach (var piece in pieces)
        {
            var name = piece.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" ||
                (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9'))
                throw new ArchiveException("UnsafeOutput", "An output name uses a reserved device name.");
        }
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(full, root) || string.Equals(full, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new ArchiveException("UnsafeOutput", "An output name escapes its directory.");
        RejectReparsePoints(full); return full;
    }
    private static bool IsWithin(string path, string root)
    {
        return DirectoryBoundary.IsWithin(path, root);
    }
    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArchiveException("UnsafeOutput", "Output paths cannot contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Deny rename/delete of the entire directory chain while creating and publishing outputs.
    // A path check alone would leave a junction-swap race during a long export.
    public static IDisposable PinDirectory(string path)
    {
        RejectReparsePoints(path); var handles = new List<SafeFileHandle>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                {
                    var handle = CreateFileW(current, 0x80, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot pin output directory.", new Win32Exception(error)); }
                    handles.Add(handle); RejectReparsePoints(current);
                }
            }
            return new DirectoryPins(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class DirectoryPins(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
