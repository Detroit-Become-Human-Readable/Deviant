using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Formats.Shaders;

public sealed partial class NativeMaterialReader
{
    private static Value ExpressionValue(MaterialExpression expression) => new(ValueKind.Expression, Node: expression);
    private static MaterialExpression ConstantNode(params float[] values) =>
        new(MaterialExpressionOperation.Constant, (MaterialValueType)values.Length, [], Constants: values);
    private static Value InputValue(MaterialInput input) => ExpressionValue(new(MaterialExpressionOperation.Input,
        input is MaterialInput.Uv0 or MaterialInput.Uv1 or MaterialInput.Uv2 or MaterialInput.Uv3 ? MaterialValueType.Vector2 :
        input == MaterialInput.VertexColor ? MaterialValueType.Vector4 : input == MaterialInput.ViewDepth ? MaterialValueType.Scalar : MaterialValueType.Vector3,
        [], Input: input));
    private static MaterialExpression? ToNode(Value value)
    {
        if (value.Node is not null) return value.Node;
        if (value.Kind == ValueKind.Constant) return ConstantNode(value.Numbers!);
        if (value.Kind != ValueKind.Coordinates) return null;
        var node = InputValue((MaterialInput)((int)MaterialInput.Uv0 + value.UvSet)).Node!;
        if (value.Scale != Vector2.One) node = new(MaterialExpressionOperation.Multiply, MaterialValueType.Vector2, [node, ConstantNode(value.Scale.X, value.Scale.Y)]);
        if (value.Offset != Vector2.Zero) node = new(MaterialExpressionOperation.Add, MaterialValueType.Vector2, [node, ConstantNode(value.Offset.X, value.Offset.Y)]);
        return node;
    }
    private static Value ConstructValue(int width, IReadOnlyList<Value> arguments)
    {
        if (arguments.Count == 1 && arguments[0].Width == width) return arguments[0];
        if (arguments.All(value => value.Kind == ValueKind.Constant))
        {
            var numbers = arguments.SelectMany(value => value.Numbers!).ToArray();
            if (numbers.Length == 1) numbers = Enumerable.Repeat(numbers[0], width).ToArray();
            return numbers.Length == width ? new(ValueKind.Constant, numbers) : Value.Unknown;
        }
        var nodes = arguments.Select(ToNode).ToArray();
        if (nodes.Any(node => node is null)) return Value.Unknown;
        var total = nodes.Sum(node => (int)node!.ValueType);
        if (total != width && !(arguments.Count == 1 && total == 1)) return Value.Unknown;
        return ExpressionValue(new(MaterialExpressionOperation.Construct, (MaterialValueType)width, nodes.Select(node => node!).ToArray()));
    }
    private static Value ExpressionBinary(string operation, Value left, Value right)
    {
        var a = ToNode(left); var b = ToNode(right);
        if (a is null || b is null) return Value.Unknown;
        var width = Math.Max((int)a.ValueType, (int)b.ValueType);
        if (a.ValueType != b.ValueType && a.ValueType != MaterialValueType.Scalar && b.ValueType != MaterialValueType.Scalar) return Value.Unknown;
        var op = operation switch
        {
            "+" => MaterialExpressionOperation.Add, "-" => MaterialExpressionOperation.Subtract,
            "*" => MaterialExpressionOperation.Multiply, "/" => MaterialExpressionOperation.Divide,
            _ => (MaterialExpressionOperation?)null
        };
        return op.HasValue ? ExpressionValue(new(op.Value, (MaterialValueType)width, [a, b])) : Value.Unknown;
    }
    private static Value MatrixMultiply(Value left, Value right)
    {
        var matrix = left.Kind == ValueKind.Matrix2 ? left : right;
        var vector = left.Kind == ValueKind.Matrix2 ? right : left;
        var m = ToNode(matrix); var v = ToNode(vector);
        if (m is null || v?.ValueType != MaterialValueType.Vector2) return Value.Unknown;
        MaterialExpression Component(MaterialExpression node, int index) =>
            new(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [node], Components: "rgba"[index].ToString());
        MaterialExpression Mul(int mi, int vi) => new(MaterialExpressionOperation.Multiply, MaterialValueType.Scalar, [Component(m, mi), Component(v, vi)]);
        var column = left.Kind == ValueKind.Matrix2;
        var x = new MaterialExpression(MaterialExpressionOperation.Add, MaterialValueType.Scalar, [Mul(0, 0), Mul(column ? 2 : 1, 1)]);
        var y = new MaterialExpression(MaterialExpressionOperation.Add, MaterialValueType.Scalar, [Mul(column ? 1 : 2, 0), Mul(3, 1)]);
        return ExpressionValue(new(MaterialExpressionOperation.Construct, MaterialValueType.Vector2, [x, y]));
    }
    private static Value PureCall(string name, IReadOnlyList<Value> arguments)
    {
        if (name == "mat2" && arguments.Count == 4 && arguments.All(value => ToNode(value)?.ValueType == MaterialValueType.Scalar))
            return new(ValueKind.Matrix2, Node: new(MaterialExpressionOperation.Construct, MaterialValueType.Vector4, arguments.Select(value => ToNode(value)!).ToArray()));
        if (name == "normalize" && arguments.Count == 1 && arguments[0].Kind == ValueKind.ViewPosition)
        {
            var sign = arguments[0].Numbers?[0] ?? 1;
            return Binary("*", InputValue(MaterialInput.ViewDirection), new(ValueKind.Constant, [-sign]));
        }
        if (name == "dot" && arguments.Count == 2 && arguments[0].Kind == ValueKind.ViewNormal &&
            ToNode(arguments[1]) is { } direction && ContainsInput(direction, MaterialInput.ViewDirection))
        {
            var normal = Binary("*", InputValue(MaterialInput.WorldNormal), new(ValueKind.Constant, [arguments[0].Numbers?[0] ?? 1]));
            return ExpressionValue(new(MaterialExpressionOperation.Dot, MaterialValueType.Scalar, [ToNode(normal)!, direction]));
        }
        if (name == "saturate" && arguments.Count == 1) return PureCall("clamp", [arguments[0], new(ValueKind.Constant, [0]), new(ValueKind.Constant, [1])]);
        var operation = name switch
        {
            "mix" => MaterialExpressionOperation.Mix, "clamp" => MaterialExpressionOperation.Clamp,
            "pow" => MaterialExpressionOperation.Pow, "min" => MaterialExpressionOperation.Min, "max" => MaterialExpressionOperation.Max,
            "abs" => MaterialExpressionOperation.Abs, "dot" => MaterialExpressionOperation.Dot, "normalize" => MaterialExpressionOperation.Normalize,
            "sin" => MaterialExpressionOperation.Sin, "cos" => MaterialExpressionOperation.Cos, "floor" => MaterialExpressionOperation.Floor,
            "fract" => MaterialExpressionOperation.Fract, "step" => MaterialExpressionOperation.Step, "smoothstep" => MaterialExpressionOperation.Smoothstep,
            _ => (MaterialExpressionOperation?)null
        };
        if (!operation.HasValue || arguments.Any(value => ToNode(value) is null)) return Value.Unknown;
        var expected = operation.Value is MaterialExpressionOperation.Mix or MaterialExpressionOperation.Clamp or MaterialExpressionOperation.Smoothstep ? 3 :
            operation.Value is MaterialExpressionOperation.Pow or MaterialExpressionOperation.Min or MaterialExpressionOperation.Max or MaterialExpressionOperation.Dot or MaterialExpressionOperation.Step ? 2 : 1;
        if (arguments.Count != expected) return Value.Unknown;
        var nodes = arguments.Select(value => ToNode(value)!).ToArray();
        var width = nodes.Max(node => (int)node.ValueType);
        if (nodes.Any(node => (int)node.ValueType != width && node.ValueType != MaterialValueType.Scalar)) return Value.Unknown;
        if (operation == MaterialExpressionOperation.Dot && (nodes[0].ValueType != nodes[1].ValueType || width == 1)) return Value.Unknown;
        if (operation == MaterialExpressionOperation.Normalize && width == 1) return Value.Unknown;
        return ExpressionValue(new(operation.Value, operation == MaterialExpressionOperation.Dot ? MaterialValueType.Scalar : (MaterialValueType)width, nodes));
    }
    private static bool ContainsInput(MaterialExpression node, MaterialInput input)
    {
        var stack = new Stack<MaterialExpression>(); stack.Push(node); var count = 0;
        while (stack.Count > 0 && ++count <= MaximumExpressionNodes)
        { var item = stack.Pop(); if (item.Input == input) return true; foreach (var child in item.Arguments) stack.Push(child); }
        return false;
    }

    private MaterialSurfaceGraph? ReadSurfaceGraph(IReadOnlyList<(StoredGlslSource Source, List<Token> Tokens)> sources,
        NativeShaderProgramLayout layout, TextureShaderSlotsDocument slots, CancellationToken token)
    {
        var diagnostics = new List<string>();
        MaterialExpression? color = null, opacity = null, cutoff = null;
        var sawColor = false; var sawOpacity = false; var sawCutoff = false;
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var body = Function(source.Tokens, "ComputeHypershade"); if (body is null) continue;
            var main = Function(source.Tokens, "main"); if (main is null || !Calls(main, "ComputeHypershade")) continue;
            var identityCalls = SourceIdentityCalls(source.Tokens);
            var values = new Dictionary<string, Value>(StringComparer.Ordinal);
            for (var i = 0; i < layout.ScalarConstants.Count; i++)
                values["Material.c" + i.ToString(CultureInfo.InvariantCulture)] = UseSerializedScalarInitialValues ?
                    new(ValueKind.Constant, [layout.ScalarConstants[i].InitialValue]) : Value.Unknown;
            var normal = Function(source.Tokens, "ComputeNormalMap");
            if (normal is not null) EvaluateSurfaceBody(normal, values, source.Source, identityCalls, token);
            EvaluateSurfaceBody(body, values, source.Source, identityCalls, token);
            var localColor = FindOutput(values, "baseColor", "bcbaseColor", "color");
            var localOpacity = FindOutput(values, "transparency");
            var localCutoff = FindOutput(values, "alphaTestValue");
            sawColor |= values.Keys.Any(key => key.EndsWith(".baseColor", StringComparison.Ordinal) || key.EndsWith(".bcbaseColor", StringComparison.Ordinal) || key.EndsWith(".color", StringComparison.Ordinal));
            sawOpacity |= values.Keys.Any(key => key.EndsWith(".transparency", StringComparison.Ordinal));
            Merge("BaseColor", localColor, 3, ref color);
            Merge("Opacity", localOpacity, 1, ref opacity);
            // A cutoff is applied only when the source's pixel entry actually discards by this field.
            if (main is not null && main.Any(item => item.Text == "discard") && main.Any(item => item.Text == "alphaTestValue"))
            {
                sawCutoff = true;
                Merge("AlphaCutoff", localCutoff, 1, ref cutoff);
            }

            void Merge(string name, Value value, int width, ref MaterialExpression? agreed)
            {
                var node = ToNode(value);
                if (node is null) return;
                if ((int)node.ValueType != width) { diagnostics.Add($"Material{name}WidthUnsupported"); return; }
                try { ValidateGraph(node, source.Tokens, layout, slots, token); }
                catch (GraphException error) { diagnostics.Add($"Material{name}:{error.Message}"); return; }
                if (conflicting.Contains(name)) return;
                if (agreed is not null && SemanticKey(agreed) != SemanticKey(node))
                { conflicting.Add(name); agreed = null; diagnostics.Add($"Material{name}SourcesDisagree"); return; }
                agreed ??= node;
            }
        }
        if (color is null && opacity is null && cutoff is null) return null;
        if (sawColor && color is null) diagnostics.Add("MaterialBaseColorUnsupported");
        if (sawOpacity && opacity is null) diagnostics.Add("MaterialOpacityUnsupported");
        if (sawCutoff && cutoff is null) diagnostics.Add("MaterialAlphaCutoffUnsupported");
        if (UseSerializedScalarInitialValues && layout.ScalarConstants.Count != 0) diagnostics.Add("MaterialPreviewUsesSerializedScalarInitialValues");
        var inputs = new HashSet<MaterialInput>(); var textureSlots = new HashSet<int>();
        foreach (var output in new[] { color, opacity, cutoff }.OfType<MaterialExpression>())
        {
            var stack = new Stack<MaterialExpression>(); stack.Push(output); var visited = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0)
            {
                var node = stack.Pop(); if (!visited.Add(node)) continue;
                if (node.Input.HasValue) inputs.Add(node.Input.Value); if (node.TextureSlot.HasValue) textureSlots.Add(node.TextureSlot.Value);
                foreach (var argument in node.Arguments) stack.Push(argument);
            }
        }
        return new(color, opacity, cutoff, inputs.OrderBy(input => input).ToArray(), textureSlots.Order().ToArray(), diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    private static Value FindOutput(Dictionary<string, Value> values, params string[] fields)
    {
        foreach (var field in fields)
        {
            var found = values.Where(value => value.Key.EndsWith("." + field, StringComparison.Ordinal)).Select(value => value.Value).ToArray();
            if (found.Length == 1) return found[0];
            if (found.Length > 1) return Value.Unknown;
        }
        return Value.Unknown;
    }
    private static HashSet<string> SourceIdentityCalls(List<Token> tokens)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i + 9 < tokens.Count; i++)
            if (tokens[i - 1].Text is "float" or "vec2" or "vec3" or "vec4" && tokens[i + 1].Text == "(" &&
                tokens[i + 2].Text == tokens[i - 1].Text && tokens[i + 4].Text == ")" && tokens[i + 5].Text == "{" &&
                tokens[i + 6].Text == "return" && tokens[i + 7].Text == tokens[i + 3].Text && tokens[i + 8].Text == ";" && tokens[i + 9].Text == "}")
                result.Add(tokens[i].Text);
        return result;
    }
    private static void EvaluateSurfaceBody(List<Token> body, Dictionary<string, Value> values, StoredGlslSource source, IReadOnlySet<string> identityCalls, CancellationToken token)
    {
        var scopes = new Stack<(bool Conditional, Dictionary<string, Dictionary<string, Value>> Locals)>();
        scopes.Push((false, new(StringComparer.Ordinal)));
        var pendingConditional = false; var statement = new List<Token>(); var statements = 0;
        for (var cursor = 0; cursor < body.Count; cursor++)
        {
            token.ThrowIfCancellationRequested(); var current = body[cursor];
            if (current.Text is "if" or "for" or "while" or "switch")
            {
                pendingConditional = true; statement.Clear();
                if (cursor + 1 < body.Count && body[cursor + 1].Text == "(")
                { cursor += 2; var depth = 1; for (; cursor < body.Count && depth > 0; cursor++) { if (body[cursor].Text == "(") depth++; if (body[cursor].Text == ")") depth--; } cursor--; }
                continue;
            }
            if (current.Text == "else") { pendingConditional = true; continue; }
            if (current.Text == "{")
            {
                if (scopes.Count >= MaximumExpressionDepth) throw new GraphException("MaterialScopeDepthLimit");
                scopes.Push((pendingConditional || scopes.Peek().Conditional, new(StringComparer.Ordinal))); pendingConditional = false; continue;
            }
            if (current.Text == "}")
            {
                if (scopes.Count <= 1) throw new GraphException("MaterialSurfaceScopeInvalid");
                foreach (var local in scopes.Pop().Locals)
                {
                    foreach (var key in values.Keys.Where(key => key == local.Key || key.StartsWith(local.Key + ".", StringComparison.Ordinal)).ToArray()) values.Remove(key);
                    foreach (var saved in local.Value) values[saved.Key] = saved.Value;
                }
                continue;
            }
            if (current.Text != ";") { statement.Add(current); continue; }
            if (++statements > MaximumFunctionStatements) throw new GraphException("MaterialStatementLimit");
            if (statement.Count == 0) continue;
            var declaration = statement.Count >= 2 && statement[0].Text is "float" or "vec2" or "vec3" or "vec4" or "mat2" or "S_SURFACE" or "S_MATERIAL_ATTRIBUTES" or "MaterialForNormalMap";
            if (declaration)
            {
                var name = statement[1].Text;
                if (scopes.Peek().Locals.ContainsKey(name)) throw new GraphException("MaterialSurfaceDeclarationAmbiguous");
                scopes.Peek().Locals[name] = values.Where(value => value.Key == name || value.Key.StartsWith(name + ".", StringComparison.Ordinal)).ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
            }
            var equals = statement.FindIndex(item => item.Text is "=" or "+=" or "-=" or "*=" or "/=");
            if (equals > 0)
            {
                var lhs = statement.Take(equals).ToList(); if (declaration) lhs.RemoveAt(0);
                var key = string.Concat(lhs.Select(item => item.Text));
                Value value;
                try { value = new Expression(statement.Skip(equals + 1).ToList(), values, identityCalls).Read(); }
                catch (GraphException error) when (error.Message == "MaterialExpressionUnsupported") { value = Value.Unknown; }
                if (pendingConditional || scopes.Peek().Conditional) value = Value.Unknown;
                if (statement[equals].Text != "=") value = Binary(statement[equals].Text[..1], ReadValue(key, values), value);
                var node = ToNode(value);
                if (node is not null)
                {
                    var start = statement[equals + 1].Start; var end = statement[^1].End;
                    var offset = Encoding.UTF8.GetByteCount(source.Text!.AsSpan(0, start));
                    var length = Encoding.UTF8.GetByteCount(source.Text.AsSpan(start, end - start));
                    value = value with { Node = node with { SourceExtent = new(source.DecodedExtent.Container, source.DecodedExtent.Offset + offset, length) } };
                }
                var dot = key.LastIndexOf('.');
                if (dot > 0 && IsSwizzle(key[(dot + 1)..])) WriteComponents(key[..dot], key[(dot + 1)..], value, values);
                else
                {
                    foreach (var old in values.Keys.Where(old => old.StartsWith(key + ".", StringComparison.Ordinal)).ToArray()) values.Remove(old);
                    values[key] = value;
                }
            }
            statement.Clear(); pendingConditional = false;
        }
    }
    private static void WriteComponents(string target, string components, Value value, Dictionary<string, Value> values)
    {
        var indices = components.Select(c => "rgbaxyzw".IndexOf(c) % 4).ToArray();
        var previous = values.GetValueOrDefault(target, Value.Unknown);
        var width = Math.Max(previous.Width, indices.Max() + 1);
        if (width is < 1 or > 4 || value.Width != 1 && value.Width != indices.Length) { values[target] = Value.Unknown; return; }
        var lanes = new Value[width];
        var aliases = new Dictionary<string, Value>(StringComparer.Ordinal) { ["old"] = previous, ["new"] = value };
        for (var i = 0; i < width; i++) lanes[i] = ReadValue("old." + "rgba"[i], aliases);
        for (var i = 0; i < indices.Length; i++) lanes[indices[i]] = value.Width == 1 ? value : ReadValue("new." + "rgba"[i], aliases);
        // Keep explicit lane values even if an unrelated lane is undefined.
        for (var i = 0; i < width; i++) values[target + "." + "rgba"[i]] = lanes[i];
        values[target] = ConstructValue(width, lanes);
        var rgb = ConstructValue(3, lanes.Take(3).ToArray());
        if (width >= 3) { values[target + ".rgb"] = rgb; values[target + ".xyz"] = rgb; }
    }
    private static void ValidateGraph(MaterialExpression root, List<Token> source, NativeShaderProgramLayout layout,
        TextureShaderSlotsDocument slots, CancellationToken token)
    {
        var stack = new Stack<(MaterialExpression Node, int Depth)>(); stack.Push((root, 0));
        var seen = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var (node, depth) = stack.Pop();
            if (depth > MaximumExpressionDepth) throw new GraphException("MaterialExpressionDepthLimit");
            if (!seen.Add(node)) continue;
            if (seen.Count > MaximumExpressionNodes) throw new GraphException("MaterialExpressionNodeLimit");
            if (node.Constants?.Any(value => !float.IsFinite(value)) == true) throw new GraphException("MaterialExpressionValueInvalid");
            if (node.TextureSlot is { } slot)
            {
                if (slot < 0 || slot >= slots.Slots.Count || !slots.Slots[slot].Reference.IsBound) throw new GraphException("MaterialSamplerSlotInvalid");
                if (MaterialFieldOffset(source, "s" + slot.ToString(CultureInfo.InvariantCulture)) != 32 + 4 * layout.AlignedScalarConstantCount + 4 * slot)
                    throw new GraphException("MaterialSamplerLayoutMismatch");
            }
            foreach (var child in node.Arguments) stack.Push((child, depth + 1));
        }
    }
    private static string SemanticKey(MaterialExpression root)
    {
        var cache = new Dictionary<MaterialExpression, string>(ReferenceEqualityComparer.Instance);
        string Key(MaterialExpression node)
        {
            if (cache.TryGetValue(node, out var value)) return value;
            var canonical = ((int)node.Operation).ToString(CultureInfo.InvariantCulture) + ":" + (int)node.ValueType + ":" + node.Input + ":" + node.TextureSlot + ":" + node.Components +
                ":" + string.Join(",", node.Constants?.Select(number => number.ToString("R", CultureInfo.InvariantCulture)) ?? []) + "(" + string.Join(";", node.Arguments.Select(Key)) + ")";
            value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            cache[node] = value; return value;
        }
        return Key(root);
    }
}
