namespace Deviant.Formats.Textures;
internal static partial class ConvertedTextureMath
{
    internal static readonly byte[] Zigzag = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63];
    internal static readonly byte[] DcLumaCounts = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    internal static readonly byte[] DcLumaSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    internal static readonly byte[] AcLumaCounts = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125];
    internal static readonly byte[] AcLumaSymbols = [1, 2, 3, 0, 4, 17, 5, 18, 33, 49, 65, 6, 19, 81, 97, 7, 34, 113, 20, 50, 129, 145, 161, 8, 35, 66, 177, 193, 21, 82, 209, 240, 36, 51, 98, 114, 130, 9, 10, 22, 23, 24, 25, 26, 37, 38, 39, 40, 41, 42, 52, 53, 54, 55, 56, 57, 58, 67, 68, 69, 70, 71, 72, 73, 74, 83, 84, 85, 86, 87, 88, 89, 90, 99, 100, 101, 102, 103, 104, 105, 106, 115, 116, 117, 118, 119, 120, 121, 122, 131, 132, 133, 134, 135, 136, 137, 138, 146, 147, 148, 149, 150, 151, 152, 153, 154, 162, 163, 164, 165, 166, 167, 168, 169, 170, 178, 179, 180, 181, 182, 183, 184, 185, 186, 194, 195, 196, 197, 198, 199, 200, 201, 202, 210, 211, 212, 213, 214, 215, 216, 217, 218, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250];
    internal static readonly byte[] DcChromaCounts = [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    internal static readonly byte[] DcChromaSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    internal static readonly byte[] AcChromaCounts = [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 119];
    internal static readonly byte[] AcChromaSymbols = [0, 1, 2, 3, 17, 4, 5, 33, 49, 6, 18, 65, 81, 7, 97, 113, 19, 34, 50, 129, 8, 20, 66, 145, 161, 177, 193, 9, 35, 51, 82, 240, 21, 98, 114, 209, 10, 22, 36, 52, 225, 37, 241, 23, 24, 25, 26, 38, 39, 40, 41, 42, 53, 54, 55, 56, 57, 58, 67, 68, 69, 70, 71, 72, 73, 74, 83, 84, 85, 86, 87, 88, 89, 90, 99, 100, 101, 102, 103, 104, 105, 106, 115, 116, 117, 118, 119, 120, 121, 122, 130, 131, 132, 133, 134, 135, 136, 137, 138, 146, 147, 148, 149, 150, 151, 152, 153, 154, 162, 163, 164, 165, 166, 167, 168, 169, 170, 178, 179, 180, 181, 182, 183, 184, 185, 186, 194, 195, 196, 197, 198, 199, 200, 201, 202, 210, 211, 212, 213, 214, 215, 216, 217, 218, 226, 227, 228, 229, 230, 231, 232, 233, 234, 242, 243, 244, 245, 246, 247, 248, 249, 250];
    private static readonly ushort[] LumaQuantization = [16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99];
    private static readonly ushort[] ChromaQuantization = [17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99];
    private static readonly short[][] RowCoefficients =
    [
        [16384, 21407, 16384, 8867, 16384, -8867, 16384, -21407, 16384, 8867, -16384, -21407, -16384, 21407, 16384, -8867, 22725, 19266, 19266, -4520, 12873, -22725, 4520, -12873, 12873, 4520, -22725, -12873, 4520, 19266, 19266, -22725],
        [22725, 29692, 22725, 12299, 22725, -12299, 22725, -29692, 22725, 12299, -22725, -29692, -22725, 29692, 22725, -12299, 31521, 26722, 26722, -6270, 17855, -31521, 6270, -17855, 17855, 6270, -31521, -17855, 6270, 26722, 26722, -31521],
        [21407, 27969, 21407, 11585, 21407, -11585, 21407, -27969, 21407, 11585, -21407, -27969, -21407, 27969, 21407, -11585, 29692, 25172, 25172, -5906, 16819, -29692, 5906, -16819, 16819, 5906, -29692, -16819, 5906, 25172, 25172, -29692],
        [19266, 25172, 19266, 10426, 19266, -10426, 19266, -25172, 19266, 10426, -19266, -25172, -19266, 25172, 19266, -10426, 26722, 22654, 22654, -5315, 15137, -26722, 5315, -15137, 15137, 5315, -26722, -15137, 5315, 22654, 22654, -26722]
    ];
    private static readonly int[] RowRounding = [65536, 2901, 2260, 1704, 1024, 455, 512, 374];
    internal static void Quantization(int quality, Span<short> luma, Span<short> chroma)
    {
        var scale = quality == 0 ? 5000 : quality < 50 ? 5000 / quality : 200 - 2 * quality;
        for (var index = 0; index < 64; index++)
        {
            luma[index] = (short)Math.Clamp((LumaQuantization[index] * scale + 50) / 100, 1, 255);
            chroma[index] = (short)Math.Clamp((ChromaQuantization[index] * scale + 50) / 100, 1, 255);
        }
    }

    // Integer inverse DCT: row dot products, then a saturating column butterfly.
    internal static void InverseDct(ReadOnlySpan<short> coefficients, ReadOnlySpan<short> quantization, Span<short> output)
    {
        Span<short> rows = stackalloc short[64];
        Span<short> scaled = stackalloc short[8];
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
                scaled[column] = unchecked((short)(coefficients[row * 8 + column] * quantization[row * 8 + column]));
            var group = row is 0 or 4 ? 0 : row is 1 or 7 ? 1 : row is 2 or 6 ? 2 : 3;
            var matrix = RowCoefficients[group];
            for (var position = 0; position < 4; position++)
            {
                var at = position * 2;
                var even = unchecked(RowRounding[row] + scaled[0] * matrix[at] + scaled[2] * matrix[at + 1] +
                    scaled[4] * matrix[8 + at] + scaled[6] * matrix[9 + at]);
                var odd = unchecked(scaled[1] * matrix[16 + at] + scaled[3] * matrix[17 + at] +
                    scaled[5] * matrix[24 + at] + scaled[7] * matrix[25 + at]);
                rows[row * 8 + position] = Saturate(unchecked(even + odd) >> 11);
                rows[row * 8 + 7 - position] = Saturate(unchecked(even - odd) >> 11);
            }
        }
        for (var column = 0; column < 8; column++)
        {
            var lowRotation = Subtract(MultiplyHigh(rows[8 + column], 13036), rows[56 + column]);
            var highRotation = Add(rows[8 + column], MultiplyHigh(rows[56 + column], 13036));
            var middleSum = Add(Add(MultiplyHigh(rows[40 + column], -21746), rows[40 + column]), rows[24 + column]);
            var middleDifference = Subtract(rows[40 + column], Add(MultiplyHigh(rows[24 + column], -21746), rows[24 + column]));
            var oddOuter = Add(highRotation, middleSum);
            var oddInner = Add(lowRotation, middleDifference);
            var oddDifference = Subtract(highRotation, middleSum);
            var oddOther = Subtract(lowRotation, middleDifference);
            var oddSum = Add(oddOther, oddDifference);
            var oddDelta = Subtract(oddDifference, oddOther);
            var oddSecond = Add(MultiplyHigh(oddSum, -19195), oddSum);
            var oddThird = Add(MultiplyHigh(oddDelta, -19195), oddDelta);
            var evenSum = Add(rows[column], rows[32 + column]);
            var evenDifference = Subtract(rows[column], rows[32 + column]);
            var evenRotation = Add(rows[16 + column], MultiplyHigh(rows[48 + column], 27146));
            var evenOther = Subtract(MultiplyHigh(rows[16 + column], 27146), rows[48 + column]);
            var outer = Add(evenRotation, evenSum);
            var inner = Subtract(evenSum, evenRotation);
            var second = Add(evenDifference, evenOther);
            var third = Subtract(evenDifference, evenOther);
            output[column] = (short)(Add(oddOuter, outer) >> 6);
            output[8 + column] = (short)(Add(second, oddSecond) >> 6);
            output[16 + column] = (short)(Add(third, oddThird) >> 6);
            output[24 + column] = (short)(Add(inner, oddInner) >> 6);
            output[32 + column] = (short)(Subtract(inner, oddInner) >> 6);
            output[40 + column] = (short)(Subtract(third, oddThird) >> 6);
            output[48 + column] = (short)(Subtract(second, oddSecond) >> 6);
            output[56 + column] = (short)(Subtract(outer, oddOuter) >> 6);
        }
    }

    internal static void Color(ReadOnlySpan<short> planes, bool alpha, Span<byte> output)
    {
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var lumaAt = ((y / 8) * 2 + x / 8) * 64 + (y % 8) * 8 + x % 8;
            var chromaAt = (y / 2) * 8 + x / 2;
            var luma = planes[lumaAt]; var co = planes[256 + chromaAt]; var cg = planes[320 + chromaAt];
            var destination = (y * 16 + x) * 4;
            output[destination] = Clamp(unchecked((short)(luma + co - cg)));
            output[destination + 1] = Clamp(unchecked((short)(luma + cg)));
            output[destination + 2] = Clamp(unchecked((short)(luma - co - cg)));
            output[destination + 3] = alpha ? Clamp(planes[384 + lumaAt]) : (byte)255;
        }
    }

    internal static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
    private static short Saturate(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    private static short Add(int left, int right) => Saturate(left + right);
    private static short Subtract(int left, int right) => Saturate(left - right);
    private static short MultiplyHigh(int left, int right) => unchecked((short)((left * right) >> 16));
}
