using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace Deviant.Formats.Shaders;

/// <summary>Evaluates the bounded expression stored on a native material array constant.</summary>
public static class NativeMaterialInstanceExpression
{
    private const int MaximumCharacters = 1024;
    private const int MaximumTokens = 1024;
    private const int MaximumDepth = 64;
    private const int MaximumElements = 64;

    public static bool TryEvaluate(
        NativeMaterialArrayConstant record,
        IReadOnlyList<Vector3>? jointWorldPositions,
        out float value,
        out string? diagnostic,
        CancellationToken token = default)
    {
        value = default;
        diagnostic = null;

        if (record is null)
        {
            diagnostic = "A material array constant is required.";
            return false;
        }

        token.ThrowIfCancellationRequested();

        try
        {
            var elementCount = Convert.ToInt32(record.ElementCount, CultureInfo.InvariantCulture);
            if (elementCount < 0 || elementCount > MaximumElements)
                throw new EvaluationException("The material array element count is outside the supported limit.");

            var words = record.ElementWords;
            if (words is null || words.Count != elementCount || words.Count > MaximumElements)
                throw new EvaluationException("The joint ordinal list is missing or exceeds the supported limit.");

            var initialBits = Convert.ToInt64(record.Float32Bits, CultureInfo.InvariantCulture);
            if (initialBits < int.MinValue || initialBits > uint.MaxValue)
                throw new EvaluationException("The initial value bits are outside the Float32 range.");

            var initialValue = BitConverter.Int32BitsToSingle(unchecked((int)initialBits));
            if (!float.IsFinite(initialValue))
                throw new EvaluationException("The initial value is not finite.");

            var expression = record.TextField2;
            double result;
            if (string.IsNullOrWhiteSpace(expression))
            {
                result = initialValue;
            }
            else
            {
                if (expression.Length > MaximumCharacters)
                    throw new EvaluationException("The expression exceeds the supported character limit.");

                var parser = new Parser(expression, words, jointWorldPositions, token);
                result = parser.Parse();
            }

            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(result))
                throw new EvaluationException("The expression result is not finite.");

            var converted = (float)result;
            if (!float.IsFinite(converted))
                throw new EvaluationException("The expression result is outside the finite Float32 range.");

            value = elementCount > 0 ? Math.Clamp(converted, 0f, 1f) : converted;
            return true;
        }
        catch (EvaluationException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            diagnostic = "The material expression contains an invalid numeric field.";
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<Token> _tokens;
        private readonly IReadOnlyList<uint> _elementWords;
        private readonly IReadOnlyList<Vector3>? _jointWorldPositions;
        private readonly CancellationToken _token;
        private int _position;

        public Parser(
            string source,
            IReadOnlyList<uint> elementWords,
            IReadOnlyList<Vector3>? jointWorldPositions,
            CancellationToken token)
        {
            _elementWords = elementWords;
            _jointWorldPositions = jointWorldPositions;
            _token = token;
            _tokens = Tokenize(source, token);
        }

        public double Parse()
        {
            var result = ParseExpression(0);
            Expect(TokenKind.End, "Unexpected text after the expression.");
            return result;
        }

        private double ParseExpression(int depth) => ParseAddition(depth);

        private double ParseAddition(int depth)
        {
            var value = ParseMultiplication(depth);
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                _token.ThrowIfCancellationRequested();
                var operation = Take().Kind;
                var right = ParseMultiplication(depth);
                value = operation == TokenKind.Plus ? value + right : value - right;
                EnsureFinite(value);
            }

            return value;
        }

        private double ParseMultiplication(int depth)
        {
            var value = ParseUnary(depth);
            while (Current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                _token.ThrowIfCancellationRequested();
                var operation = Take().Kind;
                var right = ParseUnary(depth);
                if (operation == TokenKind.Slash && right == 0d)
                    throw new EvaluationException("Division by zero is not allowed.");

                value = operation == TokenKind.Star ? value * right : value / right;
                EnsureFinite(value);
            }

            return value;
        }

        private double ParseUnary(int depth)
        {
            var negative = false;
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                _token.ThrowIfCancellationRequested();
                if (Take().Kind == TokenKind.Minus)
                    negative = !negative;
            }

            var value = ParsePrimary(depth);
            return negative ? -value : value;
        }

        private double ParsePrimary(int depth)
        {
            _token.ThrowIfCancellationRequested();
            if (Current.Kind == TokenKind.Number)
                return Take().Number;

            if (Current.Kind == TokenKind.LeftParen)
            {
                EnterDepth(depth);
                Take();
                var value = ParseExpression(depth + 1);
                Expect(TokenKind.RightParen, "A closing parenthesis is missing.");
                return value;
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                var function = Take().Text;
                EnterDepth(depth);
                Expect(TokenKind.LeftParen, "A function call must use parentheses.");
                var first = ParseExpression(depth + 1);
                Expect(TokenKind.Comma, "A function argument separator is missing.");
                var second = ParseExpression(depth + 1);

                double value;
                if (function == "dist")
                {
                    Expect(TokenKind.RightParen, "A closing parenthesis is missing.");
                    value = Distance(ResolveOrdinal(first), ResolveOrdinal(second));
                }
                else if (function == "dotProduct")
                {
                    Expect(TokenKind.Comma, "A function argument separator is missing.");
                    var third = ParseExpression(depth + 1);
                    Expect(TokenKind.RightParen, "A closing parenthesis is missing.");
                    value = DotProduct(
                        ResolveOrdinal(first),
                        ResolveOrdinal(second),
                        ResolveOrdinal(third));
                }
                else
                {
                    throw new EvaluationException($"Unknown function '{function}'.");
                }

                EnsureFinite(value);
                return value;
            }

            throw new EvaluationException("A number, function, or parenthesized expression was expected.");
        }

        private Vector3 ResolveOrdinal(double ordinalValue)
        {
            if (Math.Truncate(ordinalValue) != ordinalValue || ordinalValue < 0d || ordinalValue > int.MaxValue)
                throw new EvaluationException("Joint function arguments must be nonnegative integer ordinals.");

            var ordinal = (int)ordinalValue;
            if (ordinal >= _elementWords.Count)
                throw new EvaluationException("A joint ordinal is outside the material's ordered joint list.");

            if (_jointWorldPositions is null)
                throw new EvaluationException("Posed joint world positions are required by this expression.");

            var jointIndex = _elementWords[ordinal];
            if (jointIndex >= _jointWorldPositions.Count)
                throw new EvaluationException("A joint reference is outside the posed joint list.");

            var position = _jointWorldPositions[(int)jointIndex];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new EvaluationException("A referenced joint position is not finite.");

            return position;
        }

        private static double Distance(Vector3 first, Vector3 second)
        {
            return (float)(100f * Vector3.Distance(first, second));
        }

        private static double DotProduct(Vector3 first, Vector3 origin, Vector3 third)
        {
            var firstDirection = first - origin;
            var secondDirection = third - origin;
            if (firstDirection == Vector3.Zero || secondDirection == Vector3.Zero)
                return 0d;
            return Vector3.Dot(Vector3.Normalize(firstDirection), Vector3.Normalize(secondDirection));
        }

        private Token Current => _tokens[_position];

        private Token Take()
        {
            var token = Current;
            if (token.Kind != TokenKind.End)
                _position++;
            return token;
        }

        private void Expect(TokenKind kind, string message)
        {
            if (Current.Kind != kind)
                throw new EvaluationException(message);
            Take();
        }

        private static void EnterDepth(int depth)
        {
            if (depth >= MaximumDepth)
                throw new EvaluationException("The expression nesting limit was exceeded.");
        }

        private static void EnsureFinite(double value)
        {
            if (!double.IsFinite(value))
                throw new EvaluationException("The expression contains a nonfinite value.");
        }

        private static IReadOnlyList<Token> Tokenize(string source, CancellationToken token)
        {
            var tokens = new List<Token>(Math.Min(source.Length + 1, MaximumTokens));
            var position = 0;
            while (position < source.Length)
            {
                token.ThrowIfCancellationRequested();
                var character = source[position];
                if (char.IsWhiteSpace(character))
                {
                    position++;
                    continue;
                }

                if (tokens.Count >= MaximumTokens - 1)
                    throw new EvaluationException("The expression exceeds the supported token limit.");

                switch (character)
                {
                    case '+': tokens.Add(new Token(TokenKind.Plus)); position++; continue;
                    case '-': tokens.Add(new Token(TokenKind.Minus)); position++; continue;
                    case '*': tokens.Add(new Token(TokenKind.Star)); position++; continue;
                    case '/': tokens.Add(new Token(TokenKind.Slash)); position++; continue;
                    case '(': tokens.Add(new Token(TokenKind.LeftParen)); position++; continue;
                    case ')': tokens.Add(new Token(TokenKind.RightParen)); position++; continue;
                    case ',': tokens.Add(new Token(TokenKind.Comma)); position++; continue;
                }

                if (char.IsAsciiLetter(character))
                {
                    var start = position++;
                    while (position < source.Length && char.IsAsciiLetter(source[position]))
                        position++;
                    tokens.Add(new Token(TokenKind.Identifier, 0d, source[start..position]));
                    continue;
                }

                if (char.IsAsciiDigit(character) || character == '.')
                {
                    var start = position;
                    var digits = 0;
                    while (position < source.Length && char.IsAsciiDigit(source[position]))
                    {
                        position++;
                        digits++;
                    }

                    if (position < source.Length && source[position] == '.')
                    {
                        position++;
                        while (position < source.Length && char.IsAsciiDigit(source[position]))
                        {
                            position++;
                            digits++;
                        }
                    }

                    if (digits == 0)
                        throw new EvaluationException("A decimal number is malformed.");

                    if (position < source.Length && (source[position] == 'e' || source[position] == 'E'))
                    {
                        position++;
                        if (position < source.Length && (source[position] == '+' || source[position] == '-'))
                            position++;
                        var exponentStart = position;
                        while (position < source.Length && char.IsAsciiDigit(source[position]))
                            position++;
                        if (position == exponentStart)
                            throw new EvaluationException("A decimal exponent is malformed.");
                    }

                    var numberText = source.AsSpan(start, position - start);
                    if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        !double.IsFinite(number))
                        throw new EvaluationException("A decimal number is invalid or nonfinite.");

                    tokens.Add(new Token(TokenKind.Number, number));
                    continue;
                }

                throw new EvaluationException($"Unexpected character '{character}'.");
            }

            tokens.Add(new Token(TokenKind.End));
            return tokens;
        }
    }

    private enum TokenKind
    {
        Number,
        Identifier,
        Plus,
        Minus,
        Star,
        Slash,
        LeftParen,
        RightParen,
        Comma,
        End
    }

    private readonly record struct Token(TokenKind Kind, double Number = 0d, string Text = "");

    private sealed class EvaluationException : Exception
    {
        public EvaluationException(string message) : base(message) { }
    }
}
