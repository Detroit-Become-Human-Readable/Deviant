using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeModelSession
{
    private readonly NativeModelSurfaceCache<SharedSurfacePixels> surfaceCache = new();

    private static string CreateSurfacePreparationKey(NativeTextureSource source, int maximumPreviewDimension)
    {
        var plan = NativeTextureDecoder.PlanPreview(source, maximumPreviewDimension);
        var metadata = source.Metadata;
        var key = new StringBuilder()
            .Append(source.Revision.IndexContentId).Append('\n')
            .Append(source.Revision.PhysicalSourceStamp).Append('\n')
            .Append(NativeTextureDecoder.PreviewVersion).Append('\n')
            .Append("prepared-surface-v2").Append('\n')
            .Append(maximumPreviewDimension).Append('\n')
            .Append(metadata.FirstByte).Append(':').Append(metadata.SerializedSelector).Append(':')
            .Append(metadata.PostSelectorBoolean).Append(':').Append(metadata.Mip0Width).Append('x')
            .Append(metadata.Mip0Height).Append(':').Append(metadata.SizeGroupSelector).Append(':')
            .Append(metadata.PostSelectorByte0).Append(':').Append(metadata.PostSelectorByte1).Append(':')
            .Append(NativeTextureDecoder.IsSrgb(source)).Append('\n')
            .Append(plan.MipIndex).Append(':').Append(plan.Width).Append('x').Append(plan.Height).Append(':')
            .Append(plan.IsCubeMap).Append(':').Append(plan.IsConverted).Append('\n');
        foreach (var identity in plan.RequiredPayloadIdentities)
        {
            var payload = source.Payloads.FirstOrDefault(candidate => candidate.Identity == identity)
                ?? throw new TextureFormatException("UnsupportedNativeMipPayload", "The selected mip payload is unavailable.");
            key.Append(payload.Sha256).Append(':').Append(payload.Extent.RelativeSource).Append(':')
                .Append(payload.Extent.Offset).Append(':').Append(payload.Extent.Length).Append(':')
                .Append(payload.Extent.PayloadSha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()))).ToLowerInvariant();
    }

    private async Task<SharedSurfacePixels> DecodeSharedSurfaceAsync(NativeTextureSource source,
        NativeContainerLease container, int maximumPreviewDimension, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var preparationKey = surfaceDiskCache is null ? null : CreateSurfacePreparationKey(source, maximumPreviewDimension);
        if (surfaceDiskCache is not null)
        {
            var prepared = await PreparedTexturePixelSource.TryAcquireAsync(surfaceDiskCache, preparationKey!, MemoryBudget, token).ConfigureAwait(false);
            if (prepared is not null)
            {
                try
                {
                    if (prepared.Surface.Revision != source.Revision)
                        throw new TextureFormatException("PreparedTextureChanged", "The prepared texture belongs to another source revision.");
                    container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
                    return FromPrepared(prepared);
                }
                catch { prepared.Dispose(); throw; }
            }
        }
        var nativeDecoder = CreateSourceLoadedTextureDecoder(source, maximumPreviewDimension, token);
        using var decodeScratch = MemoryBudget.Reserve(
            nativeDecoder.EstimatePreviewWorkingSetBytes(source, maximumPreviewDimension, token), "texture decoding");
        var decoded = nativeDecoder.ReadPreview(source, maximumPreviewDimension, token);
        var document = decoded.Document;
        decodeScratch.ReduceTo(document.OriginalBytes.Length);
        var pixelDecoder = new DdsTextureDecoder(MemoryBudget.MaximumBytes);
        var mip = document.MipLevels.FirstOrDefault(level => level.Width <= maximumPreviewDimension && level.Height <= maximumPreviewDimension)
            ?? document.MipLevels[^1];
        var selection = document.DefaultSelection with { MipIndex = mip.Index };
        var resize = mip.Width > maximumPreviewDimension || mip.Height > maximumPreviewDimension;
        var resizeBytes = resize ? checked((long)maximumPreviewDimension * maximumPreviewDimension * 4) : 0;
        var pixelMemory = MemoryBudget.Reserve(
            checked(pixelDecoder.EstimateWorkingSetBytes(document, selection) + resizeBytes + 4096), "texture pixels");
        AssetMemoryBudget.Reservation? encodedMemory = null;
        try
        {
            var pixels = await pixelDecoder.DecodeSubresourceAsync(document, selection, token).ConfigureAwait(false);
            if (pixels.IsHdr || pixels.Rgba8.Length != checked(pixels.Width * pixels.Height * 4))
                throw new TextureFormatException("MaterialSurfaceFormat", "The material texture surface is unavailable.");
            if (resize)
            {
                token.ThrowIfCancellationRequested();
                pixels = pixelDecoder.CreateThumbnail(pixels, maximumPreviewDimension);
            }
            var nativeMip = decoded.SupportedMips.First().MipIndex;
            var encoded = !resize && document.OriginalBytes.Length < pixels.Rgba8.Length &&
                TextureSurface.CanRetainEncodedDds(document, selection, token) ? document : null;
            var rgbaSha256 = Convert.ToHexString(SHA256.HashData(pixels.Rgba8.Span)).ToLowerInvariant();
            encodedMemory = encoded is not null
                ? MemoryBudget.Reserve(encoded.OriginalBytes.Length + 4096L, "texture encoded surfaces") : null;
            if (encoded is null) pixelMemory.ReduceTo(pixels.Rgba8.Length + 4096L);
            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();
            using (MemoryBudget.Reserve(NativeTextureSession.MaximumResourceBytes, "texture validation"))
                await textures.RevalidateAsync(source, token).ConfigureAwait(false);
            container.EnsureUnchanged();
            if (encoded is null && surfaceDiskCache is not null)
            {
                var resident = TextureSurface.RestoreRgba8(pixels.Width, pixels.Height, pixels.Rgba8,
                    pixels.SourceIsHdr, pixels.IsSrgb, nativeMip, pixels.SourceFormat, pixels.SourceFormatName,
                    pixels.SourcePayloadSha256, pixels.Subresource with { MipIndex = nativeMip },
                    source.Metadata.Identity, source.Revision, pixels.DisplayTransform);
                var prepared = await PreparedTexturePixelSource.StoreAsync(surfaceDiskCache, preparationKey!, resident,
                    rgbaSha256, MemoryBudget, resize, token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return FromPrepared(prepared); }
                catch { prepared.Dispose(); throw; }
            }
            try
            {
                var shared = new SharedSurfacePixels(pixels.Width, pixels.Height,
                    encoded is null ? pixels.Rgba8 : ReadOnlyMemory<byte>.Empty,
                    pixels.SourceIsHdr, pixels.IsSrgb, nativeMip, pixels.SourceFormat, pixels.SourceFormatName,
                    pixels.SourcePayloadSha256, pixels.Subresource with { MipIndex = nativeMip }, pixels.DisplayTransform,
                    resize, rgbaSha256, encodedMemory ?? pixelMemory, encoded, encoded is null ? null : selection);
                if (encoded is null) pixelMemory = null!;
                encodedMemory = null;
                return shared;
            }
            finally { encodedMemory?.Dispose(); }
        }
        finally { encodedMemory?.Dispose(); pixelMemory?.Dispose(); }
    }

    private static SharedSurfacePixels FromPrepared(PreparedTexturePixelSource prepared)
    {
        var surface = prepared.Surface;
        return new(surface.Width, surface.Height, ReadOnlyMemory<byte>.Empty, surface.SourceIsHdr,
            surface.IsSrgb, surface.MipIndex, surface.SourceFormat, surface.SourceFormatName,
            surface.SourcePayloadSha256, surface.Subresource, surface.DisplayTransform,
            prepared.PreviewWasResized, prepared.RgbaSha256, prepared, pixelSource: prepared);
    }

    private sealed class SharedSurfacePixels(int width, int height, ReadOnlyMemory<byte> rgba8,
        bool sourceIsHdr, bool isSrgb, int mipIndex, TextureStorageFormat sourceFormat, string sourceFormatName,
        string sourcePayloadSha256, TextureSubresourceSelection subresource, TextureDisplayTransform? displayTransform,
        bool previewWasResized, string rgbaSha256, IDisposable reservation,
        DdsTextureDocument? encodedDds = null, TextureSubresourceSelection? encodedSelection = null,
        ITexturePixelSource? pixelSource = null) : IDisposable
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public ReadOnlyMemory<byte> Rgba8 { get; } = rgba8;
        public ITexturePixelSource? PixelSource { get; } = pixelSource;
        public DdsTextureDocument? EncodedDds { get; } = encodedDds;
        public TextureSubresourceSelection? EncodedSelection { get; } = encodedSelection;
        public bool SourceIsHdr { get; } = sourceIsHdr;
        public bool IsSrgb { get; } = isSrgb;
        public int MipIndex { get; } = mipIndex;
        public TextureStorageFormat SourceFormat { get; } = sourceFormat;
        public string SourceFormatName { get; } = sourceFormatName;
        public string SourcePayloadSha256 { get; } = sourcePayloadSha256;
        public TextureSubresourceSelection Subresource { get; } = subresource;
        public TextureDisplayTransform? DisplayTransform { get; } = displayTransform;
        public bool PreviewWasResized { get; } = previewWasResized;
        public string RgbaSha256 { get; } = rgbaSha256;
        public void Dispose() => reservation.Dispose();
    }
}

internal sealed class NativeModelSurfaceCache<T> where T : class, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal async Task<Lease> AcquireAsync(string key, Func<CancellationToken, Task<T>> factory,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            if (entries.TryGetValue(key, out entry!)) entry.References++;
            else
            {
                entry = new Entry(key) { References = 1 };
                entries.Add(key, entry);
                try
                {
                    entry.Work = factory(entry.Cancellation.Token);
                    _ = entry.Work.ContinueWith(_ => Complete(entry), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch
                {
                    entries.Remove(key);
                    entry.References = 0;
                    entry.Cancellation.Dispose();
                    throw;
                }
            }
        }

        try
        {
            var value = await entry.Work.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new Lease(this, entry, value);
        }
        catch
        {
            var drainProducer = Release(entry);
            if (drainProducer)
                try { await entry.Work.ConfigureAwait(false); }
                catch { }
            throw;
        }
    }

    private void Complete(Entry entry)
    {
        T? dispose = null;
        bool disposeCancellation;
        lock (gate)
        {
            if (!entry.Work.IsCompletedSuccessfully)
            {
                if (entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    entries.Remove(entry.Key);
                _ = entry.Work.Exception;
            }
            dispose = TakeValueForDisposal(entry);
            disposeCancellation = TakeCancellationForDisposal(entry);
        }
        dispose?.Dispose();
        if (disposeCancellation) entry.Cancellation.Dispose();
    }

    private bool Release(Entry entry)
    {
        T? dispose;
        bool cancel;
        bool disposeCancellation;
        lock (gate)
        {
            if (entry.References == 0) return false;
            entry.References--;
            if (entry.References == 0)
            {
                if (entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    entries.Remove(entry.Key);
            }
            cancel = entry.References == 0 && !entry.Work.IsCompleted;
            dispose = TakeValueForDisposal(entry);
            disposeCancellation = TakeCancellationForDisposal(entry);
        }
        if (cancel)
        {
            try { entry.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        dispose?.Dispose();
        if (disposeCancellation) entry.Cancellation.Dispose();
        return cancel;
    }

    private static T? TakeValueForDisposal(Entry entry)
    {
        if (entry.References != 0 || !entry.Work.IsCompletedSuccessfully || entry.ValueDisposed) return null;
        entry.ValueDisposed = true;
        return entry.Work.Result;
    }

    private static bool TakeCancellationForDisposal(Entry entry)
    {
        if (entry.References != 0 || !entry.Work.IsCompleted || entry.CancellationDisposed) return false;
        entry.CancellationDisposed = true;
        return true;
    }

    internal sealed class Lease : IDisposable
    {
        private NativeModelSurfaceCache<T>? owner;
        private readonly Entry entry;
        internal Lease(NativeModelSurfaceCache<T> owner, Entry entry, T value)
        { this.owner = owner; this.entry = entry; Value = value; }
        public T Value { get; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entry);
    }

    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<T> Work { get; set; } = null!;
        public int References { get; set; }
        public bool ValueDisposed { get; set; }
        public bool CancellationDisposed { get; set; }
    }
}
