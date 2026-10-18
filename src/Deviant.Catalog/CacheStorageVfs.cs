using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Deviant.Catalog;

internal interface ICacheStorageWriteLease : IDisposable
{
    // If reconciliation fails, Dispose must leave accounting dirty/conservative
    // until the owner can inventory it again; it must not refund unseen growth.
    void Complete(long actualLength);
}

/// <summary>
/// Wraps the bundled SQLite OS VFS. Reservations cover logical file lengths,
/// including WAL, journals, temporary files and the OS VFS's internal SHM file.
/// The owner must serialize reservations for a root and permit non-growing
/// cleanup even when that root is already over budget.
/// </summary>
internal static unsafe class CacheStorageVfs
{
    // ABI: https://sqlite.org/c3ref/vfs.html and /c3ref/io_methods.html.
    // The appended real-file layout follows SQLite's ext/misc/vfstrace.c.
    private const int Ok = 0, IoError = 10, NotFound = 12, Full = 13, CannotOpen = 14;
    private const int OpenReadWrite = 2, OpenCreate = 4, OpenDeleteOnClose = 8;
    private const int OpenExclusive = 16, OpenUri = 64;
    private const int SizeHint = 5, ChunkSize = 6, TempFilename = 16;
    private const int Win32SetHandle = 23, BeginAtomicWrite = 31;
    private const int CommitAtomicWrite = 32, RollbackAtomicWrite = 33;
    private const int BatchAtomic = 0x00004000;
    private static readonly int RealFileOffset = (sizeof(FileHeader) + 15) & ~15;
    private static readonly object RegistrationGate = new();
    private static readonly Dictionary<string, Registration> Registrations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // Registrations, native method tables and registration GCHandles deliberately
    // live for the process lifetime. Unregister/free needs both pooled-connection
    // shutdown and proof that no SQLite file or native callback still uses them.
    internal static string Register(string root, Func<string, long, long, ICacheStorageWriteLease?> acquire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(acquire);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (RegistrationGate)
        {
            if (Registrations.TryGetValue(normalized, out var existing))
            {
                // A shared root policy may outlive an individual manager/store.
                Volatile.Write(ref existing.Acquire, acquire);
                return existing.Name;
            }

            SQLitePCL.Batteries_V2.Init();
            if (SQLitePCL.raw.GetNativeLibraryName() != "e_sqlite3")
                throw new NotSupportedException("Cache storage requires the bundled SQLite provider.");
            var original = NativeVfsFind(null);
            if (original == null || original->Version < 1 || original->FileSize <= 0)
                throw new InvalidOperationException("SQLite has no usable OS VFS.");
            var originalName = Marshal.PtrToStringUTF8((nint)original->Name);
            // SHM filenames and allocation rules below describe the bundled OS
            // VFS, not arbitrary third-party VFS implementations.
            if (originalName is not ("win32" or "unix"))
                throw new NotSupportedException("Cache storage requires SQLite's default OS VFS.");

            var key = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
            var name = "deviant-cache-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
            var registration = new Registration(normalized, name, original, acquire);
            try
            {
                registration.Initialize();
                var result = NativeVfsRegister(registration.Native, 0);
                if (result != Ok)
                    throw new IOException($"SQLite VFS registration failed ({result}).");
            }
            catch
            {
                registration.ReleaseUnregistered();
                throw;
            }
            Registrations.Add(normalized, registration);
            return name;
        }
    }

    private sealed class Registration(string root, string name, Vfs* original,
        Func<string, long, long, ICacheStorageWriteLease?> acquire)
    {
        internal readonly string Root = root;
        internal readonly string Name = name;
        internal readonly Vfs* Original = original;
        internal Func<string, long, long, ICacheStorageWriteLease?> Acquire = acquire;
        internal Vfs* Native;
        private GCHandle handle;
        private nint nativeName;

        internal void Initialize()
        {
            Native = (Vfs*)NativeMemory.AllocZeroed((nuint)sizeof(Vfs));
            if (Native == null) throw new OutOfMemoryException();
            nativeName = Marshal.StringToCoTaskMemUTF8(Name);
            handle = GCHandle.Alloc(this);
            Native->Version = Math.Min(Original->Version, 3);
            Native->FileSize = checked(RealFileOffset + Original->FileSize);
            Native->MaxPathname = Original->MaxPathname;
            Native->Name = (byte*)nativeName;
            Native->AppData = GCHandle.ToIntPtr(handle);
            Native->Open = &Open;
            Native->Delete = &Delete;
            Native->Access = &Access;
            Native->FullPathname = &FullPathname;
            Native->DlOpen = &DlOpen;
            Native->DlError = &DlError;
            Native->DlSym = &DlSym;
            Native->DlClose = &DlClose;
            Native->Randomness = &Randomness;
            Native->Sleep = &Sleep;
            Native->CurrentTime = &CurrentTime;
            Native->GetLastError = &GetLastError;
            if (Native->Version >= 2 && Original->CurrentTimeInt64 != null)
                Native->CurrentTimeInt64 = &CurrentTimeInt64;
            if (Native->Version >= 3)
            {
                if (Original->SetSystemCall != null) Native->SetSystemCall = &SetSystemCall;
                if (Original->GetSystemCall != null) Native->GetSystemCall = &GetSystemCall;
                if (Original->NextSystemCall != null) Native->NextSystemCall = &NextSystemCall;
            }
        }

        internal void ReleaseUnregistered()
        {
            if (handle.IsAllocated) handle.Free();
            Marshal.FreeCoTaskMem(nativeName);
            NativeMemory.Free(Native);
        }

        internal string OwnedPath(byte* filename)
        {
            var path = Path.GetFullPath(Marshal.PtrToStringUTF8((nint)filename)
                ?? throw new IOException("SQLite supplied no filename."));
            var relative = Path.GetRelativePath(Root, path);
            if (relative == "." || Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("SQLite file lies outside the cache root.");
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache folders cannot contain symbolic links or junctions.");
                if (string.Equals(ancestor, Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache files cannot be symbolic links.");
            return path;
        }

        internal string NewTempPath() => Path.Combine(Root, ".sqlite-temp-" + Guid.NewGuid().ToString("N"));
        internal ICacheStorageWriteLease? Reserve(string path, long current, long requested) =>
            Volatile.Read(ref Acquire)(path, current, requested);
    }

    private sealed class OpenFile(Registration registration, string path, bool deleteOnClose)
    {
        internal readonly Registration Registration = registration;
        internal readonly string Path = path;
        internal readonly bool DeleteOnClose = deleteOnClose;
        internal IoMethods* Original;
        internal IoMethods* Methods;
        internal byte* OwnedFilename;
        internal int Chunk;
    }

    private static Registration Context(Vfs* vfs) => (Registration)GCHandle.FromIntPtr(vfs->AppData).Target!;
    private static OpenFile State(FileHeader* file) => (OpenFile)GCHandle.FromIntPtr(file->State).Target!;
    private static FileHeader* Real(FileHeader* file) => (FileHeader*)((byte*)file + RealFileOffset);
    private static long DiskLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private static long FileLength(OpenFile state, FileHeader* real)
    {
        long size;
        var result = state.Original->FileSize(real, &size);
        if (result != Ok || size < 0) throw new IOException("SQLite file length could not be read.");
        // An unlinked DELETEONCLOSE file still occupies storage until xClose.
        return size;
    }

    private static void CompleteFile(ICacheStorageWriteLease lease, OpenFile state, FileHeader* real) =>
        lease.Complete(FileLength(state, real));

    private static void CompletePath(ICacheStorageWriteLease lease, string path) =>
        lease.Complete(DiskLength(path));

    private static long Round(long size, int chunk)
    {
        if (size < 0) throw new IOException("Invalid SQLite file length.");
        return chunk > 0 && size > 0 ? checked(((size - 1) / chunk + 1) * chunk) : size;
    }

    private static void InitializeMethods(OpenFile state)
    {
        var methods = state.Methods;
        var original = state.Original;
        methods->Version = Math.Min(original->Version, 3);
        methods->Close = &Close;
        methods->Read = &Read;
        methods->Write = &Write;
        methods->Truncate = &Truncate;
        methods->Sync = &Sync;
        methods->FileSize = &FileSize;
        methods->Lock = &Lock;
        methods->Unlock = &Unlock;
        methods->CheckReservedLock = &CheckReservedLock;
        methods->FileControl = &FileControl;
        methods->SectorSize = &SectorSize;
        methods->DeviceCharacteristics = &DeviceCharacteristics;
        if (methods->Version >= 2)
        {
            if (original->ShmMap != null) methods->ShmMap = &ShmMap;
            if (original->ShmLock != null) methods->ShmLock = &ShmLock;
            if (original->ShmBarrier != null) methods->ShmBarrier = &ShmBarrier;
            if (original->ShmUnmap != null) methods->ShmUnmap = &ShmUnmap;
        }
        if (methods->Version >= 3)
        {
            if (original->Fetch != null) methods->Fetch = &Fetch;
            if (original->Unfetch != null) methods->Unfetch = &Unfetch;
        }
    }

    private static void ReleaseFile(FileHeader* file, OpenFile state)
    {
        file->Methods = null;
        if (file->State != 0)
        {
            GCHandle.FromIntPtr(file->State).Free();
            file->State = 0;
        }
        if (state.OwnedFilename != null) NativeFreeFilename(state.OwnedFilename);
        NativeMemory.Free(state.Methods);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Open(Vfs* vfs, byte* name, FileHeader* file, int flags, int* outputFlags)
    {
        file->Methods = null;
        file->State = 0;
        OpenFile? state = null;
        try
        {
            var context = Context(vfs);
            string path;
            if (name == null)
            {
                path = context.NewTempPath();
                state = new OpenFile(context, path, true);
                // Use SQLite's filename allocator: URI helpers may inspect data
                // surrounding sqlite3_filename even for a non-URI filename.
                var bytes = Encoding.UTF8.GetBytes(path + '\0');
                fixed (byte* text = bytes)
                {
                    byte empty = 0;
                    name = NativeCreateFilename(text, &empty, &empty, 0, null);
                }
                if (name == null) return IoError;
                state.OwnedFilename = name;
                flags = (flags | OpenReadWrite | OpenCreate | OpenExclusive | OpenDeleteOnClose) & ~OpenUri;
            }
            else
            {
                path = context.OwnedPath(name);
                state = new OpenFile(context, path, (flags & OpenDeleteOnClose) != 0);
            }
            if (Encoding.UTF8.GetByteCount(path) > context.Original->MaxPathname) return CannotOpen;
            state.Methods = (IoMethods*)NativeMemory.AllocZeroed((nuint)sizeof(IoMethods));
            if (state.Methods == null) return IoError;
            file->State = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            var real = Real(file);
            NativeMemory.Clear(real, (nuint)context.Original->FileSize);
            ICacheStorageWriteLease? lease = null;
            try
            {
                if ((flags & (OpenReadWrite | OpenCreate | OpenDeleteOnClose)) != 0)
                {
                    var length = DiskLength(path);
                    lease = context.Reserve(path, length, length);
                    if (lease == null) return Full;
                }
                var result = context.Original->Open(context.Original, name, real, flags, outputFlags);
                if (real->Methods != null)
                {
                    state.Original = real->Methods;
                    InitializeMethods(state);
                    file->Methods = state.Methods;
                }
                lease?.Complete(state.Original != null ? FileLength(state, real) : DiskLength(path));
                return result == Ok && file->Methods == null ? IoError : result;
            }
            finally { lease?.Dispose(); }
        }
        catch { return IoError; }
        finally
        {
            if (file->Methods == null && state != null)
            {
                try { ReleaseFile(file, state); }
                catch { /* No managed exception may cross SQLite's C boundary. */ }
            }
        }
    }

    // Every size-changing operation reconciles even a failed/partial native I/O.
    private static int MutateFile(FileHeader* file, int operation, void* buffer, int amount, long offset)
    {
        var state = State(file);
        var real = Real(file);
        // Named files are sized under the root admission lock. Only Unix
        // unlinked temporary files need their live handle length as a fallback.
        var current = !OperatingSystem.IsWindows() && state.DeleteOnClose ? FileLength(state, real) : 0;
        var requested = operation switch
        {
            0 => checked(offset + amount),
            1 => Round(offset, state.Chunk),
            2 => Round(*(long*)buffer, state.Chunk),
            _ => current
        };
        using var lease = state.Registration.Reserve(state.Path, current, requested);
        if (lease == null) return Full;
        try
        {
            if (operation == 0) return state.Original->Write(real, buffer, amount, offset);
            if (operation == 1) return state.Original->Truncate(real, offset);
            var result = state.Original->FileControl(real, operation == 2 ? SizeHint : ChunkSize, buffer);
            if (operation == 3 && result == Ok) state.Chunk = Math.Max(0, *(int*)buffer);
            return result;
        }
        finally { CompleteFile(lease, state, real); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Write(FileHeader* file, void* buffer, int amount, long offset)
    {
        try { return amount < 0 || offset < 0 ? IoError : MutateFile(file, 0, buffer, amount, offset); }
        catch (OverflowException) { return Full; }
        catch { return IoError; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Truncate(FileHeader* file, long size)
    {
        try { return MutateFile(file, 1, null, 0, size); }
        catch (OverflowException) { return Full; }
        catch { return IoError; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FileControl(FileHeader* file, int operation, void* argument)
    {
        try
        {
            if (operation == SizeHint || operation == ChunkSize)
                return argument == null ? IoError : MutateFile(file, operation == SizeHint ? 2 : 3, argument, 0, 0);
            var state = State(file);
            if (operation == TempFilename)
            {
                if (argument == null) return IoError;
                *(byte**)argument = null;
                var bytes = Encoding.UTF8.GetBytes(state.Registration.NewTempPath() + '\0');
                if (bytes.Length > state.Registration.Original->MaxPathname + 1) return CannotOpen;
                var allocated = (byte*)NativeMalloc64((ulong)bytes.Length);
                if (allocated == null) return IoError;
                bytes.AsSpan().CopyTo(new Span<byte>(allocated, bytes.Length));
                *(byte**)argument = allocated; // Caller owns sqlite3_free().
                return Ok;
            }
            // Replacing the OS handle breaks path accounting. Batch-atomic VFS
            // buffering defers physical growth beyond an individual write lease.
            if (operation is Win32SetHandle or BeginAtomicWrite or CommitAtomicWrite or RollbackAtomicWrite)
                return NotFound;
            return state.Original->FileControl(Real(file), operation, argument);
        }
        catch (OverflowException) { return Full; }
        catch { return IoError; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Close(FileHeader* file)
    {
        OpenFile? state = null;
        ICacheStorageWriteLease? lease = null;
        var closeAttempted = false;
        var result = IoError;
        try
        {
            state = State(file);
            var current = FileLength(state, Real(file));
            lease = state.Registration.Reserve(state.Path, current, state.DeleteOnClose ? 0 : current);
            // SQLite releases its sqlite3_file storage after this call, even if
            // xClose fails. Never leave an OS handle pointing into freed storage.
            closeAttempted = true;
            result = state.Original->Close(Real(file));
            if (lease == null) result = IoError;
            else CompletePath(lease, state.Path);
        }
        catch { result = IoError; }
        finally
        {
            if (state != null)
            {
                if (!closeAttempted)
                {
                    try { state.Original->Close(Real(file)); }
                    catch { }
                }
                try { lease?.Dispose(); }
                catch { result = IoError; }
                try { ReleaseFile(file, state); }
                catch { result = IoError; }
            }
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Delete(Vfs* vfs, byte* name, int syncDirectory)
    {
        try
        {
            var state = Context(vfs);
            var path = state.OwnedPath(name);
            var current = DiskLength(path);
            using var lease = state.Reserve(path, current, 0);
            if (lease == null) return Full;
            try { return state.Original->Delete(state.Original, name, syncDirectory); }
            finally { CompletePath(lease, path); }
        }
        catch { return IoError; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ShmMap(FileHeader* file, int page, int pageSize, int extend, void** output)
    {
        if (output != null) *output = null;
        try
        {
            if (page < 0 || pageSize <= 0 || output == null) return IoError;
            var state = State(file);
            var path = state.Path + "-shm";
            var current = DiskLength(path);
            // Unix may map multiple 32KiB regions on large-page systems. Its
            // first SHM open may also initialize a three-byte DMS marker even
            // with extend=0. Windows extends exactly to the requested region.
            var regions = OperatingSystem.IsWindows() ? 1 : Math.Max(1, Environment.SystemPageSize / 32768);
            var requested = extend != 0
                ? checked((((long)page / regions + 1) * regions) * pageSize)
                : OperatingSystem.IsWindows() ? 0 : 3;
            using var lease = state.Registration.Reserve(path, current, Math.Max(current, requested));
            if (lease == null) return Full;
            try { return state.Original->ShmMap(Real(file), page, pageSize, extend, output); }
            finally { CompletePath(lease, path); }
        }
        catch (OverflowException) { if (output != null) *output = null; return Full; }
        catch { if (output != null) *output = null; return IoError; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ShmUnmap(FileHeader* file, int delete)
    {
        OpenFile? state = null;
        ICacheStorageWriteLease? lease = null;
        var attempted = false;
        var result = IoError;
        try
        {
            state = State(file);
            var path = state.Path + "-shm";
            var current = DiskLength(path);
            lease = state.Registration.Reserve(path, current, delete != 0 ? 0 : current);
            attempted = true;
            result = state.Original->ShmUnmap(Real(file), delete);
            if (lease == null) result = IoError;
            else CompletePath(lease, path);
        }
        catch { result = IoError; }
        finally
        {
            // Like xClose, unmapping cannot be deferred after SQLite discards
            // its file. A broken cleanup lease must not leave dangling SHM state.
            if (!attempted && state != null)
            {
                try { state.Original->ShmUnmap(Real(file), delete); }
                catch { }
            }
            try { lease?.Dispose(); }
            catch { result = IoError; }
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Read(FileHeader* f, void* b, int n, long offset)
    { try { return State(f).Original->Read(Real(f), b, n, offset); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Sync(FileHeader* f, int flags)
    { try { return State(f).Original->Sync(Real(f), flags); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FileSize(FileHeader* f, long* size)
    { try { return State(f).Original->FileSize(Real(f), size); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Lock(FileHeader* f, int flags)
    { try { return State(f).Original->Lock(Real(f), flags); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Unlock(FileHeader* f, int flags)
    { try { return State(f).Original->Unlock(Real(f), flags); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CheckReservedLock(FileHeader* f, int* output)
    { try { return State(f).Original->CheckReservedLock(Real(f), output); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SectorSize(FileHeader* f)
    { try { return State(f).Original->SectorSize(Real(f)); } catch { return 4096; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DeviceCharacteristics(FileHeader* f)
    { try { return State(f).Original->DeviceCharacteristics(Real(f)) & ~BatchAtomic; } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ShmLock(FileHeader* f, int offset, int n, int flags)
    { try { return State(f).Original->ShmLock(Real(f), offset, n, flags); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ShmBarrier(FileHeader* f)
    { try { State(f).Original->ShmBarrier(Real(f)); } catch { } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Fetch(FileHeader* f, long offset, int n, void** output)
    { try { return State(f).Original->Fetch(Real(f), offset, n, output); } catch { if (output != null) *output = null; return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Unfetch(FileHeader* f, long offset, void* buffer)
    { try { return State(f).Original->Unfetch(Real(f), offset, buffer); } catch { return IoError; } }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Access(Vfs* v, byte* name, int flags, int* output)
    { try { var c = Context(v); return c.Original->Access(c.Original, name, flags, output); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FullPathname(Vfs* v, byte* name, int n, byte* output)
    { try { var c = Context(v); return c.Original->FullPathname(c.Original, name, n, output); } catch { return CannotOpen; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* DlOpen(Vfs* v, byte* name)
    { try { var c = Context(v); return c.Original->DlOpen == null ? null : c.Original->DlOpen(c.Original, name); } catch { return null; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DlError(Vfs* v, int n, byte* output)
    { try { var c = Context(v); if (c.Original->DlError != null) c.Original->DlError(c.Original, n, output); } catch { if (n > 0 && output != null) *output = 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint DlSym(Vfs* v, void* library, byte* name)
    { try { var c = Context(v); return c.Original->DlSym == null ? 0 : c.Original->DlSym(c.Original, library, name); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DlClose(Vfs* v, void* library)
    { try { var c = Context(v); if (c.Original->DlClose != null) c.Original->DlClose(c.Original, library); } catch { } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Randomness(Vfs* v, int n, byte* output)
    { try { var c = Context(v); return c.Original->Randomness(c.Original, n, output); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Sleep(Vfs* v, int microseconds)
    { try { var c = Context(v); return c.Original->Sleep(c.Original, microseconds); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CurrentTime(Vfs* v, double* output)
    { try { var c = Context(v); return c.Original->CurrentTime(c.Original, output); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastError(Vfs* v, int n, byte* output)
    { try { var c = Context(v); return c.Original->GetLastError == null ? 0 : c.Original->GetLastError(c.Original, n, output); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CurrentTimeInt64(Vfs* v, long* output)
    { try { var c = Context(v); return c.Original->CurrentTimeInt64(c.Original, output); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetSystemCall(Vfs* v, byte* name, nint callback)
    { try { var c = Context(v); return c.Original->SetSystemCall(c.Original, name, callback); } catch { return IoError; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint GetSystemCall(Vfs* v, byte* name)
    { try { var c = Context(v); return c.Original->GetSystemCall(c.Original, name); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte* NextSystemCall(Vfs* v, byte* name)
    { try { var c = Context(v); return c.Original->NextSystemCall(c.Original, name); } catch { return null; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileHeader
    {
        internal IoMethods* Methods;
        internal nint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vfs
    {
        internal int Version, FileSize, MaxPathname;
        internal Vfs* Next;
        internal byte* Name;
        internal nint AppData;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, FileHeader*, int, int*, int> Open;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, int, int> Delete;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, int, int*, int> Access;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, int, byte*, int> FullPathname;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, void*> DlOpen;
        internal delegate* unmanaged[Cdecl]<Vfs*, int, byte*, void> DlError;
        internal delegate* unmanaged[Cdecl]<Vfs*, void*, byte*, nint> DlSym;
        internal delegate* unmanaged[Cdecl]<Vfs*, void*, void> DlClose;
        internal delegate* unmanaged[Cdecl]<Vfs*, int, byte*, int> Randomness;
        internal delegate* unmanaged[Cdecl]<Vfs*, int, int> Sleep;
        internal delegate* unmanaged[Cdecl]<Vfs*, double*, int> CurrentTime;
        internal delegate* unmanaged[Cdecl]<Vfs*, int, byte*, int> GetLastError;
        internal delegate* unmanaged[Cdecl]<Vfs*, long*, int> CurrentTimeInt64;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, nint, int> SetSystemCall;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, nint> GetSystemCall;
        internal delegate* unmanaged[Cdecl]<Vfs*, byte*, byte*> NextSystemCall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoMethods
    {
        internal int Version;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int> Close;
        internal delegate* unmanaged[Cdecl]<FileHeader*, void*, int, long, int> Read;
        internal delegate* unmanaged[Cdecl]<FileHeader*, void*, int, long, int> Write;
        internal delegate* unmanaged[Cdecl]<FileHeader*, long, int> Truncate;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int> Sync;
        internal delegate* unmanaged[Cdecl]<FileHeader*, long*, int> FileSize;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int> Lock;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int> Unlock;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int*, int> CheckReservedLock;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, void*, int> FileControl;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int> SectorSize;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int> DeviceCharacteristics;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int, int, void**, int> ShmMap;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int, int, int> ShmLock;
        internal delegate* unmanaged[Cdecl]<FileHeader*, void> ShmBarrier;
        internal delegate* unmanaged[Cdecl]<FileHeader*, int, int> ShmUnmap;
        internal delegate* unmanaged[Cdecl]<FileHeader*, long, int, void**, int> Fetch;
        internal delegate* unmanaged[Cdecl]<FileHeader*, long, void*, int> Unfetch;
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_vfs_find", CallingConvention = CallingConvention.Cdecl)]
    private static extern Vfs* NativeVfsFind(byte* name);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_vfs_register", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeVfsRegister(Vfs* vfs, int makeDefault);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_create_filename", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte* NativeCreateFilename(byte* database, byte* journal, byte* wal, int parameters, byte** values);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_free_filename", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFreeFilename(byte* name);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_malloc64", CallingConvention = CallingConvention.Cdecl)]
    private static extern void* NativeMalloc64(ulong bytes);
}
