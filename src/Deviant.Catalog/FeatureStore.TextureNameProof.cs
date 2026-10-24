using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private static ReadOnlySpan<byte> TextureNameProofHeader => "TNP2"u8;
    private sealed record CompactTextureNameProof(string ThinSha256, IReadOnlyList<TextureEvidence> Sources);

    private static byte[] EncodeCompactTextureNameProof(TextureDiscoveryEntry thin, IReadOnlyList<TextureEvidence> sources)
    {
        var thinBytes = JsonSerializer.SerializeToUtf8Bytes(thin with { Alias = null });
        if (thinBytes.Length > MaximumTextureEvidenceBytes)
            throw new InvalidDataException("TextureProjectionEvidenceLimit");
        var content = JsonSerializer.SerializeToUtf8Bytes(new CompactTextureNameProof(
            Convert.ToHexString(SHA256.HashData(thinBytes)), sources));
        if (content.Length > MaximumTextureEvidenceBytes)
            throw new InvalidDataException("TextureProjectionEvidenceLimit");
        using var output = new MemoryStream();
        output.Write(TextureNameProofHeader);
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(content);
        if (output.Length > MaximumTextureNameProofBytes)
            throw new InvalidDataException("TextureProjectionNameProofLimit");
        return output.ToArray();
    }

    private static bool ValidCompactTextureNameProof(byte[] payload, TextureDiscoveryEntry thin,
        Func<TextureEvidence, bool>? validEvidence)
    {
        try
        {
            using var input = new MemoryStream(payload, TextureNameProofHeader.Length,
                payload.Length - TextureNameProofHeader.Length, writable: false);
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = decompressor.Read(buffer)) != 0)
            {
                if (output.Length + read > MaximumTextureEvidenceBytes) return false;
                output.Write(buffer.AsSpan(0, read));
            }
            var proof = JsonSerializer.Deserialize<CompactTextureNameProof>(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            if (proof is null || proof.Sources is null) return false;
            var thinBytes = JsonSerializer.SerializeToUtf8Bytes(thin with { Alias = null });
            return proof.ThinSha256 == Convert.ToHexString(SHA256.HashData(thinBytes)) &&
                (validEvidence is null || proof.Sources.All(validEvidence));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException or OverflowException)
        { return false; }
    }
}
