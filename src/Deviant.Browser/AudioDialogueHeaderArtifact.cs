using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;

namespace Deviant.Browser;

internal static class AudioDialogueHeaderArtifact
{
    private const int HeaderBytes = 16;
    private const string Category = "dialogue-headers";
    private const string Profile = "dialogue-header-rows-v1";
    private static ReadOnlySpan<byte> Magic => "DVAHDR01"u8;

    public static async Task<ReadLease> AcquireAsync(CacheManager cache, SourceRevision revision,
        int count, string rowsSha256, int maximumBytes, IReadOnlyList<DialogueHeaderAssociation>? rows,
        CancellationToken token)
    {
        if (count < 0 || rowsSha256 is not { Length: 64 } || !rowsSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Dialogue artifact identity is invalid.");
        var key = CacheManager.PreparedKey(Category, Profile, revision.IndexContentId + "|" + revision.PhysicalSourceStamp,
            count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + rowsSha256);
        CacheLease? lease = null;
        FileStream? input = null;
        try
        {
            lease = await cache.AcquirePreparedOutputAsync(Category, key, ".headers", async (output, cancellation) =>
            {
                if (rows is null) throw new FileNotFoundException("The prepared dialogue headers are unavailable.");
                byte[] header = new byte[HeaderBytes];
                Magic.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), count);
                await output.WriteAsync(header, cancellation).ConfigureAwait(false);
                using var bounded = new PayloadWriter(output, maximumBytes - HeaderBytes);
                await JsonSerializer.SerializeAsync(bounded, rows, cancellationToken: cancellation).ConfigureAwait(false);
                await bounded.FlushAsync(cancellation).ConfigureAwait(false);
            }, async (path, cancellation) =>
            {
                input?.Dispose(); input = null;
                var candidate = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                bool accepted = false;
                try
                {
                    using var timing = AssetLoadDiagnostics.Measure("DialogueArtifactVerify", key,
                        relativeSource: Path.GetFileName(path), requestedBytes: candidate.Length);
                    if (candidate.Length is < HeaderBytes + 2 || candidate.Length > maximumBytes) return false;
                    byte[] header = new byte[HeaderBytes];
                    await candidate.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
                    if (!header.AsSpan(0, 8).SequenceEqual(Magic) ||
                        BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion ||
                        BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != count) return false;
                    var digest = await SHA256.HashDataAsync(candidate, cancellation).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(rowsSha256))) return false;
                    candidate.Position = HeaderBytes;
                    input = candidate; accepted = true; return true;
                }
                finally { if (!accepted) candidate.Dispose(); }
            }, token).ConfigureAwait(false);
            if (input is null) throw new InvalidDataException("Dialogue artifact validation is missing.");
            return new(lease, input, count);
        }
        catch
        {
            if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal sealed class ReadLease(CacheLease lease, FileStream input, int count) : IAsyncDisposable
    {
        public async Task<List<DialogueHeaderAssociation>> ReadAsync(CancellationToken token)
        {
            using var timing = AssetLoadDiagnostics.Measure("DialogueArtifactRestore");
            var rows = new List<DialogueHeaderAssociation>(count);
            await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<DialogueHeaderAssociation>(input,
                cancellationToken: token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (row is null || rows.Count >= count) throw new InvalidDataException("Dialogue artifact row count is invalid.");
                rows.Add(row);
            }
            if (rows.Count != count) throw new InvalidDataException("Dialogue artifact is incomplete.");
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            try { await input.DisposeAsync().ConfigureAwait(false); }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private sealed class PayloadWriter(Stream output, long maximumBytes) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        private void Admit(int count)
        {
            if (count > maximumBytes - written) throw new InvalidDataException("Dialogue header serialization exceeds its limit.");
            written += count;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); output.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Admit(buffer.Length); await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
