using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.ModelProbe;

internal static class Program
{
    private const int MaximumReadBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            var snapshot = await new ArchiveReader().OpenAsync(options.Input).ConfigureAwait(false);
            if (options.Ordinal < 0 || options.Ordinal >= snapshot.Records.Count)
                throw new ArgumentOutOfRangeException(nameof(options.Ordinal), "The ordinal is outside the selected index.");

            var record = snapshot.Records[checked((int)options.Ordinal)];
            var readLength = Math.Min(record.StoredSize, options.PrefixBytes);
            if (record.Validity != ResourceValidity.Valid)
                throw new ArchiveException("InvalidResource", $"The selected resource is {record.Validity}.");
            if (readLength < 0 || readLength > MaximumReadBytes)
                throw new InvalidDataException($"The requested read exceeds the {MaximumReadBytes}-byte safety limit.");

            var identity = new ResourceIdentity(snapshot.IndexContentId, record.Ordinal);
            var resolver = new ArchiveResourceResolver(snapshot);
            var bytes = await resolver.ReadAsync(identity, 0, checked((int)readLength)).ConfigureAwait(false);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var report = new
            {
                Format = "deviant-model-probe-v1",
                IndexContentId = snapshot.IndexContentId,
                Resource = new
                {
                    Identity = identity.Key,
                    record.Ordinal,
                    record.Type,
                    record.Flags,
                    record.Id,
                    record.Package,
                    record.Offset,
                    record.StoredSize,
                    record.AlternativeSize,
                    record.VolumeName,
                    record.Validity
                },
                Read = new
                {
                    RelativeSource = record.VolumeName,
                    Offset = record.Offset,
                    Length = bytes.Length,
                    IsCompleteResource = bytes.Length == record.StoredSize,
                    Sha256 = digest,
                    Signature = IdentifySignature(bytes),
                    PreviewHex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))).ToLowerInvariant(),
                    PreviewAscii = GetAsciiPreview(bytes, 64)
                },
                Diagnostics = snapshot.Diagnostics
                    .Where(diagnostic => diagnostic.Ordinal == record.Ordinal || diagnostic.Package == record.Package)
                    .Select(diagnostic => new { diagnostic.Code, diagnostic.Ordinal, diagnostic.Package, diagnostic.Message })
                    .ToArray()
            };
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }
    }

    private static string IdentifySignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("segs"u8)) return "segs";
        if (bytes.StartsWith("QZIP\0"u8)) return "QZIP";
        if (bytes.StartsWith("QUANTICDREAMTABINDEX"u8)) return "QUANTICDREAMTABINDEX";
        return "unknown";
    }

    private static string GetAsciiPreview(ReadOnlySpan<byte> bytes, int limit)
    {
        var length = Math.Min(bytes.Length, limit);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
        return new string(chars);
    }

    private sealed record ProbeOptions(string Input, long Ordinal, int PrefixBytes)
    {
        public const string Usage = "Usage: Deviant.ModelProbe --input <index-or-directory> --ordinal <n> [--prefix-bytes <n>]";

        public static ProbeOptions Parse(string[] args)
        {
            string? input = null;
            long? ordinal = null;
            var prefix = 8192;
            for (var i = 0; i < args.Length; i++)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
                var option = args[i];
                var value = args[++i];
                switch (option)
                {
                    case "--input": input = value; break;
                    case "--ordinal":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOrdinal))
                            throw new ArgumentException("Ordinal must be a non-negative decimal integer.");
                        ordinal = parsedOrdinal;
                        break;
                    case "--prefix-bytes":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix is <= 0 or > MaximumReadBytes)
                            throw new ArgumentException($"Prefix length must be between 1 and {MaximumReadBytes}.");
                        break;
                    default: throw new ArgumentException($"Unknown option {option}.");
                }
            }
            if (string.IsNullOrWhiteSpace(input) || ordinal is null)
                throw new ArgumentException(Usage);
            return new(input, ordinal.Value, prefix);
        }
    }
}
