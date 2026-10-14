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
        input == MaterialInput.VertexColor ? MaterialValueType.Vector4 : input is MaterialInput.ViewDepth or MaterialInput.SceneViewDepth ? MaterialValueType.Scalar : MaterialValueType.Vector3,
        [], Input: input));
    private static MaterialExpression? ToNode(Value value)
    {
        if (value.Node is not null) return value.Node;
        if (value.Kind == ValueKind.Lanes)
        {
            var lanes = value.Lanes!.Select(ToNode).ToArray();
            return lanes.Any(lane => lane?.ValueType != MaterialValueType.Scalar) ? null :
                new(MaterialExpressionOperation.Construct, (MaterialValueType)lanes.Length, lanes.Select(lane => lane!).ToArray());
        }
        if (value.Kind == ValueKind.Constant) return ConstantNode(value.Numbers!);
        if (value.Kind == ValueKind.ViewNormal)
            return Binary("*", InputValue(MaterialInput.ViewNormal), new(ValueKind.Constant, [value.Numbers?[0] ?? 1])).Node;
        if (value.Kind != ValueKind.Coordinates) return null;
        var node = InputValue((MaterialInput)((int)MaterialInput.Uv0 + value.UvSet)).Node!;
        if (value.Scale != Vector2.One) node = new(MaterialExpressionOperation.Multiply, MaterialValueType.Vector2, [node, ConstantNode(value.Scale.X, value.Scale.Y)]);
        if (value.Offset != Vector2.Zero) node = new(MaterialExpressionOperation.Add, MaterialValueType.Vector2, [node, ConstantNode(value.Offset.X, value.Offset.Y)]);
        return node;
    }
    private static Value ConstructValue(int width, IReadOnlyList<Value> arguments)
    {
        if (arguments.Count == 1 && arguments[0].Width == width) return arguments[0];
        if (arguments.Any(value => value.Kind == ValueKind.Lanes))
        {
            var lanes = arguments.SelectMany(value => value.Kind == ValueKind.Lanes ? value.Lanes! :
                Enumerable.Range(0, value.Width).Select(i => ReadLane(value, i))).ToArray();
            if (lanes.Length == 1) lanes = Enumerable.Repeat(lanes[0], width).ToArray();
            return lanes.Length == width ? JoinLanes(lanes) : Value.Unknown;
        }
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
        if (name is "mix" or "clamp" or "pow" or "min" or "max" or "abs" or "sin" or "cos" or
            "floor" or "fract" or "step" or "smoothstep" or "saturate" or "sqrt" or "lessThan")
        {
            var widths = arguments.Select(value => value.Width != 0 ? value.Width : (int)(ToNode(value)?.ValueType ?? 0)).ToArray();
            if (!ValidCallWidths(name, widths)) return Value.Unknown;
        }
        if (name == "lessThan" && arguments.Count == 2)
            return Binary("-", new(ValueKind.Constant, [1]), PureCall("step", [arguments[1], arguments[0]]));
        if (arguments.Any(value => value.Kind == ValueKind.Lanes) && name is
            "mix" or "clamp" or "pow" or "min" or "max" or "abs" or "sin" or "cos" or
            "floor" or "fract" or "step" or "smoothstep" or "saturate" or "sqrt" or "lessThan")
        {
            var laneWidth = arguments.Max(value => value.Width);
            if (arguments.Any(value => value.Width != laneWidth && value.Width != 1)) return Value.Unknown;
            return JoinLanes(Enumerable.Range(0, laneWidth).Select(i => PureCall(name,
                arguments.Select(value => ReadLane(value, value.Width == 1 ? 0 : i)).ToArray())).ToArray());
        }
        if (name == "sqrt" && arguments.Count == 1)
            return PureCall("pow", [arguments[0], new(ValueKind.Constant,
                Enumerable.Repeat(.5f, arguments[0].Width != 0 ? arguments[0].Width : (int)ToNode(arguments[0])!.ValueType).ToArray())]);
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

    private static bool ValidCallWidths(string name, IReadOnlyList<int> widths)
    {
        if (widths.Any(width => width is < 1 or > 4)) return false;
        return name switch
        {
            "mix" => widths.Count == 3 && widths[0] == widths[1] && (widths[2] == 1 || widths[2] == widths[0]),
            "clamp" => widths.Count == 3 && widths[1] == widths[2] && (widths[1] == 1 || widths[1] == widths[0]),
            "smoothstep" => widths.Count == 3 && widths[0] == widths[1] && (widths[0] == 1 || widths[0] == widths[2]),
            "step" => widths.Count == 2 && (widths[0] == 1 || widths[0] == widths[1]),
            "min" or "max" => widths.Count == 2 && (widths[1] == 1 || widths[0] == widths[1]),
            "pow" => widths.Count == 2 && widths[0] == widths[1],
            "lessThan" => widths.Count == 2 && widths[0] > 1 && widths[0] == widths[1],
            _ => widths.Count == 1
        };
    }
    private static bool ContainsInput(MaterialExpression node, MaterialInput input)
    {
        var stack = new Stack<MaterialExpression>(); stack.Push(node); var count = 0;
        while (stack.Count > 0 && ++count <= MaximumExpressionNodes)
        { var item = stack.Pop(); if (item.Input == input) return true; foreach (var child in item.Arguments) stack.Push(child); }
        return false;
    }

    private static Value JoinLanes(IReadOnlyList<Value> lanes)
    {
        if (lanes.Count is < 1 or > 4) return Value.Unknown;
        if (lanes.Count == 1) return lanes[0];
        if (lanes.All(lane => lane.Kind == ValueKind.Constant && lane.Width == 1))
            return new(ValueKind.Constant, lanes.Select(lane => lane.Numbers![0]).ToArray());
        var nodes = lanes.Select(ToNode).ToArray();
        if (nodes.All(node => node?.ValueType == MaterialValueType.Scalar))
            return ExpressionValue(new(MaterialExpressionOperation.Construct, (MaterialValueType)nodes.Length,
                nodes.Select(node => node!).ToArray()));
        return new(ValueKind.Lanes, Lanes: lanes);
    }

    private static Value ReadLane(Value value, int lane)
    {
        if (value.Kind == ValueKind.Lanes)
            return lane >= 0 && lane < value.Width ? value.Lanes![lane] : Value.Unknown;
        return ReadValue("lane." + "rgba"[lane], new Dictionary<string, Value>(StringComparer.Ordinal) { ["lane"] = value });
    }

    private MaterialSurfaceGraph? ReadSurfaceGraph(IReadOnlyList<(StoredGlslSource Source, List<Token> Tokens)> sources,
        NativeShaderProgramLayout layout, TextureShaderSlotsDocument slots, CancellationToken token)
    {
        var diagnostics = new List<string>();
        MaterialExpression? color = null, opacity = null, cutoff = null;
        var sawColor = false; var sawOpacity = false; var sawCutoff = false;
        bool? unlit = null;
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        var preferPixelCoverage = sources.Any(source =>
        {
            token.ThrowIfCancellationRequested();
            var main = Function(source.Tokens, "main");
            if (main is null) return false;
            foreach (var producer in new[] { "ComputeHypershade", "ComputeTransparencyHypershade" })
                if (HypershadeResultVariable(main, producer) is { } result &&
                    (DirectPixelAlphaConsumer(source.Tokens, main, result, producer) ||
                     ZeroAlphaConditionConsumesCoverage(source.Tokens, main, result, producer))) return true;
            return false;
        });
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var body = Function(source.Tokens, "ComputeHypershade"); if (body is null) continue;
            var main = Function(source.Tokens, "main"); if (main is null || !Calls(main, "ComputeHypershade")) continue;
            var resultVariable = HypershadeResultVariable(main);
            var directPixelAlpha = resultVariable is not null &&
                DirectPixelAlphaConsumer(source.Tokens, main, resultVariable);
            var discardConsumesAlpha = resultVariable is not null &&
                DiscardConditionConsumesAlpha(main, resultVariable);
            var identityCalls = SourceIdentityCalls(source.Tokens);
            var values = new Dictionary<string, Value>(StringComparer.Ordinal);
            for (var i = 0; i < layout.ScalarConstants.Count; i++)
                values["Material.c" + i.ToString(CultureInfo.InvariantCulture)] = UseSerializedScalarInitialValues ?
                    new(ValueKind.Constant, [layout.ScalarConstants[i].InitialValue]) : Value.Unknown;
            for (var i = 0; i < layout.ArrayConstants.Count; i++)
            {
                var field = "c" + i.ToString(CultureInfo.InvariantCulture);
                var qualified = MaterialFieldOffset(source.Tokens, field, "S_MATERIAL_INSTANCE", "float") == checked(i * 4);
                values["MaterialInstance." + field] = qualified && UseSerializedScalarInitialValues &&
                    NativeMaterialInstanceExpression.TryEvaluate(layout.ArrayConstants[i], JointWorldPositions,
                        out var initial, out _, token)
                    ? new(ValueKind.Constant, [initial]) : Value.Unknown;
            }
            var transparency = Function(source.Tokens, "ComputeTransparencyHypershade");
            var transparencyVariable = HypershadeResultVariable(main, "ComputeTransparencyHypershade");
            var transparencyOpacity = Value.Unknown;
            var transparencyCutoff = Value.Unknown;
            var consumesTransparency = false;
            var testsTransparency = false;
            var zeroAlphaCoverage = false;
            if (transparency is not null && transparencyVariable is not null && Calls(main, "ComputeTransparencyHypershade"))
            {
                var transparencyDepth = TryProjectionDepthSource(source.Tokens, transparency, ProjectionDepthNormalizationDistance);
                EvaluateSurfaceBody(transparency, values, source.Source, identityCalls, token, transparencyDepth);
                transparencyOpacity = FindOutput(values, "transparency");
                transparencyCutoff = FindOutput(values, "alphaTestValue");
                consumesTransparency = DirectPixelAlphaConsumer(source.Tokens, main, transparencyVariable, "ComputeTransparencyHypershade");
                testsTransparency = DiscardConditionConsumesAlpha(main, transparencyVariable, "ComputeTransparencyHypershade");
                zeroAlphaCoverage = ZeroAlphaConditionConsumesCoverage(source.Tokens, main, transparencyVariable,
                    "ComputeTransparencyHypershade");
                if (zeroAlphaCoverage)
                    transparencyOpacity = Binary("*", transparencyOpacity,
                        PureCall("step", [transparencyCutoff, transparencyOpacity]));
            }
            var normal = Function(source.Tokens, "ComputeNormalMap");
            if (normal is not null)
            {
                var normalDepth = TryProjectionDepthSource(source.Tokens, normal, ProjectionDepthNormalizationDistance);
                EvaluateSurfaceBody(normal, values, source.Source, identityCalls, token, normalDepth);
            }
            var surfaceDepth = TryProjectionDepthSource(source.Tokens, body, ProjectionDepthNormalizationDistance);
            EvaluateSurfaceBody(body, values, source.Source, identityCalls, token, surfaceDepth);
            var localColor = FindOutput(values, "baseColor", "bcbaseColor", "diffuseColor", "color");
            var localOpacity = directPixelAlpha || discardConsumesAlpha
                ? FindOutput(values, "transparency") : consumesTransparency || testsTransparency || zeroAlphaCoverage ? transparencyOpacity : Value.Unknown;
            var localCutoff = discardConsumesAlpha ? FindOutput(values, "alphaTestValue") : testsTransparency ? transparencyCutoff : Value.Unknown;
            var selectedCoverage = !preferPixelCoverage || directPixelAlpha || consumesTransparency || zeroAlphaCoverage;
            var terminal = ReadTerminalOutput(source.Tokens, main, body, resultVariable, values, identityCalls, source.Source);
            bool sourceUnlit = terminal is not null;
            if (terminal is not null)
            {
                var aliases = new Dictionary<string, Value>(StringComparer.Ordinal)
                { ["pixel"] = ExpressionValue(terminal) };
                localColor = ReadValue("pixel.rgb", aliases);
                localOpacity = ReadValue("pixel.a", aliases);
                localCutoff = Value.Unknown;
                selectedCoverage = true;
                sawColor = true; sawOpacity = true;
            }
            if (!selectedCoverage) { localOpacity = Value.Unknown; localCutoff = Value.Unknown; }
            sawColor |= values.Keys.Any(key => key.EndsWith(".baseColor", StringComparison.Ordinal) || key.EndsWith(".bcbaseColor", StringComparison.Ordinal) || key.EndsWith(".diffuseColor", StringComparison.Ordinal) || key.EndsWith(".color", StringComparison.Ordinal));
            sawOpacity |= selectedCoverage && values.Keys.Any(key => key.EndsWith(".transparency", StringComparison.Ordinal));
            sawCutoff |= selectedCoverage && !zeroAlphaCoverage && values.Keys.Any(key => key.EndsWith(".alphaTestValue", StringComparison.Ordinal));
            Merge("BaseColor", localColor, 3, ref color);
            Merge("Opacity", localOpacity, 1, ref opacity);
            if (ToNode(localColor) is not null)
            {
                if (unlit.HasValue && unlit.Value != sourceUnlit)
                { color = null; conflicting.Add("BaseColor"); diagnostics.Add("MaterialShadingSourcesDisagree"); }
                unlit ??= sourceUnlit;
            }
            if (selectedCoverage && (discardConsumesAlpha || testsTransparency)) Merge("AlphaCutoff", localCutoff, 1, ref cutoff);

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
        if (color is null && opacity is null && cutoff is null && !sawColor && !sawOpacity && !sawCutoff) return null;
        if (sawColor && color is null) diagnostics.Add("MaterialBaseColorUnsupported");
        if (sawOpacity && opacity is null) diagnostics.Add("MaterialOpacityUnsupported");
        if (sawCutoff && cutoff is null) diagnostics.Add("MaterialAlphaCutoffUnsupported");
        if (UseSerializedScalarInitialValues && layout.ScalarConstants.Count != 0) diagnostics.Add("MaterialPreviewUsesSerializedScalarInitialValues");
        if (UseSerializedScalarInitialValues && layout.ArrayConstants.Count != 0) diagnostics.Add("MaterialPreviewUsesSourceInstanceParameters");
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
        return new(color, opacity, cutoff, inputs.OrderBy(input => input).ToArray(), textureSlots.Order().ToArray(), diagnostics.Distinct(StringComparer.Ordinal).ToArray())
        { IsUnlit = unlit == true };
    }

    private static MaterialExpression? ReadTerminalOutput(List<Token> source, List<Token> main,
        List<Token> hypershade, string? resultVariable, Dictionary<string, Value> values,
        IReadOnlySet<string> identityCalls, StoredGlslSource evidenceSource)
    {
        if (resultVariable is null || MutatesHypershadeResult(main, resultVariable, "ComputeHypershade") ||
            main.Any(token => token.Text is "if" or "for" or "while" or "switch" or "?" or "discard" or "return" or "break" or "continue" or "do"))
            return null;
        var outputs = TopLevelVec4Outputs(source);
        if (outputs.Count != 1) return null;
        var owners = hypershade.Select((token, index) => (token, index))
            .Where(item => item.token.Text == "return" && item.index + 2 < hypershade.Count &&
                IsIdentifier(hypershade[item.index + 1].Text) && hypershade[item.index + 2].Text == ";")
            .Select(item => hypershade[item.index + 1].Text).Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Length != 1) return null;
        var aliases = values.Where(value => value.Key.StartsWith("Material.", StringComparison.Ordinal) ||
            value.Key.StartsWith("MaterialInstance.", StringComparison.Ordinal))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        foreach (var value in values.Where(value => value.Key.StartsWith(owners[0] + ".", StringComparison.Ordinal)))
            aliases[resultVariable + value.Key[owners[0].Length..]] = value.Value;
        var output = outputs.Single();
        MaterialExpression? result = null;
        int writes = 0;
        for (var index = 0; index < main.Count; index++)
        {
            if (main[index].Text != output) continue;
            var cursor = index + 1;
            string? component = null;
            if (cursor + 1 < main.Count && main[cursor].Text == ".")
            { component = main[cursor + 1].Text; cursor += 2; }
            if (cursor >= main.Count || main[cursor].Text is not ("=" or "+=" or "-=" or "*=" or "/=")) continue;
            if (++writes > 1 || main[cursor].Text != "=" || component is not (null or "rgba" or "xyzw")) return null;
            int end = cursor + 1;
            while (end < main.Count && main[end].Text != ";") end++;
            if (end == main.Count) return null;
            try { result = ToNode(new Expression(main.Skip(cursor + 1).Take(end - cursor - 1).ToList(), aliases, identityCalls).Read()); }
            catch (GraphException error) when (error.Message == "MaterialExpressionUnsupported") { return null; }
            if (result?.ValueType != MaterialValueType.Vector4) return null;
            var expressionStart = main[cursor + 1].Start;
            var expressionEnd = main[end - 1].End;
            var byteStart = Encoding.UTF8.GetByteCount(evidenceSource.Text!.AsSpan(0, expressionStart));
            var byteLength = Encoding.UTF8.GetByteCount(evidenceSource.Text.AsSpan(expressionStart, expressionEnd - expressionStart));
            result = result with { SourceExtent = new(evidenceSource.DecodedExtent.Container,
                checked(evidenceSource.DecodedExtent.Offset + byteStart), byteLength) };
        }
        return writes == 1 ? result : null;
    }

    private static string? HypershadeResultVariable(IReadOnlyList<Token> main, string functionName = "ComputeHypershade")
    {
        string? result = null;
        for (var index = 2; index + 1 < main.Count; index++)
        {
            if (main[index].Text != functionName || main[index + 1].Text != "(" || main[index - 1].Text != "=")
                continue;
            var candidate = main[index - 2].Text;
            if (!IsIdentifier(candidate) || result is not null) return null;
            result = candidate;
        }
        return result;
    }

    private static bool DirectPixelAlphaConsumer(IReadOnlyList<Token> source,
        IReadOnlyList<Token> main, string resultVariable, string producerFunction = "ComputeHypershade")
    {
        if (MutatesHypershadeResult(main, resultVariable, producerFunction)) return false;
        var pixelOutputs = TopLevelVec4Outputs(source);
        var alphaWrites = 0;
        var qualifiedWrite = false;
        for (var index = 0; index + 4 < main.Count; index++)
        {
            var target = main[index].Text;
            if (!IsPixelOutput(target, pixelOutputs)) continue;

            var alphaMutation = main[index + 1].Text == "." &&
                (main[index + 2].Text.Contains('a') || main[index + 2].Text.Contains('w')) ? index + 3 : index + 1;
            if (IncrementAt(main, alphaMutation)) { alphaWrites++; continue; }

            if (main[index + 1].Text == "." && (main[index + 2].Text.Contains('a') || main[index + 2].Text.Contains('w')) &&
                IsAssignmentOperator(main[index + 3].Text))
            {
                alphaWrites++;
                var rhsStart = index + 4;
                var end = FindStatementEnd(main, rhsStart);
                if (main[index + 2].Text is "a" or "w" && !IsConditionalWrite(main, index) && main[index + 3].Text == "=" &&
                    UsesField(main, rhsStart, end, resultVariable, "transparency"))
                    qualifiedWrite = true;
                continue;
            }

            if (!IsAssignmentOperator(main[index + 1].Text)) continue;
            alphaWrites++;
            if (main[index + 1].Text != "=" || IsConditionalWrite(main, index) || index + 3 >= main.Count ||
                main[index + 2].Text != "vec4" || main[index + 3].Text != "(") continue;

            var depth = 1;
            var argumentCount = 1;
            var lastArgumentStart = index + 4;
            var close = -1;
            for (var cursor = index + 4; cursor < main.Count; cursor++)
            {
                if (main[cursor].Text == "(") depth++;
                else if (main[cursor].Text == ")")
                {
                    depth--;
                    if (depth == 0) { close = cursor; break; }
                }
                else if (main[cursor].Text == "," && depth == 1)
                { argumentCount++; lastArgumentStart = cursor + 1; }
            }
            var statementEnd = FindStatementEnd(main, index + 2);
            if (close < 0 || close != statementEnd - 1) continue;
            if ((argumentCount == 2 || argumentCount == 4) &&
                UsesField(main, lastArgumentStart, close, resultVariable, "transparency"))
                qualifiedWrite = true;
        }
        var forwarded = HelperPixelAlphaWrites(source, main, resultVariable, pixelOutputs);
        alphaWrites += forwarded.Writes;
        qualifiedWrite |= forwarded.Qualified;
        return alphaWrites == 1 && qualifiedWrite;
    }

    private static (int Writes, bool Qualified) HelperPixelAlphaWrites(IReadOnlyList<Token> source,
        IReadOnlyList<Token> main, string resultVariable, IReadOnlySet<string> outputs,
        Func<IReadOnlyList<Token>, int, bool>? coverageArgument = null)
    {
        var writes = 0; var qualified = false;
        for (var call = 0; call + 1 < main.Count; call++)
        {
            var name = main[call].Text;
            if (!IsIdentifier(name) || main[call + 1].Text != "(" ||
                name is "vec2" or "vec3" or "vec4" or "float" or "int" or "bool" or "clamp" or "saturate" or
                    "if" or "for" or "while" or "switch") continue;
            if (!TryArguments(main, call + 1, out var arguments, out _)) continue;
            List<List<Token>>? parameters = null;
            for (var definition = 1; definition + 1 < source.Count; definition++)
            {
                if (source[definition].Text != name || source[definition - 1].Text != "void" ||
                    source[definition + 1].Text != "(" ||
                    !TryArguments(source, definition + 1, out var candidate, out var end) ||
                    end + 1 >= source.Count || source[end + 1].Text != "{") continue;
                if (parameters is not null) { parameters = null; break; }
                parameters = candidate;
            }
            if (parameters is null)
            {
                if (arguments.Any(argument => UsesField(argument, 0, argument.Count, resultVariable, "transparency"))) writes++;
                continue;
            }
            var body = Function(source, name);
            if (body is null) continue;
            var parameterNames = parameters.Select(parameter => parameter.Count == 0 ? "" : parameter[^1].Text).ToArray();
            var coverageParameter = -1;
            if (parameters.Count == arguments.Count)
                for (var index = 0; index < parameters.Count; index++)
                    if (parameters[index].Count >= 2 && parameters[index][^2].Text == "float" &&
                        (coverageArgument?.Invoke(arguments[index], call) ??
                            (arguments[index].Count == 3 && arguments[index][0].Text == resultVariable &&
                             arguments[index][1].Text == "." && arguments[index][2].Text == "transparency"))) coverageParameter = index;
            var safe = !IsConditionalWrite(main, call) &&
                !body.Any(token => token.Text is "if" or "else" or "for" or "while" or "switch" or "?" or "return" or "discard") &&
                parameterNames.All(parameter => IsIdentifier(parameter) && !outputs.Contains(parameter)) &&
                parameters.All(parameter => !parameter.Any(token => token.Text is "out" or "inout"));
            for (var index = 0; index + 1 < body.Count; index++)
            {
                if (parameterNames.Contains(body[index].Text, StringComparer.Ordinal) &&
                    (IsAssignmentOperator(body[index + 1].Text) || body[index + 1].Text is "++" or "--")) safe = false;
                if (body[index + 1].Text == "(" && IsIdentifier(body[index].Text) &&
                    body[index].Text is not ("vec2" or "vec3" or "vec4" or "float" or "clamp" or "saturate")) safe = false;
                if (body[index].Text is "float" or "vec2" or "vec3" or "vec4" && outputs.Contains(body[index + 1].Text)) safe = false;
                if (index + 2 < body.Count && body[index].Text is "+" or "-" && body[index + 1].Text == body[index].Text &&
                    (parameterNames.Contains(body[index + 2].Text, StringComparer.Ordinal) ||
                        index > 0 && parameterNames.Contains(body[index - 1].Text, StringComparer.Ordinal))) safe = false;
                if (body[index].Text is "++" or "--" && parameterNames.Contains(body[index + 1].Text, StringComparer.Ordinal)) safe = false;
            }
            for (var index = 0; index + 1 < body.Count; index++)
            {
                if (!IsPixelOutput(body[index].Text, outputs)) continue;
                var assignment = index + 1;
                var component = "";
                if (body[assignment].Text == "." && assignment + 2 < body.Count)
                { component = body[assignment + 1].Text; assignment += 2; }
                if (!IsAssignmentOperator(body[assignment].Text) ||
                    component.Length != 0 && !component.Contains('a') && !component.Contains('w')) continue;
                writes++;
                if (!safe || coverageParameter < 0 || body[assignment].Text != "=") continue;
                var end = FindStatementEnd(body, assignment + 1);
                var rhs = body.Skip(assignment + 1).Take(end - assignment - 1).ToList();
                if (component.Length == 0 && rhs.Count > 2 && rhs[0].Text == "vec4" &&
                    TryArguments(rhs, 1, out var channels, out var close) && close == rhs.Count - 1 && channels.Count is 2 or 4)
                    rhs = channels[^1];
                else if (component is not ("a" or "w")) continue;
                qualified |= IdentityCoverage(rhs, parameterNames[coverageParameter]);
            }
        }
        return (writes, qualified);
    }

    private static bool ZeroAlphaConditionConsumesCoverage(IReadOnlyList<Token> source,
        IReadOnlyList<Token> main, string resultVariable, string producerFunction)
    {
        if (MutatesHypershadeResult(main, resultVariable, producerFunction)) return false;
        var outputs = TopLevelVec4Outputs(source);
        for (var index = 0; index + 10 < main.Count; index++)
        {
            // This branch writes zero alpha rather than discarding. Preserve it as an opacity expression.
            if (main[index].Text != "if" || main[index + 1].Text != "(" ||
                main[index + 2].Text != resultVariable || main[index + 3].Text != "." || main[index + 4].Text != "transparency" ||
                main[index + 5].Text != "<" || main[index + 6].Text != resultVariable || main[index + 7].Text != "." ||
                main[index + 8].Text != "alphaTestValue" || main[index + 9].Text != ")") continue;
            var rejected = Block(main, index + 10, out var next);
            if (rejected is null || next >= main.Count || main[next].Text != "else") continue;
            var accepted = Block(main, next + 1, out _);
            if (accepted is null) continue;
            var zero = HelperPixelAlphaWrites(source, rejected, resultVariable, outputs,
                (expression, call) => AlphaAlias(expression, rejected, call, resultVariable, true));
            var coverage = HelperPixelAlphaWrites(source, accepted, resultVariable, outputs,
                (expression, call) => AlphaAlias(expression, accepted, call, resultVariable, false));
            var all = HelperPixelAlphaWrites(source, main, resultVariable, outputs);
            if (zero is { Writes: 1, Qualified: true } && coverage is { Writes: 1, Qualified: true } &&
                all.Writes == 2 && !main.Any(item => outputs.Contains(item.Text))) return true;
        }
        return false;
    }

    private static List<Token>? Block(IReadOnlyList<Token> tokens, int start, out int next)
    {
        next = start;
        if (start >= tokens.Count || tokens[start].Text != "{") return null;
        var depth = 1;
        for (var index = start + 1; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "{") depth++;
            else if (tokens[index].Text == "}" && --depth == 0)
            { next = index + 1; return tokens.Skip(start + 1).Take(index - start - 1).ToList(); }
        }
        return null;
    }

    private static bool AlphaAlias(IReadOnlyList<Token> expression, IReadOnlyList<Token> body,
        int use, string resultVariable, bool zero)
    {
        if (zero && expression.Count == 1 && Number(expression[0].Text, 0)) return true;
        if (!zero && expression.Count == 3 && expression[0].Text == resultVariable &&
            expression[1].Text == "." && expression[2].Text == "transparency") return true;
        if (expression.Count != 1 || !IsIdentifier(expression[0].Text)) return false;
        var name = expression[0].Text;
        var declaration = -1;
        for (var index = 0; index < body.Count; index++)
        {
            if (body[index].Text != name) continue;
            if (index > 0 && body[index - 1].Text == "float" && index + 1 < body.Count && body[index + 1].Text == "=")
            {
                if (declaration >= 0 || index >= use || IsConditionalWrite(body, index)) return false;
                declaration = index;
            }
            else if (index > 0 && body[index - 1].Text is "float" or "vec2" or "vec3" or "vec4" ||
                index + 1 < body.Count && (IsAssignmentOperator(body[index + 1].Text) || IncrementAt(body, index + 1)) ||
                IncrementAt(body, index - 2)) return false;
        }
        if (declaration < 0) return false;
        for (var call = 0; call + 1 < use; call++)
            if (body[call + 1].Text == "(" && TryArguments(body, call + 1, out var arguments, out _) &&
                arguments.Any(argument => argument.Any(item => item.Text == name))) return false;
        var end = FindStatementEnd(body, declaration + 2);
        var initializer = body.Skip(declaration + 2).Take(end - declaration - 2).ToList();
        return zero ? initializer.Count == 1 && Number(initializer[0].Text, 0) :
            initializer.Count == 3 && initializer[0].Text == resultVariable && initializer[1].Text == "." && initializer[2].Text == "transparency";
    }

    private static bool IdentityCoverage(IReadOnlyList<Token> expression, string parameter)
    {
        if (expression.Count == 1) return expression[0].Text == parameter;
        if (expression.Count == 4 && expression[1].Text == "(" && expression[2].Text == parameter && expression[3].Text == ")")
            return expression[0].Text is "saturate" or "float";
        return expression.Count == 8 && expression[0].Text == "clamp" && expression[1].Text == "(" &&
            expression[2].Text == parameter && expression[3].Text == "," && Number(expression[4].Text, 0) &&
            expression[5].Text == "," && Number(expression[6].Text, 1) && expression[7].Text == ")";
    }

    private static bool TryArguments(IReadOnlyList<Token> tokens, int open,
        out List<List<Token>> arguments, out int close)
    {
        arguments = []; close = -1;
        var depth = 1; var start = open + 1;
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index].Text is "(" or "[") depth++;
            else if (tokens[index].Text is ")" or "]")
            {
                if (--depth != 0) continue;
                if (index > start) arguments.Add(tokens.Skip(start).Take(index - start).ToList());
                close = index; return true;
            }
            else if (tokens[index].Text == "," && depth == 1)
            { arguments.Add(tokens.Skip(start).Take(index - start).ToList()); start = index + 1; }
        }
        return false;
    }

    private static bool DiscardConditionConsumesAlpha(IReadOnlyList<Token> main, string resultVariable, string producerFunction = "ComputeHypershade")
    {
        if (MutatesHypershadeResult(main, resultVariable, producerFunction)) return false;
        for (var index = 0; index + 1 < main.Count; index++)
        {
            if (main[index].Text != "if" || main[index + 1].Text != "(") continue;
            var depth = 1;
            var close = -1;
            for (var cursor = index + 2; cursor < main.Count; cursor++)
            {
                if (main[cursor].Text == "(") depth++;
                else if (main[cursor].Text == ")" && --depth == 0) { close = cursor; break; }
            }
            if (close < 0 || !UsesField(main, index + 2, close, resultVariable, "transparency") ||
                !UsesField(main, index + 2, close, resultVariable, "alphaTestValue")) continue;
            var body = close + 1;
            if (body < main.Count && main[body].Text == "discard") return true;
            if (body + 3 < main.Count && main[body].Text == "{" && main[body + 1].Text == "discard" &&
                main[body + 2].Text == ";" && main[body + 3].Text == "}") return true;
        }
        return false;
    }

    private static bool MutatesHypershadeResult(IReadOnlyList<Token> main, string resultVariable, string producerFunction = "ComputeHypershade")
    {
        for (var index = 0; index + 1 < main.Count; index++)
        {
            if (main[index].Text == resultVariable && (IncrementAt(main, index + 1) ||
                index > 0 && main[index - 1].Text is "++" or "--" || index > 1 && IncrementAt(main, index - 2))) return true;
            if (main[index].Text == resultVariable && main[index + 1].Text == "=")
            {
                var isResultInitialization = index + 3 < main.Count &&
                    main[index + 2].Text == producerFunction && main[index + 3].Text == "(";
                if (!isResultInitialization) return true;
            }
            if (index + 3 < main.Count && main[index].Text == resultVariable && main[index + 1].Text == "." &&
                IsIdentifier(main[index + 2].Text) &&
                (IsAssignmentOperator(main[index + 3].Text) || IncrementAt(main, index + 3))) return true;
        }
        return false;
    }

    private static bool IsAssignmentOperator(string value) =>
        value is "=" or "+=" or "-=" or "*=" or "/=";

    private static bool IncrementAt(IReadOnlyList<Token> tokens, int index) => index >= 0 && index < tokens.Count &&
        (tokens[index].Text is "++" or "--" || index + 1 < tokens.Count && tokens[index].Text is "+" or "-" &&
            tokens[index + 1].Text == tokens[index].Text);

    private static int FindStatementEnd(IReadOnlyList<Token> tokens, int start)
    {
        var parentheses = 0;
        var brackets = 0;
        for (var index = Math.Max(0, start); index < tokens.Count; index++)
        {
            var value = tokens[index].Text;
            if (value == "(") parentheses++;
            else if (value == ")") parentheses--;
            else if (value == "[") brackets++;
            else if (value == "]") brackets--;
            else if (value == ";" && parentheses == 0 && brackets == 0) return index;
        }
        return tokens.Count;
    }

    private static bool IsConditionalWrite(IReadOnlyList<Token> main, int writeIndex)
    {
        for (var index = 0; index < writeIndex; index++)
        {
            if ((main[index].Text is "if" or "for" or "while" or "switch") &&
                index + 1 < main.Count && main[index + 1].Text == "(")
            {
                var depth = 1;
                var close = -1;
                for (var cursor = index + 2; cursor < main.Count; cursor++)
                {
                    if (main[cursor].Text == "(") depth++;
                    else if (main[cursor].Text == ")" && --depth == 0) { close = cursor; break; }
                }
                if (close >= 0 && InControlBody(main, close + 1, writeIndex)) return true;
            }
            else if (main[index].Text == "else" && InControlBody(main, index + 1, writeIndex)) return true;
        }
        return false;
    }

    private static bool InControlBody(IReadOnlyList<Token> tokens, int bodyStart, int writeIndex)
    {
        if (bodyStart >= tokens.Count) return false;
        var end = bodyStart;
        if (tokens[bodyStart].Text == "{")
        {
            var depth = 1;
            for (end = bodyStart + 1; end < tokens.Count && depth > 0; end++)
            { if (tokens[end].Text == "{") depth++; else if (tokens[end].Text == "}") depth--; }
            end--;
        }
        else end = FindStatementEnd(tokens, bodyStart);
        return writeIndex >= bodyStart && writeIndex <= end;
    }

    private static HashSet<string> TopLevelVec4Outputs(IReadOnlyList<Token> source)
    {
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        var braceDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var token = source[index].Text;
            if (token == "{") { braceDepth++; continue; }
            if (token == "}") { braceDepth--; continue; }
            if (token == "(") { parenthesisDepth++; continue; }
            if (token == ")") { parenthesisDepth--; continue; }
            if (braceDepth == 0 && parenthesisDepth == 0 && token == "out" && index + 2 < source.Count &&
                source[index + 1].Text == "vec4" && IsIdentifier(source[index + 2].Text))
                outputs.Add(source[index + 2].Text);
        }
        return outputs;
    }

    private static bool IsPixelOutput(string target, IReadOnlySet<string> declaredOutputs) =>
        target == "gl_FragColor" || declaredOutputs.Contains(target);

    private static bool UsesField(IReadOnlyList<Token> tokens, int start, int end,
        string owner, string field)
    {
        for (var index = Math.Max(0, start); index + 2 < Math.Min(end, tokens.Count); index++)
            if (tokens[index].Text == owner && tokens[index + 1].Text == "." && tokens[index + 2].Text == field)
                return true;
        return false;
    }

    private static bool IsIdentifier(string text) => text.Length != 0 &&
        (char.IsLetter(text[0]) || text[0] == '_') &&
        text.All(character => char.IsLetterOrDigit(character) || character == '_');

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
    private static void EvaluateSurfaceBody(List<Token> body, Dictionary<string, Value> values, StoredGlslSource source,
        IReadOnlySet<string> identityCalls, CancellationToken token, ProjectionDepthSource? projectionDepth = null)
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
                try
                {
                    value = projectionDepth is not null && key == projectionDepth.DepthVariable && statement[equals].Text == "="
                        ? new(ValueKind.PassDepthSample)
                        : new Expression(statement.Skip(equals + 1).ToList(), values, identityCalls,
                            projectionDepth?.LinearizeFunction, projectionDepth?.NormalizationDistance).Read();
                }
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
        if (width is < 1 or > 4 || value.Width != indices.Length) { values[target] = Value.Unknown; return; }
        var lanes = new Value[width];
        var aliases = new Dictionary<string, Value>(StringComparer.Ordinal) { ["old"] = previous, ["new"] = value };
        for (var i = 0; i < width; i++) lanes[i] = ReadValue("old." + "rgba"[i], aliases);
        for (var i = 0; i < indices.Length; i++) lanes[indices[i]] = value.Width == 1 ? value : ReadValue("new." + "rgba"[i], aliases);
        // Keep explicit lane values even if an unrelated lane is undefined.
        for (var i = 0; i < width; i++) values[target + "." + "rgba"[i]] = lanes[i];
        values[target] = JoinLanes(lanes);
        var rgb = JoinLanes(lanes.Take(3).ToArray());
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
