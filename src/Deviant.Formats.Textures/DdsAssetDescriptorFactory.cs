using Deviant.Assets;

namespace Deviant.Formats.Textures;

public static class DdsAssetDescriptorFactory
{
    public static AssetDescriptor Create(DdsTextureDocument document, ResourceIdentity identity, SourceRevision revision,
        string label, IReadOnlyList<SourceExtent> extents, string? groupKey = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(extents);
        if (!StringComparer.Ordinal.Equals(identity.IndexContentId, revision.IndexContentId))
            throw new ArgumentException("The texture identity and source revision refer to different indexes.", nameof(revision));
        if (document.Identity is not null && document.Identity != identity)
            throw new ArgumentException("The texture document was read from a different resource identity.", nameof(identity));
        if (document.Revision is not null && document.Revision != revision)
            throw new ArgumentException("The texture document was read from a different source revision.", nameof(revision));

        var info = document.Info;
        var capabilities = AssetCapabilities.Original | AssetCapabilities.Dds;
        if (info.PixelDecodeSupported)
        {
            capabilities |= AssetCapabilities.Preview;
            if (!info.IsHdr) capabilities |= AssetCapabilities.Png;
        }
        var names = new[] { new NameEvidence(label, NameOrigin.Description, info.Format.ToString()) };
        return new AssetDescriptor(identity, revision, "Texture", label, groupKey,
            AssetAvailability.Available, AssetCompleteness.Complete, capabilities, names, extents,
            Reason: !info.PixelDecodeSupported ? $"{info.FormatName} pixels are not decoded by this build."
                : info.IsHdr ? "HDR pixels are retained as floating-point data." : null,
            SortKey: label)
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["width"] = info.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["height"] = info.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mipCount"] = info.MipCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pixelFormat"] = info.FormatName,
                ["isSrgb"] = info.IsSrgb ? "true" : "false",
                ["isHdr"] = info.IsHdr ? "true" : "false",
                ["arrayLayerCount"] = info.ArrayLayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isCubeMap"] = info.IsCubeMap ? "true" : "false",
                ["cubeFaces"] = string.Join(",", info.CubeFaces)
            }
        };
    }
}
