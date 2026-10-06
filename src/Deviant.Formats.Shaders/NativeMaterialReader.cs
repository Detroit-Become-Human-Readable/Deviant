using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Formats.Shaders;

/// <summary>Recognizes bounded stored material graphs and retains every native texture reference.</summary>
public sealed partial class NativeMaterialReader
{
    public const string Version = "native-material-2";
    public bool UseSerializedScalarInitialValues { get; init; }
    public const int MaximumSourceRegions = 128;
    public const int MaximumSourceBytes = 512 * 1024;
    public const int MaximumSourceTokens = 65536;
    public const int MaximumFunctionStatements = 4096;
    public const int MaximumExpressionDepth = 64;
    public const int MaximumExpressionNodes = 1024;

    public DecodeResult<MaterialAsset> Parse(NativeObjectFrame frame, SourceRevision revision,
        StoredGlslScan sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<string>();
        var references = new List<ResourceReference>();
        var bindings = new List<MaterialTextureBinding>();
        MaterialSurfaceGraph? surfaceGraph = null;
        DecodeResult<MaterialAsset> Finish() => new(
            new(frame.Identity, revision, surfaceGraph?.BaseColor is not null ? "surface-expression" : bindings.Count == 0 ? "neutral" : "direct-normal", bindings, references, diagnostics) { SurfaceGraph = surfaceGraph },
            frame.Identity, revision, sources.SourceExtents,
            [.. sources.DecodeSteps, new(nameof(NativeMaterialReader), Version, "ordered-native-slots; stored-normal-dataflow")],
            diagnostics, [], new Dictionary<string, string>());
        var adapter = new TextureContainerObjectFrame(frame.Identity, frame.DirectoryIndex, frame.NativeType,
            frame.NativeId, frame.DirectoryEntryExtent, frame.HeaderExtent, frame.PayloadExtent, frame.RawHeader,
            frame.RawPayload, frame.WrapperFlag, frame.WrapperCount, frame.WrapperKey, frame.ClassCount);
        var usage = new TextureUsageReader().ParseShaderSlots(adapter, revision, sources.SourceExtents, cancellationToken);
        if (usage.Value is not { } slots) { diagnostics.AddRange(usage.Diagnostics); return Finish(); }
        foreach (var slot in slots.Slots)
            references.Add(new(IdentifierNamespace.Native, slot.Reference.Id.ToString(CultureInfo.InvariantCulture),
                frame.Identity, slot.Reference.Type));
        if (sources.ContainerIdentity != frame.Identity || sources.Revision != revision ||
            sources.Sources.Any(source => source.Revision != revision || source.DecodedExtent.Container != frame.Identity))
        { diagnostics.Add("MaterialSourceIdentityMismatch"); return Finish(); }
        if (sources.Diagnostics.Count != 0)
        { diagnostics.Add("MaterialSourceScanIncomplete"); return Finish(); }
        if (sources.Sources.Count > MaximumSourceRegions)
        { diagnostics.Add("MaterialSourceRegionLimit"); return Finish(); }
        var programs = new NativeShaderProgramReader().Parse(frame, slots, cancellationToken);
        if (programs.Value is not { } layout) { diagnostics.AddRange(programs.Diagnostics); return Finish(); }
        Candidate? agreed = null;
        StoredGlslSource? evidenceSource = null;
        var surfaceSources = new List<(StoredGlslSource Source, List<Token> Tokens)>();
        try
        {
            foreach (var source in sources.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.HasNullTerminator || source.Text is null || source.Utf8Bytes.Length > MaximumSourceBytes || source.Text.Length > MaximumSourceBytes)
                    throw new GraphException(source.Utf8Bytes.Length > MaximumSourceBytes || source.Text?.Length > MaximumSourceBytes ? "MaterialSourceByteLimit" : "MaterialSourceIncomplete");
                if (!source.Utf8Bytes.Span.SequenceEqual(Encoding.UTF8.GetBytes(source.Text)) ||
                    !string.Equals(source.Sha256, Convert.ToHexString(SHA256.HashData(source.Utf8Bytes.Span)), StringComparison.OrdinalIgnoreCase))
                    throw new GraphException("MaterialSourceDigestMismatch");
                var tokens = Lex(source.Text, cancellationToken);
                surfaceSources.Add((source, tokens));
                Candidate? candidate;
                try { candidate = ReadNormal(tokens, cancellationToken); }
                catch (GraphException error) when (error.Message is "MaterialNormalGraphUnsupported" or "MaterialNormalControlFlowUnsupported" or "MaterialNormalConsumerUnsupported" or "MaterialNormalDeclarationAmbiguous")
                { candidate = null; if (error.Message != "MaterialNormalGraphUnsupported" && !diagnostics.Contains(error.Message, StringComparer.Ordinal)) diagnostics.Add(error.Message); }
                if (candidate is null) continue;
                if (candidate.Slot < 0 || candidate.Slot >= slots.Slots.Count)
                    throw new GraphException("MaterialSamplerSlotInvalid");
                var expectedOffset = checked(32 + 4 * layout.AlignedScalarConstantCount + 4 * candidate.Slot);
                if (MaterialFieldOffset(tokens, "s" + candidate.Slot.ToString(CultureInfo.InvariantCulture)) != expectedOffset)
                    throw new GraphException("MaterialSamplerLayoutMismatch");
                if (agreed is not null && (candidate.Slot != agreed.Slot || candidate.UvSet != agreed.UvSet ||
                    candidate.Scale != agreed.Scale || candidate.Offset != agreed.Offset))
                    throw new GraphException("MaterialNormalSourcesDisagree");
                agreed ??= candidate;
                evidenceSource ??= source;
            }
            if (agreed is null || evidenceSource is null)
            {
                surfaceGraph = ReadSurfaceGraph(surfaceSources, layout, slots, cancellationToken);
                diagnostics.Add("MaterialNormalGraphUnsupported"); return Finish();
            }
            var selectedSlot = slots.Slots[agreed.Slot];
            if (!selectedSlot.Reference.IsBound || selectedSlot.Reference.Type == 0)
            { diagnostics.Add("MaterialNormalReferenceUnavailable"); return Finish(); }
            var byteOffset = Encoding.UTF8.GetByteCount(evidenceSource.Text!.AsSpan(0, agreed.Start));
            var byteLength = Encoding.UTF8.GetByteCount(evidenceSource.Text.AsSpan(agreed.Start, agreed.End - agreed.Start));
            var expressionExtent = new DecodedExtent(frame.Identity, evidenceSource.DecodedExtent.Offset + byteOffset, byteLength);
            var evidence = new RelationshipEvidence(true, nameof(NativeMaterialReader),
                $"Stored normal graph; source {evidenceSource.Sha256}; slot {agreed.Slot}; scalar count {layout.ScalarConstantCount}; count offset {layout.CountExtent.Offset}",
                evidenceSource.PhysicalExtent, expressionExtent);
            bindings.Add(new(agreed.Slot, references[agreed.Slot], MaterialTextureRole.Normal,
                agreed.UvSet, agreed.Scale, agreed.Offset, evidence));
            surfaceGraph = ReadSurfaceGraph(surfaceSources, layout, slots, cancellationToken);
        }
        catch (GraphException error) { diagnostics.Add(error.Message); }
        catch (OverflowException) { diagnostics.Add("MaterialExpressionRangeInvalid"); }
        return Finish();
    }

    private sealed record Candidate(int Slot, int UvSet, Vector2 Scale, Vector2 Offset, int Start, int End);
    private sealed record Token(string Text, int Start, int End);
    private sealed class GraphException(string code) : Exception(code);
    private enum ValueKind { Unknown, Constant, Coordinates, Sample, Expression, Matrix2, ViewPosition, ViewNormal }
    private sealed record Value(ValueKind Kind, float[]? Numbers = null, int UvSet = 0,
        Vector2 Scale = default, Vector2 Offset = default, int Slot = -1, string Channels = "", MaterialExpression? Node = null)
    {
        public static Value Unknown { get; } = new(ValueKind.Unknown);
        public int Width => Kind == ValueKind.Constant ? Numbers!.Length : Kind == ValueKind.Sample ? Channels.Length : Node is not null ? (int)Node.ValueType : 0;
    }

    private static List<Token> Lex(string text, CancellationToken token)
    {
        var result = new List<Token>();
        for (var cursor = 0; cursor < text.Length;)
        {
            if ((cursor & 1023) == 0) token.ThrowIfCancellationRequested();
            var c = text[cursor];
            if (char.IsWhiteSpace(c)) { cursor++; continue; }
            if (c == '#' || c == '/' && cursor + 1 < text.Length && text[cursor + 1] == '/')
            { while (cursor < text.Length && text[cursor] != '\n') cursor++; continue; }
            if (c == '/' && cursor + 1 < text.Length && text[cursor + 1] == '*')
            {
                var end = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (end < 0) throw new GraphException("MaterialSourceCommentInvalid");
                cursor = end + 2; continue;
            }
            var start = cursor++;
            if (c is '\'' or '"')
            {
                var terminated = false;
                while (cursor < text.Length)
                {
                    if (text[cursor] == '\\') { cursor += Math.Min(2, text.Length - cursor); continue; }
                    if (text[cursor++] == c) { terminated = true; break; }
                }
                if (!terminated) throw new GraphException("MaterialSourceStringInvalid");
                result.Add(new("literal", start, cursor));
                if (result.Count > MaximumSourceTokens) throw new GraphException("MaterialSourceTokenLimit");
                continue;
            }
            if (char.IsLetter(c) || c == '_')
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
            else if (char.IsDigit(c) || c == '.' && cursor < text.Length && char.IsDigit(text[cursor]))
            {
                while (cursor < text.Length && (char.IsDigit(text[cursor]) || text[cursor] == '.')) cursor++;
                if (cursor < text.Length && text[cursor] is 'e' or 'E')
                {
                    cursor++;
                    if (cursor < text.Length && text[cursor] is '+' or '-') cursor++;
                    while (cursor < text.Length && char.IsDigit(text[cursor])) cursor++;
                }
                if (cursor < text.Length && text[cursor] is 'f' or 'F') cursor++;
            }
            else if (cursor < text.Length && (text.Substring(start, 2) is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>")) cursor++;
            result.Add(new(text[start..cursor], start, cursor));
            if (result.Count > MaximumSourceTokens) throw new GraphException("MaterialSourceTokenLimit");
        }
        return result;
    }

    private static List<Token>? Function(IReadOnlyList<Token> tokens, string name)
    {
        List<Token>? result = null;
        for (var index = 1; index < tokens.Count - 2; index++)
        {
            if (tokens[index].Text != name || tokens[index + 1].Text != "(") continue;
            var cursor = index + 2; var depth = 1;
            for (; cursor < tokens.Count && depth != 0; cursor++)
            { if (tokens[cursor].Text == "(") depth++; if (tokens[cursor].Text == ")") depth--; }
            if (cursor >= tokens.Count || tokens[cursor].Text != "{") continue;
            var start = ++cursor; depth = 1;
            for (; cursor < tokens.Count && depth != 0; cursor++)
            { if (tokens[cursor].Text == "{") depth++; if (tokens[cursor].Text == "}") depth--; }
            if (depth != 0) throw new GraphException("MaterialFunctionTruncated");
            if (result is not null) throw new GraphException("MaterialFunctionAmbiguous");
            result = tokens.Skip(start).Take(cursor - start - 1).ToList();
        }
        return result;
    }

    private static Candidate? ReadNormal(List<Token> tokens, CancellationToken cancellationToken)
    {
        var body = Function(tokens, "ComputeNormalMap");
        if (body is null) return null;
        var outputs = new List<(Value Value, int Start, int End)>();
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        var scopes = new Stack<Dictionary<string, Dictionary<string, Value>>>();
        scopes.Push(new(StringComparer.Ordinal));
        void Declare(string name)
        {
            if (scopes.Peek().ContainsKey(name)) throw new GraphException("MaterialNormalDeclarationAmbiguous");
            var saved = values.Where(item => item.Key == name || item.Key.StartsWith(name + ".", StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            scopes.Peek().Add(name, saved);
            foreach (var key in saved.Keys) values.Remove(key);
        }
        var statement = new List<Token>(); var count = 0;
        foreach (var token in body)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Text is "if" or "for" or "while" or "switch" or "?" or "do")
                throw new GraphException("MaterialNormalControlFlowUnsupported");
            if (token.Text == "{")
            {
                if (scopes.Count >= MaximumExpressionDepth) throw new GraphException("MaterialScopeDepthLimit");
                scopes.Push(new(StringComparer.Ordinal)); continue;
            }
            if (token.Text == "}")
            {
                if (scopes.Count <= 1) throw new GraphException("MaterialNormalScopeInvalid");
                foreach (var variable in scopes.Pop())
                {
                    foreach (var key in values.Keys.Where(key => key == variable.Key || key.StartsWith(variable.Key + ".", StringComparison.Ordinal)).ToArray()) values.Remove(key);
                    foreach (var saved in variable.Value) values[saved.Key] = saved.Value;
                }
                continue;
            }
            if (token.Text != ";") { statement.Add(token); continue; }
            if (++count > MaximumFunctionStatements) throw new GraphException("MaterialStatementLimit");
            var declaration = statement.Count >= 2 && statement[0].Text is "float" or "vec2" or "vec3" or "vec4" or "S_SURFACE" or "MaterialForNormalMap";
            if (declaration) Declare(statement[1].Text);
            var equals = statement.FindIndex(item => item.Text is "=" or "+=" or "-=" or "*=" or "/=");
            if (equals > 0)
            {
                var lhs = statement.Take(equals).ToList();
                if (declaration) lhs.RemoveAt(0);
                var key = string.Concat(lhs.Select(item => item.Text));
                if (lhs.Count == 1 || lhs.Count >= 3 && lhs[1].Text == ".")
                {
                    Value value;
                    try { value = new Expression(statement.Skip(equals + 1).ToList(), values).Read(); }
                    catch (GraphException error) when (error.Message == "MaterialExpressionUnsupported") { value = Value.Unknown; }
                    if (statement[equals].Text != "=") value = Binary(statement[equals].Text[..1], ReadValue(key, values), value);
                    var dot = key.LastIndexOf('.');
                    if (dot > 0 && IsSwizzle(key[(dot + 1)..]))
                    {
                        var target = key[..dot]; var swizzle = key[(dot + 1)..];
                        // Only whole XYZ/RGB writes are supported; other lane writes remain unresolved.
                        values[target] = swizzle is "rgb" or "xyz" && value.Width == 3 ? value : Value.Unknown;
                    }
                    else values[key] = value;
                    if (key.EndsWith(".fTsNormal", StringComparison.Ordinal))
                        outputs.Add((value, statement[equals + 1].Start, statement[^1].End));
                }
            }
            statement.Clear();
        }
        if (outputs.Count == 0) return null;
        if (outputs.Count != 1 || outputs[0].Value.Kind != ValueKind.Sample || outputs[0].Value.Channels != "rgb")
            throw new GraphException("MaterialNormalGraphUnsupported");
        var main = Function(tokens, "main");
        var consumer = Function(tokens, "ComputeSurfaceFromNormalMap");
        var decode = Function(tokens, "ComputeNormal");
        if (main is null || consumer is null || decode is null ||
            !Calls(main, "ComputeNormalMap") || !Calls(main, "ComputeSurfaceFromNormalMap") ||
            !Calls(consumer, "ComputeNormal") || !consumer.Any(item => item.Text == "fTsNormal") ||
            !StandardNormalDecode(tokens, decode))
            throw new GraphException("MaterialNormalConsumerUnsupported");
        var selected = outputs[0];
        return new(selected.Value.Slot, selected.Value.UvSet, selected.Value.Scale, selected.Value.Offset, selected.Start, selected.End);
    }

    private static bool Calls(IReadOnlyList<Token> tokens, string name) =>
        tokens.Select((token, index) => token.Text == name && index + 1 < tokens.Count && tokens[index + 1].Text == "(").Any(match => match);

    private enum DecodeValue { Unknown, SignedNormal, Basis, Perturbed, Normalized }
    private static bool StandardNormalDecode(List<Token> source, List<Token> body)
    {
        // The profile consumes RGB encoded in [0,1], transforms it through authored T/B/N,
        // and normalizes it. Temporary names and either equivalent matrix construction may vary.
        string[]? parameters = null;
        for (var i = 0; i + 2 < source.Count; i++)
        {
            if (source[i].Text != "ComputeNormal" || source[i + 1].Text != "(") continue;
            var cursor = i + 2; var names = new List<string>(); var valid = true;
            while (cursor < source.Count && source[cursor].Text != ")")
            {
                if (source[cursor].Text == "const") cursor++;
                if (cursor + 1 >= source.Count || source[cursor++].Text != "vec3") { valid = false; break; }
                names.Add(source[cursor++].Text);
                if (cursor < source.Count && source[cursor].Text == ",") cursor++;
                else break;
            }
            if (valid && names.Count == 4 && cursor + 1 < source.Count && source[cursor].Text == ")" && source[cursor + 1].Text == "{")
            { parameters = names.ToArray(); break; }
        }
        if (parameters is null || parameters.Distinct(StringComparer.Ordinal).Count() != 4) return false;
        if (body.Any(token => token.Text is "if" or "for" or "while" or "switch" or "?")) return false;
        var values = new Dictionary<string, DecodeValue>(StringComparer.Ordinal);
        var statement = new List<Token>();
        foreach (var token in body)
        {
            if (token.Text is "{" or "}") continue;
            if (token.Text != ";") { statement.Add(token); continue; }
            if (statement.Count == 0) continue;
            if (statement[0].Text == "return")
            {
                var returned = statement.Skip(1).Where(t => t.Text is not ("(" or ")")).ToArray();
                return returned.Length == 1 && values.GetValueOrDefault(returned[0].Text) == DecodeValue.Normalized ||
                    returned.Length == 4 && returned[0].Text == "normalize" && returned[2].Text == "*" &&
                    values.GetValueOrDefault(returned[1].Text) == DecodeValue.Basis && values.GetValueOrDefault(returned[3].Text) == DecodeValue.SignedNormal;
            }
            var equals = statement.FindIndex(t => t.Text == "=");
            if (equals > 0)
            {
                var target = statement[equals - 1].Text;
                var expression = statement.Skip(equals + 1).ToArray();
                var flat = expression.Where(t => t.Text is not ("(" or ")")).Select(t => t.Text).ToArray();
                var value = DecodeValue.Unknown;
                if (flat.Length == 6 && Number(flat[0], 2) && flat[1] == "*" && flat[2] == parameters[0] &&
                    flat[3] == "-" && flat[4] == "vec3" && Number(flat[5], 1)) value = DecodeValue.SignedNormal;
                else if (BasisExpression(expression, parameters)) value = DecodeValue.Basis;
                else if (flat.Length == 3 && flat[1] == "*" && values.GetValueOrDefault(flat[0]) == DecodeValue.Basis &&
                    values.GetValueOrDefault(flat[2]) == DecodeValue.SignedNormal) value = DecodeValue.Perturbed;
                else if (flat.Length == 2 && flat[0] == "normalize" && values.GetValueOrDefault(flat[1]) == DecodeValue.Perturbed)
                    value = DecodeValue.Normalized;
                // A direct normalize(matrix * signedNormal) is equivalent to the two assignments.
                else if (flat.Length == 4 && flat[0] == "normalize" && flat[2] == "*" &&
                    values.GetValueOrDefault(flat[1]) == DecodeValue.Basis && values.GetValueOrDefault(flat[3]) == DecodeValue.SignedNormal)
                    value = DecodeValue.Normalized;
                values[target] = value;
            }
            statement.Clear();
        }
        return false;
    }

    private static bool Number(string token, float expected) =>
        float.TryParse(token.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == expected;

    private sealed record BasisNode(string Name, IReadOnlyList<BasisNode> Arguments);
    private static bool BasisExpression(IReadOnlyList<Token> tokens, string[] parameters)
    {
        var cursor = 0;
        BasisNode? Read(int depth)
        {
            if (depth > MaximumExpressionDepth || cursor >= tokens.Count) return null;
            if (tokens[cursor].Text == "(")
            {
                cursor++; var nested = Read(depth + 1);
                if (cursor >= tokens.Count || tokens[cursor++].Text != ")") return null;
                return nested;
            }
            var name = tokens[cursor++].Text;
            if (cursor < tokens.Count && tokens[cursor].Text == ".")
            { cursor++; if (cursor >= tokens.Count) return null; name += "." + tokens[cursor++].Text; }
            var args = new List<BasisNode>();
            if (cursor < tokens.Count && tokens[cursor].Text == "(")
            {
                cursor++;
                while (cursor < tokens.Count && tokens[cursor].Text != ")")
                {
                    var argument = Read(depth + 1); if (argument is null) return null; args.Add(argument);
                    if (cursor < tokens.Count && tokens[cursor].Text == ",") cursor++;
                    else break;
                    if (args.Count > 4) return null;
                }
                if (cursor >= tokens.Count || tokens[cursor++].Text != ")") return null;
            }
            return new(name, args);
        }
        var root = Read(0);
        if (root is null || cursor != tokens.Count) return false;
        static bool Matrix(BasisNode node) => node.Name is "mat3" or "mat3x3" && node.Arguments.Count == 3;
        if (Matrix(root)) return root.Arguments.Select((arg, i) => arg.Arguments.Count == 0 && arg.Name == parameters[i + 1]).All(value => value);
        if (root.Name != "transpose" || root.Arguments.Count != 1 || !Matrix(root.Arguments[0])) return false;
        for (var row = 0; row < 3; row++)
        {
            var vector = root.Arguments[0].Arguments[row];
            if (vector.Name != "vec3" || vector.Arguments.Count != 3) return false;
            for (var column = 0; column < 3; column++)
                if (vector.Arguments[column].Arguments.Count != 0 || vector.Arguments[column].Name != parameters[column + 1] + "." + "xyz"[row]) return false;
        }
        return true;
    }

    private static int? MaterialFieldOffset(List<Token> tokens, string field)
    {
        var start = tokens.FindIndex(item => item.Text == "S_MATERIAL");
        if (start < 1 || tokens[start - 1].Text != "struct" || start + 1 >= tokens.Count || tokens[start + 1].Text != "{") return null;
        var cursor = start + 2; var offset = 0;
        while (cursor + 2 < tokens.Count && tokens[cursor].Text != "}")
        {
            var type = tokens[cursor++].Text; var name = tokens[cursor++].Text;
            if (tokens[cursor++].Text != ";") return null;
            var length = type switch { "vec4" => 16, "float" or "uint" => 4, _ => 0 };
            if (length == 0 || type == "vec4" && offset % 16 != 0) return null;
            if (name == field) return type == "uint" ? offset : null;
            offset = checked(offset + length);
        }
        return null;
    }

    private static bool IsSwizzle(string text) => text.Length is >= 1 and <= 4 && text.All(c => "rgbaxyzw".Contains(c));
    private static Value ReadValue(string name, Dictionary<string, Value> values)
    {
        if (values.TryGetValue(name, out var direct)) return direct;
        if (name.StartsWith("Surface.fUv", StringComparison.Ordinal) && int.TryParse(name[11..], NumberStyles.None, CultureInfo.InvariantCulture, out var uv) && uv is >= 0 and <= 3)
            return new(ValueKind.Coordinates, UvSet: uv, Scale: Vector2.One);
        var input = name switch
        {
            "Surface.fWsPosition" => MaterialInput.WorldPosition,
            "Surface.fVertexColor" => MaterialInput.VertexColor,
            "Surface.fVsDepth" => MaterialInput.ViewDepth,
            "Surface.fWsNormal" or "Surface.fWsGeometryNormal" => MaterialInput.WorldNormal,
            "Surface.fWsCameraDir" => MaterialInput.ViewDirection,
            _ => (MaterialInput?)null
        };
        if (input.HasValue) return InputValue(input.Value);
        if (name == "Surface.fVsPosition") return new(ValueKind.ViewPosition);
        if (name is "Surface.fVsNormal" or "Surface.fVsGeometryNormal") return new(ValueKind.ViewNormal);
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || !IsSwizzle(name[(dot + 1)..])) return Value.Unknown;
        var original = ReadValue(name[..dot], values);
        var channels = name[(dot + 1)..].Select(c => c switch { 'x' => 'r', 'y' => 'g', 'z' => 'b', 'w' => 'a', _ => c }).ToArray();
        var indices = channels.Select(c => "rgba".IndexOf(c)).ToArray();
        if (original.Kind == ValueKind.Constant && indices.All(i => i >= 0 && i < original.Width))
            return new(ValueKind.Constant, indices.Select(i => original.Numbers![i]).ToArray());
        if (original.Kind == ValueKind.Sample && indices.All(i => i >= 0 && i < original.Width))
            return original with { Channels = new string(indices.Select(i => original.Channels[i]).ToArray()),
                Node = new(MaterialExpressionOperation.Swizzle, (MaterialValueType)indices.Length, [ToNode(original)!], Components: new string(channels)) };
        var node = ToNode(original);
        if (node is not null && indices.All(i => i >= 0 && i < (int)node.ValueType))
            return ExpressionValue(new(MaterialExpressionOperation.Swizzle, (MaterialValueType)indices.Length, [node], Components: new string(channels)));
        return Value.Unknown;
    }

    private static Value Binary(string op, Value left, Value right)
    {
        if (op == "*" && (left.Kind == ValueKind.Matrix2 || right.Kind == ValueKind.Matrix2)) return MatrixMultiply(left, right);
        if (left.Kind == ValueKind.Constant && right.Kind == ValueKind.Constant)
        {
            var width = Math.Max(left.Width, right.Width);
            if (left.Width != right.Width && left.Width != 1 && right.Width != 1) return Value.Unknown;
            var numbers = new float[width];
            for (var i = 0; i < width; i++)
            {
                var a = left.Numbers![left.Width == 1 ? 0 : i]; var b = right.Numbers![right.Width == 1 ? 0 : i];
                numbers[i] = op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => float.NaN };
                if (!float.IsFinite(numbers[i])) return Value.Unknown;
            }
            return new(ValueKind.Constant, numbers);
        }
        if (op == "*" && left.Kind == ValueKind.Constant && right.Kind == ValueKind.Coordinates) return Binary(op, right, left);
        if (left.Kind != ValueKind.Coordinates || right.Kind != ValueKind.Constant || right.Width is not (1 or 2))
            return ExpressionBinary(op, left, right);
        var factor = new Vector2(right.Numbers![0], right.Numbers[right.Width == 1 ? 0 : 1]);
        var result = op switch
        {
            "*" => left with { Scale = left.Scale * factor, Offset = left.Offset * factor },
            "/" => left with { Scale = left.Scale / factor, Offset = left.Offset / factor },
            "+" => left with { Offset = left.Offset + factor },
            "-" => left with { Offset = left.Offset - factor },
            _ => Value.Unknown
        };
        return float.IsFinite(result.Scale.X) && float.IsFinite(result.Scale.Y) && float.IsFinite(result.Offset.X) && float.IsFinite(result.Offset.Y) ? result : Value.Unknown;
    }

    private sealed class Expression(List<Token> tokens, Dictionary<string, Value> values, IReadOnlySet<string>? identityCalls = null)
    {
        private int cursor;
        private int nodes;
        public Value Read()
        {
            var value = ReadBinary(0, 0);
            if (cursor != tokens.Count) throw new GraphException("MaterialExpressionUnsupported");
            return value;
        }
        private Value ReadBinary(int precedence, int depth)
        {
            if (depth > MaximumExpressionDepth) throw new GraphException("MaterialExpressionDepthLimit");
            var left = Primary(depth + 1);
            while (cursor < tokens.Count)
            {
                var op = tokens[cursor].Text; var level = op is "*" or "/" ? 20 : op is "+" or "-" ? 10 : -1;
                if (level < precedence) break;
                cursor++;
                left = Binary(op, left, ReadBinary(level + 1, depth + 1));
            }
            return left;
        }
        private Value Primary(int depth)
        {
            if (depth > MaximumExpressionDepth) throw new GraphException("MaterialExpressionDepthLimit");
            if (++nodes > MaximumExpressionNodes) throw new GraphException("MaterialExpressionNodeLimit");
            if (cursor >= tokens.Count) throw new GraphException("MaterialExpressionUnsupported");
            var name = tokens[cursor++].Text;
            if (name is "+" or "-")
            {
                var unary = Primary(depth + 1);
                if (unary.Kind is ValueKind.ViewPosition or ValueKind.ViewNormal)
                    return name == "+" ? unary : unary with { Numbers = [-(unary.Numbers?[0] ?? 1)] };
                return name == "+" ? unary : Binary("*", new(ValueKind.Constant, [-1f]), unary);
            }
            if (name == "(")
            { var value = ReadBinary(0, depth + 1); Need(")"); return Postfix(value); }
            if (float.TryParse(name.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return float.IsFinite(number) ? new(ValueKind.Constant, [number]) : Value.Unknown;
            if (name == "texture" && Peek("(")) return Postfix(Sample(depth + 1));
            if (Peek("("))
            {
                cursor++; var arguments = new List<Value>();
                do { arguments.Add(ReadBinary(0, depth + 1)); if (!Peek(",")) break; cursor++; } while (arguments.Count <= 4);
                Need(")");
                return Postfix(identityCalls?.Contains(name) == true && arguments.Count == 1 ? arguments[0] :
                    name is "vec2" or "vec3" or "vec4" ? ConstructValue(name[3] - '0', arguments) : PureCall(name, arguments));
            }
            while (Peek("."))
            { cursor++; if (cursor >= tokens.Count) throw new GraphException("MaterialExpressionUnsupported"); name += "." + tokens[cursor++].Text; }
            return ReadValue(name, values);
        }
        private Value Sample(int depth)
        {
            Need("("); Need("sampler2D"); Need("("); Need("g_rb2DTextures"); Need("["); Need("Material"); Need(".");
            if (cursor >= tokens.Count) throw new GraphException("MaterialExpressionUnsupported");
            var field = tokens[cursor++].Text;
            if (!field.StartsWith('s') || !int.TryParse(field.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var slot))
                throw new GraphException("MaterialExpressionUnsupported");
            Need("+"); Need("glslHack"); Need("]"); Need("."); Need("rHandle"); Need("."); Need("xy"); Need(")"); Need(",");
            var uv = ReadBinary(0, depth + 1); Need(")");
            var coordinates = ToNode(uv);
            if (coordinates is null || coordinates.ValueType != MaterialValueType.Vector2) return Value.Unknown;
            var node = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [coordinates], TextureSlot: slot);
            if (uv.Kind != ValueKind.Coordinates) return ExpressionValue(node);
            return new(ValueKind.Sample, UvSet: uv.UvSet, Scale: uv.Scale, Offset: uv.Offset, Slot: slot, Channels: "rgba", Node: node);
        }
        private bool Peek(string text) => cursor < tokens.Count && tokens[cursor].Text == text;
        private void Need(string text) { if (!Peek(text)) throw new GraphException("MaterialExpressionUnsupported"); cursor++; }
        private Value Postfix(Value value)
        {
            while (Peek("."))
            {
                cursor++;
                if (cursor >= tokens.Count || !IsSwizzle(tokens[cursor].Text)) throw new GraphException("MaterialExpressionUnsupported");
                var alias = new Dictionary<string, Value>(StringComparer.Ordinal) { ["value"] = value };
                value = ReadValue("value." + tokens[cursor++].Text, alias);
            }
            return value;
        }
    }
}
