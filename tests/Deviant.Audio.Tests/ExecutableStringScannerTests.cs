using System.Buffers.Binary;
using System.Text;
using Deviant.Formats.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class ExecutableStringScannerTests
{
    [Fact]
    public void ReadsIdentifierFromNonExecutableDataSection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deviant-name-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.exe");
        try
        {
            var image = new byte[0x300];
            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), 0x80);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, 0x80);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94, 2), 0);
            Encoding.ASCII.GetBytes(".rdata").CopyTo(image, 0x98);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 16, 4), 0x100);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 20, 4), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 36, 4), 0x40000040);
            Encoding.ASCII.GetBytes("candidate_sound\0").CopyTo(image, 0x200);
            File.WriteAllBytes(path, image);

            var recovered = ExecutableStringScanner.Read(path);

            var item = Assert.Single(recovered);
            Assert.Equal("candidate_sound", item.Text);
            Assert.Equal(".rdata", item.Section);
            Assert.Equal(0x200L, item.Offset);
            Assert.Matches("^[0-9a-f]{64}$", item.InputSha256);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
