using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Animation;

namespace Deviant.Export.Animation;

public static class AnimationJsonWriter
{
    /// <summary>Writes a portable record document with the complete encoded resource.</summary>
    public static async Task WriteAsync(Stream destination, DecodeResult<AnimationDataDocument> decoded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(decoded);
        cancellationToken.ThrowIfCancellationRequested();
        var document = decoded.Value ?? throw new ArgumentException("Animation decoding did not produce a document.", nameof(decoded));
        foreach (var extent in decoded.Extents)
            EnsureRelativeSource(extent.RelativeSource);
        var export = new
        {
            Format = "deviant-animation-records-v1",
            decoded.Identity,
            decoded.Revision,
            IndexContentId = decoded.Revision.IndexContentId,
            SourceRevisionRef = SourceIdentity.HashFields("deviant-animation-source-revision-v1",
                [decoded.Revision.IndexContentId, decoded.Revision.PhysicalSourceStamp]),
            decoded.Extents,
            decoded.Steps,
            decoded.Diagnostics,
            decoded.UnknownFields,
            document.HeaderWords,
            document.Version14Layout,
            Sections = document.Sections.Select(s => new
            {
                s.Tag, s.Version, s.ResourceOffset, s.BodyOffset, s.BodyLength,
                BodySha256 = Convert.ToHexString(SHA256.HashData(s.Body.Span)).ToLowerInvariant()
            }),
            EncodedResource = Convert.ToBase64String(document.EncodedResource.Span)
        };
        await JsonSerializer.SerializeAsync(destination, export, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureRelativeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || Path.IsPathRooted(source) || source.Contains(':') ||
            source.StartsWith('/') || source.StartsWith('\\') ||
            source.Split(['/', '\\']).Any(s => s is ".." or "." or ""))
            throw new ArgumentException("Export source references must be relative paths.", nameof(source));
    }
}
