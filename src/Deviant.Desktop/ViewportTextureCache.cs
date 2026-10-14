using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;

[assembly: InternalsVisibleTo("Deviant.Desktop.Tests")]

namespace Deviant.Desktop;

internal readonly record struct TextureContentKey(SourceRevision Revision, string RgbaSha256,
    int Width, int Height, bool Srgb)
{
    public bool IsCube { get; init; }
    public int MipCount { get; init; } = 1;
    public TextureStorageFormat? EncodedFormat { get; init; }

    internal static void ValidateEncoded2D(DdsTextureDocument document, SourceRevision revision,
        TextureSubresourceSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(revision);
        if (document.Identity is null || document.Identity.IndexContentId != revision.IndexContentId || document.Revision != revision)
            throw new InvalidDataException("The texture belongs to another source.");
        var info = document.Info;
        selection ??= document.DefaultSelection;
        if (info.IsCubeMap || info.CubeFaces.Count != 0 || info.ArrayLayerCount != 1 || info.MipCount != 1 || info.IsHdr ||
            info.TrailingLength != 0 || document.Subresources.Count != 1 ||
            document.OriginalBytes.Length != checked(info.HeaderLength + info.EncodedLength) ||
            info.Format is not (TextureStorageFormat.Bc1 or TextureStorageFormat.Bc3 or TextureStorageFormat.Bc4 or TextureStorageFormat.Bc5 or TextureStorageFormat.Luminance8) ||
            info.Format == TextureStorageFormat.Luminance8 && info.FormatName != "R8_UNORM" ||
            selection != new TextureSubresourceSelection())
            throw new InvalidDataException("The encoded texture layout is unavailable.");
        var hash = Convert.ToHexString(SHA256.HashData(document.OriginalBytes.Span));
        if (!string.Equals(hash, document.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The encoded texture content changed.");
        var parsed = new DdsTextureReader().Read(document.OriginalBytes, document.Identity, document.Revision);
        if ((parsed.Info with { CubeFaces = info.CubeFaces }) != info || !parsed.MipLevels.SequenceEqual(document.MipLevels) ||
            !parsed.Subresources.SequenceEqual(document.Subresources) ||
            !TextureSurface.CanRetainEncodedDds(document, selection))
            throw new InvalidDataException("The encoded texture metadata or sampling differs from its DDS bytes.");
    }

    public static TextureContentKey CreateEncoded(RenderTexture texture, SourceRevision revision, bool srgb)
    {
        var surface = texture.Surface;
        var document = surface.EncodedDds ?? throw new InvalidDataException("The encoded texture is unavailable.");
        ValidateEncoded2D(document, revision, surface.EncodedDdsSelection);
        if (surface.Revision != revision || surface.Width != document.Info.Width || surface.Height != document.Info.Height ||
            surface.IsSrgb != document.Info.IsSrgb || surface.SourceFormat != document.Info.Format ||
            surface.DisplayTransform is not null || surface.IsHdr || surface.SourceIsHdr || texture.RgbaSha256.Length != 64 ||
            !texture.RgbaSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The encoded texture surface is invalid.");
        if (srgb && document.Info.Format is TextureStorageFormat.Bc4 or TextureStorageFormat.Bc5 or TextureStorageFormat.Luminance8)
            throw new InvalidDataException("The scalar texture requires linear sampling.");
        return new(revision, document.ContentSha256.ToUpperInvariant(), surface.Width, surface.Height, srgb)
        { EncodedFormat = document.Info.Format, MipCount = document.Info.MipCount };
    }

    internal static void ValidateCube(DdsTextureDocument document, SourceRevision revision)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(revision);
        if (document.Identity is null || document.Identity.IndexContentId != revision.IndexContentId ||
            document.Revision != revision)
            throw new InvalidDataException("The cube texture belongs to another source.");
        var info = document.Info;
        if (!info.IsCubeMap ||
            info.ArrayLayerCount != 1 || info.CubeFaces.Count != 6 ||
            info.CubeFaces.Distinct().Count() != 6 || !info.IsHdr || info.IsSrgb ||
            info.Format != TextureStorageFormat.Bc6Unsigned || info.TrailingLength != 0 ||
            document.Subresources.Count != checked(info.MipCount * 6) ||
            document.OriginalBytes.Length != checked(info.HeaderLength + info.EncodedLength))
            throw new InvalidDataException("The HDR cube texture layout is unavailable.");
        string hash = Convert.ToHexString(SHA256.HashData(document.OriginalBytes.Span));
        if (!string.Equals(hash, document.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cube texture content changed.");
        var expectedFaces = Enum.GetValues<CubeFace>();
        var firstFaceMips = document.Subresources.Where(extent => extent.Selection.ArrayLayer == 0 &&
            extent.Selection.Face == CubeFace.PositiveX).Select(extent => new TextureMipLevel(
                extent.Selection.MipIndex, extent.Width, extent.Height, extent.SourceOffset, extent.EncodedLength));
        if (!info.CubeFaces.SequenceEqual(expectedFaces) || !document.MipLevels.SequenceEqual(firstFaceMips))
            throw new InvalidDataException("The cube texture metadata differs from its DDS bytes.");
    }

    public static TextureContentKey CreateCube(DdsTextureDocument document, SourceRevision revision)
    {
        ValidateCube(document, revision);
        return new(revision, document.ContentSha256.ToUpperInvariant(), document.Info.Width, document.Info.Height, false)
        { IsCube = true, MipCount = document.Info.MipCount, EncodedFormat = document.Info.Format };
    }

    public static TextureContentKey Create(RenderTexture texture, SourceRevision revision, bool srgb)
    {
        var surface = texture.Surface;
        if (surface.Width <= 0 || surface.Height <= 0 || (long)surface.Width * surface.Height * 4 != surface.Rgba8.Length)
            throw new InvalidDataException("The texture pixel layout is invalid.");
        string hash = Convert.ToHexString(SHA256.HashData(surface.Rgba8.Span));
        if (!string.Equals(hash, texture.RgbaSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The texture pixel hash is invalid.");
        return new(revision, hash, surface.Width, surface.Height, srgb);
    }
}

/// <summary>Retains one upload for each decoded pixel layout until its last material closes.</summary>
internal sealed class SharedTexturePool<TResource> where TResource : IDisposable
{
    private readonly Dictionary<TextureContentKey, Entry> entries = [];
    public int Count => entries.Count;

    public Lease Acquire(TextureContentKey key, Func<TResource> create)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new(create());
            entries.Add(key, entry);
        }
        checked { entry.References++; }
        return new(this, key, entry.Resource);
    }

    private void Release(TextureContentKey key)
    {
        if (entries.TryGetValue(key, out var entry) && --entry.References == 0)
        {
            entries.Remove(key);
            entry.Resource.Dispose();
        }
    }

    private sealed class Entry(TResource resource)
    { public TResource Resource { get; } = resource; public int References; }

    public sealed class Lease(SharedTexturePool<TResource> owner, TextureContentKey key, TResource resource) : IDisposable
    {
        private SharedTexturePool<TResource>? current = owner;
        public TResource Resource { get; } = resource;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release(key);
    }
}
