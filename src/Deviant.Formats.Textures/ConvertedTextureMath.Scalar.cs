namespace Deviant.Formats.Textures;

internal static partial class ConvertedTextureMath
{
    internal static void InverseDctScalar(ReadOnlySpan<short> coefficients,
        ReadOnlySpan<short> quantization, Span<byte> output)
    {
        if (coefficients.Length != 64) throw new ArgumentException("Expected 64 coefficient words.", nameof(coefficients));
        if (quantization.Length != 64) throw new ArgumentException("Expected 64 quantization words.", nameof(quantization));
        if (output.Length != 64) throw new ArgumentException("Expected 64 output samples.", nameof(output));

        Span<int> intermediate = stackalloc int[64];
        Span<int> scaled = stackalloc int[8];
        unchecked
        {
            // The native first pass transforms one input column at a time.
            for (var column = 0; column < 8; column++)
            {
                var dcAt = column;
                var dcCoefficient = coefficients[dcAt];
                var dcQuantization = (ushort)quantization[dcAt];
                var acCoefficientsAreZero = true;
                for (var frequency = 1; frequency < 8; frequency++)
                    acCoefficientsAreZero &= coefficients[frequency * 8 + column] == 0;

                if (acCoefficientsAreZero)
                {
                    var dc = 4 * dcCoefficient * dcQuantization;
                    for (var row = 0; row < 8; row++) intermediate[row * 8 + column] = dc;
                    continue;
                }

                for (var frequency = 0; frequency < 8; frequency++)
                {
                    var at = frequency * 8 + column;
                    scaled[frequency] = coefficients[at] * (ushort)quantization[at];
                }

                var q2c2 = scaled[2];
                var q6c6 = scaled[6];
                var rotation = 4433 * (q6c6 + q2c2);
                var evenA = rotation + 6270 * q2c2;
                var evenB = rotation - 15137 * q6c6;
                var q7c7 = scaled[7];
                var q5c5 = scaled[5];
                var q3c3 = scaled[3];
                var dcTerm = ((scaled[4] << 13) + ((scaled[0] << 13) + 1024));
                var dcOpposite = ((scaled[0] << 13) + 1024) - (scaled[4] << 13);
                var evenPlus = dcTerm + evenA;
                var evenMinus = dcTerm - evenA;
                var q1c1 = scaled[1];
                var oddPlus = evenB + dcOpposite;
                var oddMinus = dcOpposite - evenB;
                var oddA = 9633 * (q5c5 + q1c1 + q7c7 + q3c3) - 16069 * (q7c7 + q3c3);
                var oddB = 9633 * (q5c5 + q1c1 + q7c7 + q3c3) - 3196 * (q5c5 + q1c1);
                var oddC = -7373 * (q7c7 + q1c1);
                var oddD = oddC + 12299 * q1c1;
                var oddE = oddA + oddC + 2446 * q7c7;
                var oddF = -20995 * (q5c5 + q3c3);
                var oddG = oddB + oddD;
                var oddH = oddB + oddF + 16819 * q5c5;
                var oddI = oddA + oddF + 25172 * q3c3;

                intermediate[column] = (evenPlus + oddG) >> 11;
                intermediate[56 + column] = (evenPlus - oddG) >> 11;
                intermediate[8 + column] = (oddPlus + oddI) >> 11;
                intermediate[48 + column] = (oddPlus - oddI) >> 11;
                intermediate[16 + column] = (oddH + oddMinus) >> 11;
                intermediate[40 + column] = (oddMinus - oddH) >> 11;
                intermediate[24 + column] = (evenMinus + oddE) >> 11;
                intermediate[32 + column] = (evenMinus - oddE) >> 11;
            }

            // The second pass transforms each intermediate row and writes clamped bytes.
            for (var row = 0; row < 8; row++)
            {
                var at = row * 8;
                var dc = intermediate[at];
                if (intermediate[at + 1] == 0 && intermediate[at + 2] == 0 &&
                    intermediate[at + 3] == 0 && intermediate[at + 4] == 0 &&
                    intermediate[at + 5] == 0 && intermediate[at + 6] == 0 &&
                    intermediate[at + 7] == 0)
                {
                    var sample = Math.Clamp((dc + 16) >> 5, 0, 255);
                    output[at] = (byte)sample;
                    output[at + 1] = (byte)sample;
                    output[at + 2] = (byte)sample;
                    output[at + 3] = (byte)sample;
                    output[at + 4] = (byte)sample;
                    output[at + 5] = (byte)sample;
                    output[at + 6] = (byte)sample;
                    output[at + 7] = (byte)sample;
                    continue;
                }

                var q2 = intermediate[at + 2];
                var q6 = intermediate[at + 6];
                var rotation = 4433 * (q6 + q2);
                var evenA = rotation + 6270 * q2;
                var evenB = rotation - 15137 * q6;
                var q5 = intermediate[at + 5];
                var q3 = intermediate[at + 3];
                var dcTerm = (intermediate[at + 4] + dc + 16) << 13;
                var dcOpposite = (dc + 16 - intermediate[at + 4]) << 13;
                var evenPlus = dcTerm + evenA;
                var evenMinus = dcTerm - evenA;
                var q1 = intermediate[at + 1];
                var oddPlus = dcOpposite + evenB;
                var oddMinus = dcOpposite - evenB;
                var q7 = intermediate[at + 7];
                var oddA = 9633 * (q5 + q1 + q7 + q3) - 16069 * (q7 + q3);
                var oddB = 9633 * (q5 + q1 + q7 + q3) - 3196 * (q5 + q1);
                var oddC = -7373 * (q7 + q1);
                var oddD = oddC + 12299 * q1;
                var oddE = oddA + oddC + 2446 * q7;
                var oddF = -20995 * (q5 + q3);
                var oddG = oddB + oddD;
                var oddH = oddB + oddF + 16819 * q5;
                var oddI = oddA + oddF + 25172 * q3;

                output[at] = Clamp((oddG + evenPlus) >> 18);
                output[at + 7] = Clamp((evenPlus - oddG) >> 18);
                output[at + 1] = Clamp((oddPlus + oddI) >> 18);
                output[at + 6] = Clamp((oddPlus - oddI) >> 18);
                output[at + 2] = Clamp((oddMinus + oddH) >> 18);
                output[at + 5] = Clamp((oddMinus - oddH) >> 18);
                output[at + 3] = Clamp((evenMinus + oddE) >> 18);
                output[at + 4] = Clamp((evenMinus - oddE) >> 18);
            }
        }
    }
}
