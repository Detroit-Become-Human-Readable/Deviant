using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Deviant.Formats.Textures;

namespace Deviant.Export.Textures;

public sealed record TextureExportResult(string RelativePath, long Length, string Sha256, AssetExportFormat Format,
    string InputPayloadSha256, ResourceIdentity? Identity, SourceRevision? Revision, int? SelectedMip, string PixelFormat,
    bool SourceIsHdr, bool OutputIsHdr, bool SourceIsSrgb, bool OutputIsSrgb, string Precision, string Losses,
    TextureDisplayTransform? DisplayTransform)
{
    public TextureSubresourceSelection? SelectedSubresource { get; init; }
}

/// <summary>Writes validated DDS bytes or decoded RGBA pixels to a safe destination.</summary>
public sealed class TextureExportService
{
    public async Task<TextureExportResult> ExportAsync(DdsTextureDocument document, TextureSurface? decoded,
        AssetExportFormat format, string sourceRoot, string outputRoot, string relativePath,
        bool overwrite = false, CancellationToken cancellationToken = default, SourceRevision? expectedRevision = null,
        Func<CancellationToken, Task>? preCommitValidation = null)
    {
        ValidateEncoding(document, decoded, format, expectedRevision, preCommitValidation);

        var output = Path.GetFullPath(outputRoot);
        OutputSafety.ValidateOutputRoot(output, sourceRoot);
        var destination = OutputSafety.ResolveOutputPath(output, relativePath);
        if (File.Exists(destination) && !overwrite)
            throw new ArchiveException("DestinationExists", "The selected output file already exists.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        using var rootPin = OutputSafety.PinDirectory(output);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        using var parentPin = OutputSafety.PinDirectory(parent);
        OutputSafety.ValidateFilePath(destination, sourceRoot);

        var temporaryPath = OutputSafety.ResolveOutputPath(output,
            relativePath + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var temporaryCreated = false;
        try
        {
            TextureExportResult result;
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryCreated = true;
                result = await EncodeCoreAsync(document, decoded, format, file, relativePath, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            if (preCommitValidation is not null)
                await preCommitValidation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OutputSafety.ValidateFilePath(destination, sourceRoot);
            if (File.Exists(destination) && !overwrite)
                throw new ArchiveException("DestinationExists", "The selected output file already exists.");
            File.Move(temporaryPath, destination, overwrite);
            temporaryCreated = false;
            return result;
        }
        finally
        {
            if (temporaryCreated)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Encodes into a caller-owned stream; the caller publishes the output after this method succeeds.</summary>
    public async Task<TextureExportResult> EncodeAsync(DdsTextureDocument document, TextureSurface? decoded,
        AssetExportFormat format, Stream output, string relativePath, CancellationToken cancellationToken = default,
        SourceRevision? expectedRevision = null, Func<CancellationToken, Task>? preCommitValidation = null)
    {
        ValidateEncoding(document, decoded, format, expectedRevision, preCommitValidation);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));
        var result = await EncodeCoreAsync(document, decoded, format, output, relativePath, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (preCommitValidation is not null)
            await preCommitValidation(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task<TextureExportResult> EncodeCoreAsync(DdsTextureDocument document, TextureSurface? decoded,
        AssetExportFormat format, Stream output, string relativePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashing = new HashingWriteStream(output, digest);
        switch (format)
        {
            case AssetExportFormat.Original:
            case AssetExportFormat.Dds:
                await WriteBytesAsync(hashing, document.OriginalBytes, token).ConfigureAwait(false);
                break;
            case AssetExportFormat.Png:
                PngTextureWriter.Write(hashing, decoded!, decoded!.IsSrgb, token);
                break;
        }
        token.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        var hdrOutput = (format is AssetExportFormat.Dds or AssetExportFormat.Original) && document.Info.IsHdr;
        var losses = format == AssetExportFormat.Png ? DescribePngLosses(document, decoded!) : "None; original encoded bytes copied exactly.";
        return new TextureExportResult(relativePath, hashing.BytesWritten, hash, format, document.ContentSha256,
            document.Identity, document.Revision,
            format == AssetExportFormat.Png ? decoded!.MipIndex : null, document.Info.FormatName, document.Info.IsHdr, hdrOutput,
            document.Info.IsSrgb, format == AssetExportFormat.Png ? decoded!.IsSrgb : document.Info.IsSrgb,
            format == AssetExportFormat.Png ? "RGBA8" : "Source encoded", losses,
            format == AssetExportFormat.Png ? decoded!.DisplayTransform : null)
        {
            SelectedSubresource = format == AssetExportFormat.Png ? decoded!.Subresource : null
        };
    }

    private static void ValidateEncoding(DdsTextureDocument document, TextureSurface? decoded, AssetExportFormat format,
        SourceRevision? expectedRevision, Func<CancellationToken, Task>? preCommitValidation)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (format is not (AssetExportFormat.Original or AssetExportFormat.Dds or AssetExportFormat.Png))
            throw new ArchiveException("UnsupportedFormat", "Texture export supports Original, DDS, and PNG formats.");
        if (document.Identity is not null)
        {
            if (expectedRevision is null)
                throw new ArchiveException("SourceRevisionRequired", "Archive-backed texture export requires the selected source revision.");
            if (!StringComparer.Ordinal.Equals(expectedRevision.IndexContentId, document.Identity.IndexContentId) ||
                (document.Revision is not null && expectedRevision != document.Revision))
                throw new ArchiveException("SourceChanged", "The texture document belongs to a different source revision.");
            if (preCommitValidation is null)
                throw new ArchiveException("SourceValidationRequired", "Archive-backed texture export requires a pre-commit source validation callback.");
        }
        else if (document.Revision is not null || expectedRevision is not null)
            throw new ArchiveException("SourceRevisionUnavailable", "The texture document does not carry the requested source revision.");
        if (format == AssetExportFormat.Png)
        {
            if (decoded is null) throw new ArchiveException("PixelsUnavailable", "A decoded texture surface is required for PNG export.");
            ValidateSurface(document, decoded);
        }
    }

    private static void ValidateSurface(DdsTextureDocument document, TextureSurface surface)
    {
        if (!StringComparer.Ordinal.Equals(surface.SourcePayloadSha256, document.ContentSha256) ||
            surface.Identity != document.Identity || surface.Revision != document.Revision ||
            surface.SourceFormat != document.Info.Format || surface.SourceFormatName != document.Info.FormatName ||
            surface.SourceIsHdr != document.Info.IsHdr || (uint)surface.MipIndex >= (uint)document.MipLevels.Count)
            throw new ArchiveException("TextureSurfaceMismatch", "The decoded surface does not belong to this texture document or source revision.");
        var selectedMip = document.Subresources.FirstOrDefault(extent => extent.Selection == surface.Subresource);
        if (selectedMip is null)
            throw new ArchiveException("TextureSurfaceMismatch", "The decoded surface selects a subresource that is absent from the DDS file.");
        if (surface.Subresource.MipIndex != surface.MipIndex)
            throw new ArchiveException("TextureSurfaceMismatch", "The decoded surface mip selection is inconsistent.");
        if (surface.Width != selectedMip.Width || surface.Height != selectedMip.Height || surface.IsHdr)
            throw new ArchiveException("TextureSurfaceMismatch", "The PNG surface does not match its selected mip or contains unconverted HDR pixels.");
        if (surface.DisplayTransform is { EncodedSrgb: true } && !surface.IsSrgb)
            throw new ArchiveException("TextureSurfaceMismatch", "The display transform encoding metadata does not match its pixel surface.");
    }

    private static string DescribePngLosses(DdsTextureDocument document, TextureSurface surface)
    {
        if (surface.DisplayTransform is { } transform)
            return $"Tone mapped HDR with {transform.Operator} at {transform.ExposureStops.ToString(System.Globalization.CultureInfo.InvariantCulture)} EV; quantized to 8-bit sRGB RGBA.";
        return surface.SourceFormat is TextureStorageFormat.Bc1 or TextureStorageFormat.Bc2 or TextureStorageFormat.Bc3 or
            TextureStorageFormat.Bc4 or TextureStorageFormat.Bc5 or TextureStorageFormat.Bc6Unsigned or
            TextureStorageFormat.Bc6Signed or TextureStorageFormat.Bc7
            ? $"Decoded {document.Info.FormatName} blocks to 8-bit RGBA."
            : "Converted the source pixel layout to 8-bit RGBA.";
    }

    private static async Task WriteBytesAsync(Stream destination, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        const int bufferSize = 128 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += bufferSize)
        {
            token.ThrowIfCancellationRequested();
            await destination.WriteAsync(bytes.Slice(offset, Math.Min(bufferSize, bytes.Length - offset)), token).ConfigureAwait(false);
        }
    }

    private sealed class HashingWriteStream(Stream destination, IncrementalHash hash) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            var next = checked(BytesWritten + count);
            hash.AppendData(buffer.AsSpan(offset, count));
            destination.Write(buffer, offset, count);
            BytesWritten = next;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var next = checked(BytesWritten + buffer.Length);
            hash.AppendData(buffer);
            destination.Write(buffer);
            BytesWritten = next;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = checked(BytesWritten + buffer.Length);
            hash.AppendData(buffer.Span);
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten = next;
        }
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
