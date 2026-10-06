using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Deviant.Core;

namespace Deviant.Tests;

internal sealed class ArchiveFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantTests", Guid.NewGuid().ToString("N"));
    public string Source { get; }
    public string Output => Path.Combine(Root, "output");
    public string Index => Path.Combine(Source, "sample.idx");
    public ArchiveFixture() { Source = Path.Combine(Root, "source"); Directory.CreateDirectory(Source); }
    public string Volume(uint package) => Path.Combine(Source, IndexLayout.VolumeName("sample", package));
    public void WriteIndex(params (uint Type, uint Flags, uint Id, uint Offset, uint Size, uint Alt, uint Package)[] rows)
    {
        var bytes = new byte[IndexLayout.HeaderLength + rows.Length * IndexLayout.RecordLength];
        IndexLayout.Magic.CopyTo(bytes); Put(bytes, 20, 18); Put(bytes, 24, 42); Put(bytes, 101, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; var values = new[] { r.Type, r.Flags, r.Id, r.Offset, r.Size, r.Alt, r.Package };
            for (var j = 0; j < values.Length; j++) Put(bytes, 105 + i * 28 + j * 4, values[j]);
        }
        File.WriteAllBytes(Index, bytes);
    }
    public static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
    public void WriteVolume(uint package, byte[] data) => File.WriteAllBytes(Volume(package), data);
    public void SparseVolume(uint package, long length)
    {
        using var stream = new FileStream(Volume(package), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        if (OperatingSystem.IsWindows() && !DeviceIoControl(stream.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Cannot create sparse test volume.");
        stream.SetLength(length);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, IntPtr input,
        uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{ public void Report(T value) => action(value); }
