using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Deviant.Core;
using Deviant.Formats.Textures;

if (args.Length == 0 || (args.Length - 1) % 2 != 0)
{
    Console.Error.WriteLine("Usage: Deviant.TextureProbe <dds-file> [--layer N] [--face PositiveX|NegativeX|PositiveY|NegativeY|PositiveZ|NegativeZ] [--mip N]");
    return 2;
}

int? requestedLayer = null;
int? requestedMip = null;
CubeFace? requestedFace = null;
var faceSpecified = false;
for (var index = 1; index < args.Length; index += 2)
{
    var key = args[index];
    var value = args[index + 1];
    switch (key)
    {
        case "--layer" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var layer) && layer >= 0:
            requestedLayer = layer;
            break;
        case "--mip" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mip) && mip >= 0:
            requestedMip = mip;
            break;
        case "--face":
            faceSpecified = true;
            requestedFace = string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                ? null
                : Enum.TryParse<CubeFace>(value, ignoreCase: true, out var parsedFace)
                    ? parsedFace
                    : throw new ArgumentException("The cube face name is invalid.");
            break;
        default:
            Console.Error.WriteLine($"Invalid option: {key}");
            return 2;
    }
}

try
{
    var path = Path.GetFullPath(args[0]);
    var revisions = new FileRevisionProvider();
    var expected = revisions.Capture(path);
    if (!expected.Exists) throw new FileNotFoundException("The DDS file was not found.", path);
    if (expected.Error is not null || expected.Length < 0)
        throw new IOException("The DDS source could not be read reliably.");
    if (expected.Length > DdsTextureReader.MaximumInputBytes)
        throw new TextureFormatException("TextureTooLarge", "The selected DDS exceeds the decoder's input limit.");
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
        FileOptions.SequentialScan);
    var held = revisions.Capture(stream.SafeFileHandle, path);
    SourceIdentity.EnsureUnchanged(revisions, stream.SafeFileHandle, path, expected);
    var document = new DdsTextureReader().Read(stream);
    SourceIdentity.EnsureUnchanged(revisions, stream.SafeFileHandle, path, held);
    var selection = document.DefaultSelection with
    {
        ArrayLayer = requestedLayer ?? document.DefaultSelection.ArrayLayer,
        Face = faceSpecified ? requestedFace : document.DefaultSelection.Face,
        MipIndex = requestedMip ?? document.DefaultSelection.MipIndex
    };
    var surface = document.Info.PixelDecodeSupported
        ? new DdsTextureDecoder().DecodeSubresource(document, selection)
        : null;
    var pixelDigest = surface is null ? null : surface.IsHdr
        ? Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(surface.HdrPixels.Span))).ToLowerInvariant()
        : Convert.ToHexString(SHA256.HashData(surface.Rgba8.Span)).ToLowerInvariant();
    var report = new
    {
        file = Path.GetFileName(path),
        sourceBytes = document.OriginalBytes.Length,
        sourceSha256 = document.ContentSha256,
        document.Info.Width,
        document.Info.Height,
        document.Info.MipCount,
        pixelFormat = document.Info.FormatName,
        document.Info.IsSrgb,
        document.Info.IsHdr,
        document.Info.HeaderLength,
        document.Info.EncodedLength,
        document.Info.TrailingLength,
        document.Info.PixelDecodeSupported,
        decodedPixelSha256 = pixelDigest,
        decodedWidth = surface?.Width,
        decodedHeight = surface?.Height,
        selectedSubresource = surface?.Subresource,
        arrayLayerCount = document.Info.ArrayLayerCount,
        document.Info.IsCubeMap,
        cubeFaces = document.Info.CubeFaces,
        subresources = document.Subresources
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    }));
    return 0;
}
catch (TextureFormatException ex)
{
    Console.Error.WriteLine($"{ex.Code}: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"InputFailed: {ex.Message}");
    return 2;
}
