using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;

[assembly: InternalsVisibleTo("Deviant.Desktop.Tests")]

namespace Deviant.Desktop;

internal readonly record struct TextureContentKey(SourceRevision Revision, string RgbaSha256,
    int Width, int Height, bool Srgb)
{
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
