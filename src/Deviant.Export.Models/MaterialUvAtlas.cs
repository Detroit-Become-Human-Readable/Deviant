using System.Numerics;
using System.Runtime.InteropServices;
using Deviant.Assets;

namespace Deviant.Export.Models;

public sealed record MaterialUvAtlasResult(int Width, int Height, IReadOnlyList<Vector2> PolygonCornerCoordinates);

/// <summary>Generates export UVs while retaining the source control-point and polygon ordering.</summary>
public static class MaterialUvAtlas
{
    public static MaterialUvAtlasResult Generate(MeshPrimitive primitive, int resolution,
        long nativeMemoryLimit, FbxExportMemoryReservation reserve, CancellationToken token = default,
        bool separateFaces = false)
    {
        ArgumentNullException.ThrowIfNull(primitive); ArgumentNullException.ThrowIfNull(reserve);
        token.ThrowIfCancellationRequested();
        if (primitive.Topology != PrimitiveTopology.Triangles || primitive.Positions.Count is < 3 or > 20_000_000 ||
            primitive.Indices.Count is < 3 or > 15_000_000 || primitive.Indices.Count % 3 != 0 ||
            resolution is < 16 or > 16384 || nativeMemoryLimit is < 1024 * 1024 or > 512L * 1024 * 1024 ||
            primitive.Indices.Any(index => index >= primitive.Positions.Count))
            throw new InvalidDataException("The mesh or atlas limits are invalid.");
        int inputVertices = separateFaces ? primitive.Indices.Count : primitive.Positions.Count;
        using var memory = reserve(checked(nativeMemoryLimit + inputVertices * 40L + primitive.Indices.Count * 20L + 4096), "material UV atlas");
        var positions = new float[checked(inputVertices * 3)];
        var seams = new float[checked(inputVertices * 2)];
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var point in primitive.Positions)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new InvalidDataException("Atlas positions must be finite.");
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        double extent = Math.Max((double)maximum.X - minimum.X, Math.Max((double)maximum.Y - minimum.Y, (double)maximum.Z - minimum.Z));
        if (extent <= 0 || !double.IsFinite(extent)) throw new InvalidDataException("The atlas mesh has no finite spatial extent.");
        // xatlas uses an absolute area tolerance. Condition only its working positions;
        // exported control points and source attribute interpolation retain their original values.
        double scale = 1024 / extent;
        for (int index = 0; index < inputVertices; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            var point = primitive.Positions[separateFaces ? checked((int)primitive.Indices[index]) : index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new InvalidDataException("Atlas positions must be finite.");
            positions[index * 3] = (float)(((double)point.X - minimum.X) * scale);
            positions[index * 3 + 1] = (float)(((double)point.Y - minimum.Y) * scale);
            positions[index * 3 + 2] = (float)(((double)point.Z - minimum.Z) * scale);
            // These labels only prevent welding distinct source control points at
            // coincident positions. They are not source or exported UV coordinates.
            seams[index * 2] = index & 0xffff; seams[index * 2 + 1] = index >> 16;
        }
        var indices = separateFaces ? Enumerable.Range(0, primitive.Indices.Count).Select(i => (uint)i).ToArray() : primitive.Indices.ToArray();
        var positionsPin = GCHandle.Alloc(positions, GCHandleType.Pinned);
        var indicesPin = GCHandle.Alloc(indices, GCHandleType.Pinned);
        var seamsPin = GCHandle.Alloc(seams, GCHandleType.Pinned);
        IntPtr result = IntPtr.Zero;
        ProgressCallback progress = (_, _, _) => token.IsCancellationRequested ? 0 : 1;
        try
        {
            var input = new NativeInput { Positions = positionsPin.AddrOfPinnedObject(), VertexCount = (ulong)inputVertices,
                Normals = IntPtr.Zero, NormalCount = 0, SourceUvs = seamsPin.AddrOfPinnedObject(), SourceUvCount = (ulong)inputVertices,
                Indices = indicesPin.AddrOfPinnedObject(), CornerCount = (ulong)indices.Length };
            var options = new NativeOptions { Resolution = (uint)resolution, Padding = 4, MemoryLimit = (ulong)nativeMemoryLimit };
            var error = new byte[1024];
            int status = GenerateNative(in input, in options, progress, IntPtr.Zero, out result, error, (ulong)error.Length);
            GC.KeepAlive(progress); token.ThrowIfCancellationRequested();
            if (status != 0)
            {
                int end = Array.IndexOf(error, (byte)0); if (end < 0) end = error.Length;
                throw new InvalidDataException($"UV atlas status {status}: {System.Text.Encoding.UTF8.GetString(error, 0, end)}");
            }
            if (result == IntPtr.Zero) throw new InvalidDataException("The atlas returned no result.");
            var output = Marshal.PtrToStructure<NativeResult>(result);
            if (output.CornerCount != (ulong)indices.Length || output.Width is < 1 or > 16384 ||
                output.Height is < 1 or > 16384 || output.Coordinates == IntPtr.Zero)
                throw new InvalidDataException("The atlas result does not match the selected mesh.");
            var flat = new float[checked(indices.Length * 2)]; Marshal.Copy(output.Coordinates, flat, 0, flat.Length);
            var corners = new Vector2[indices.Length];
            for (int corner = 0; corner < corners.Length; corner++)
            {
                if ((corner & 1023) == 0) token.ThrowIfCancellationRequested();
                var uv = new Vector2(flat[corner * 2], flat[corner * 2 + 1]);
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
                    throw new InvalidDataException("The atlas returned invalid normalized coordinates.");
                corners[corner] = uv;
            }
            return new(checked((int)output.Width), checked((int)output.Height), Array.AsReadOnly(corners));
        }
        catch (DllNotFoundException error) { throw new InvalidDataException("The UV atlas library is unavailable.", error); }
        finally
        {
            if (result != IntPtr.Zero) FreeNative(result);
            seamsPin.Free(); indicesPin.Free(); positionsPin.Free(); GC.KeepAlive(progress);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgressCallback(int category, int progress, IntPtr user);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput
    {
        public IntPtr Positions; public ulong VertexCount; public IntPtr Normals; public ulong NormalCount;
        public IntPtr SourceUvs; public ulong SourceUvCount; public IntPtr Indices; public ulong CornerCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeOptions { public uint Resolution; public uint Padding; public ulong MemoryLimit; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeResult { public ulong CornerCount; public uint Width; public uint Height; public IntPtr Coordinates; }
    [DllImport("Deviant.Export.Models.Native", EntryPoint = "deviant_xatlas_generate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GenerateNative(in NativeInput input, in NativeOptions options, ProgressCallback progress,
        IntPtr user, out IntPtr result, [Out] byte[] error, ulong errorCapacity);
    [DllImport("Deviant.Export.Models.Native", EntryPoint = "deviant_xatlas_free_result", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeNative(IntPtr result);
}
