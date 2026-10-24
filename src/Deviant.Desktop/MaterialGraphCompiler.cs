using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Desktop;

public sealed record CompiledMaterialGraph(string Declarations, string Body, string Signature,
    IReadOnlyList<int> TextureSlots, IReadOnlyList<string> Diagnostics,
    bool HasBaseColor, bool HasOpacity, bool HasAlphaCutoff)
{
    public bool RequiresSceneDepth { get; init; }
}

/// <summary>Emits shader expressions from bounded, typed material data.</summary>
public static class MaterialGraphCompiler
{
    public const int MaximumTextureSlots = 16;
    public const int MaximumNodes = 4096;
    public const int MaximumDepth = MaximumNodes;
    public const int MaximumSourceCharacters = 128 * 1024;

    public static CompiledMaterialGraph Compile(MaterialSurfaceGraph graph,
        IReadOnlySet<MaterialInput>? availableInputs = null, CancellationToken token = default,
        IReadOnlyDictionary<int, int>? samplerGroups = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        availableInputs ??= new HashSet<MaterialInput>
        { MaterialInput.Uv0, MaterialInput.Uv1, MaterialInput.Uv2, MaterialInput.Uv3,
            MaterialInput.WorldPosition, MaterialInput.VertexColor, MaterialInput.WorldNormal, MaterialInput.ViewDirection, MaterialInput.ViewNormal, MaterialInput.ViewDepth, MaterialInput.SceneViewDepth };
        var diagnostics = new List<string>();
        var slots = new SortedSet<int>();
        var requiredInputs = new HashSet<MaterialInput>();
        var output = new StringBuilder();
        var declaredSlots = graph.TextureSlots.ToHashSet();
        string? color = EmitRole("BaseColor", graph.BaseColor, [MaterialValueType.Scalar, MaterialValueType.Vector3, MaterialValueType.Vector4]);
        string? opacity = EmitRole("Opacity", graph.Opacity, [MaterialValueType.Scalar]);
        string? cutoff = EmitRole("AlphaCutoff", graph.AlphaCutoff, [MaterialValueType.Scalar]);
        if (graph.Opacity is not null && opacity is null || graph.AlphaCutoff is not null && cutoff is null)
            throw new InvalidDataException("The material coverage inputs are unavailable: " + string.Join("; ", diagnostics));
        if (slots.Count > MaximumTextureSlots)
            throw new InvalidDataException("The material uses too many textures.");
        var slotArray = slots.ToArray();
        for (int index = 0; index < slotArray.Length; index++)
        {
            output.Replace($"$texture{slotArray[index]}$", $"surfaceTexture{index}");
            if (samplerGroups is not null)
            {
                if (!samplerGroups.TryGetValue(slotArray[index], out int sampler) || sampler is < 0 or >= 15)
                    throw new InvalidDataException("The material sampler assignment is invalid.");
                output.Replace($"$sampler{slotArray[index]}$", $"surfaceSampler{sampler}");
            }
        }
        var declarations = new StringBuilder();
        for (int index = 0; index < slotArray.Length; index++)
            declarations.Append(CultureInfo.InvariantCulture, $"Texture2D<float4> surfaceTexture{index} : register(t{64 + index});\n");
        if (samplerGroups is null) declarations.AppendLine("SamplerState surfaceGraphSampler : register(s10);");
        else foreach (int sampler in slotArray.Select(slot => samplerGroups[slot]).Distinct().Order())
            declarations.Append(CultureInfo.InvariantCulture, $"SamplerState surfaceSampler{sampler} : register(s{1 + sampler});\n");
        output.AppendLine($"float3 surfaceColor = {(color is null ? "input.cDiffuse.rgb" : color)};");
        output.AppendLine($"float surfaceOpacity = saturate({opacity ?? "input.cDiffuse.a"});");
        output.AppendLine($"bool surfaceUnlit = {(graph.IsUnlit ? "true" : "false")};");
        if (cutoff is not null) output.AppendLine($"clip(surfaceOpacity - ({cutoff}));");
        string body = output.ToString();
        if (body.Length + declarations.Length > MaximumSourceCharacters)
            throw new InvalidDataException("The material program exceeds the renderer limit.");
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(declarations + body)));
        return new(declarations.ToString(), body, signature, slotArray, diagnostics,
            color is not null, opacity is not null, cutoff is not null)
        { RequiresSceneDepth = requiredInputs.Contains(MaterialInput.SceneViewDepth) };

        string? EmitRole(string role, MaterialExpression? expression, MaterialValueType[] outputTypes)
        {
            if (expression is null) return null;
            token.ThrowIfCancellationRequested();
            try
            {
                if (!outputTypes.Contains(expression.ValueType)) throw new InvalidDataException("Invalid output type.");
                var emitter = new Emitter(role, declaredSlots, availableInputs, token, samplerGroups is not null);
                string result = emitter.Emit(expression);
                if (slots.Concat(emitter.TextureSlots).Distinct().Count() > MaximumTextureSlots)
                    throw new InvalidDataException("The role exceeds the texture limit.");
                if (output.Length + emitter.Statements.Length + 4096 > MaximumSourceCharacters)
                    throw new InvalidDataException("The role exceeds the material program limit.");
                output.Append(emitter.Statements);
                slots.UnionWith(emitter.TextureSlots);
                requiredInputs.UnionWith(emitter.Inputs);
                if (role == "BaseColor") result = expression.ValueType switch
                {
                    MaterialValueType.Scalar => $"float3({result}, {result}, {result})",
                    MaterialValueType.Vector4 => $"{result}.rgb",
                    _ => result
                };
                return result;
            }
            catch (InvalidDataException error)
            {
                diagnostics.Add($"{role}: {error.Message}");
                return null;
            }
        }
    }

    private sealed class Emitter(string prefix, IReadOnlySet<int> declaredSlots,
        IReadOnlySet<MaterialInput> availableInputs, CancellationToken token, bool sourceSamplers)
    {
        private readonly Dictionary<MaterialExpression, string> emitted = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<MaterialExpression> visiting = new(ReferenceEqualityComparer.Instance);
        public StringBuilder Statements { get; } = new();
        public HashSet<int> TextureSlots { get; } = [];
        public HashSet<MaterialInput> Inputs { get; } = [];

        public string Emit(MaterialExpression expression)
        {
            IReadOnlyList<MaterialExpression> order;
            try { order = MaterialExpressionGraph.GetPostOrder(expression, MaximumNodes, token); }
            catch (ArgumentException error) { throw new InvalidDataException(error.Message, error); }
            foreach (var node in order) EmitNode(node);
            return emitted[expression];
        }

        private string EmitNode(MaterialExpression expression)
        {
            token.ThrowIfCancellationRequested();
            if (emitted.TryGetValue(expression, out var existing)) return existing;
            if (emitted.Count + visiting.Count >= MaximumNodes) throw new InvalidDataException("The expression is too large.");
            if (!visiting.Add(expression)) throw new InvalidDataException("The expression contains a cycle.");
            try
            {
                int width = Width(expression.ValueType);
                var arguments = expression.Arguments;
                if (arguments is null) throw new InvalidDataException("An expression has no argument list.");
                if (arguments.Count > 4) throw new InvalidDataException("Too many expression arguments.");
                string[] args = arguments.Select(argument => argument is not null && emitted.TryGetValue(argument, out var dependency)
                    ? dependency : throw new InvalidDataException("An expression dependency changed during compilation.")).ToArray();
                string code = expression.Operation switch
                {
                    MaterialExpressionOperation.Constant => Constant(),
                    MaterialExpressionOperation.Input => Input(),
                    MaterialExpressionOperation.TextureSample => Sample(),
                    MaterialExpressionOperation.Swizzle => Swizzle(),
                    MaterialExpressionOperation.Construct => Construct(),
                    MaterialExpressionOperation.Add => Binary("+"),
                    MaterialExpressionOperation.Subtract => Binary("-"),
                    MaterialExpressionOperation.Multiply => Binary("*"),
                    MaterialExpressionOperation.Divide => Binary("/"),
                    MaterialExpressionOperation.Negate => Unary("-", isOperator: true),
                    MaterialExpressionOperation.Mix => Function("lerp", 3),
                    MaterialExpressionOperation.Clamp => Function("clamp", 3),
                    MaterialExpressionOperation.Pow => Function("pow", 2),
                    MaterialExpressionOperation.Min => Function("min", 2),
                    MaterialExpressionOperation.Max => Function("max", 2),
                    MaterialExpressionOperation.Abs => Unary("abs"),
                    MaterialExpressionOperation.Normalize => Unary("normalize"),
                    MaterialExpressionOperation.Sin => Unary("sin"),
                    MaterialExpressionOperation.Cos => Unary("cos"),
                    MaterialExpressionOperation.Floor => Unary("floor"),
                    MaterialExpressionOperation.Fract => Unary("frac"),
                    MaterialExpressionOperation.Step => Function("step", 2),
                    MaterialExpressionOperation.Smoothstep => Function("smoothstep", 3),
                    MaterialExpressionOperation.Dot => Dot(),
                    _ => throw new InvalidDataException("Unsupported expression operation.")
                };
                string name = $"material{prefix}{emitted.Count}";
                Statements.AppendLine($"{Type(expression.ValueType)} {name} = {code};");
                if (Statements.Length > MaximumSourceCharacters) throw new InvalidDataException("The expression is too large.");
                emitted.Add(expression, name);
                return name;

                void Count(int expected)
                { if (args.Length != expected) throw new InvalidDataException("Invalid expression argument count."); }
                void Compatible(int index)
                { int size = Width(arguments[index].ValueType); if (size != 1 && size != width) throw new InvalidDataException("Incompatible expression types."); }
                string Constant()
                {
                    Count(0);
                    if (expression.Constants?.Count != width || expression.Constants.Any(x => !float.IsFinite(x)))
                        throw new InvalidDataException("Invalid numeric constant.");
                    var numbers = expression.Constants.Select(x =>
                    {
                        string value = x.ToString("R", CultureInfo.InvariantCulture);
                        if (!value.Contains('.') && !value.Contains('E') && !value.Contains('e')) value += ".0";
                        return value + "f";
                    });
                    return width == 1 ? numbers.Single() : $"{Type(expression.ValueType)}({string.Join(", ", numbers)})";
                }
                string Input()
                {
                    Count(0);
                    if (expression.Input is not { } input) throw new InvalidDataException("The vertex input is missing.");
                    if (!availableInputs.Contains(input)) throw new InvalidDataException($"The {input} vertex input is unavailable.");
                    Inputs.Add(input);
                    var (expected, value) = input switch
                    {
                        MaterialInput.Uv0 => (MaterialValueType.Vector2, "input.t"),
                        MaterialInput.Uv1 => (MaterialValueType.Vector2, "input.uv1"),
                        MaterialInput.Uv2 => (MaterialValueType.Vector2, "input.uv2"),
                        MaterialInput.Uv3 => (MaterialValueType.Vector2, "input.uv3"),
                        MaterialInput.WorldPosition => (MaterialValueType.Vector3, "input.wp.xyz"),
                        MaterialInput.VertexColor => (MaterialValueType.Vector4, "input.c"),
                        MaterialInput.WorldNormal => (MaterialValueType.Vector3, "normalize(input.n)"),
                        MaterialInput.ViewNormal => (MaterialValueType.Vector3, "normalize(mul(normalize(input.n), (float3x3)mView))"),
                        MaterialInput.ViewDirection => (MaterialValueType.Vector3, "normalize(input.vEye.xyz)"),
                        MaterialInput.ViewDepth => (MaterialValueType.Scalar, "-input.sp.z"),
                        MaterialInput.SceneViewDepth => (MaterialValueType.Scalar, "ReadSceneViewDepth(input.p.xy)"),
                        _ => throw new InvalidDataException($"The {input} vertex input is unsupported.")
                    };
                    if (expression.ValueType != expected) throw new InvalidDataException("Invalid vertex input type.");
                    return value;
                }
                string Sample()
                {
                    Count(1);
                    if (expression.ValueType != MaterialValueType.Vector4 || arguments[0].ValueType != MaterialValueType.Vector2 ||
                        expression.TextureSlot is not { } slot || slot < 0 || slot > 4095 || !declaredSlots.Contains(slot))
                        throw new InvalidDataException("Invalid texture sample.");
                    TextureSlots.Add(slot);
                    return $"$texture{slot}$.Sample({(!sourceSamplers ? "surfaceGraphSampler" : $"$sampler{slot}$")}, {args[0]})";
                }
                string Swizzle()
                {
                    Count(1);
                    string? components = expression.Components;
                    if (components?.Length != width) throw new InvalidDataException("Invalid channel selection.");
                    int inputWidth = Width(arguments[0].ValueType);
                    bool rgba = components.All(c => "rgba".Contains(c));
                    bool xyzw = components.All(c => "xyzw".Contains(c));
                    if (!rgba && !xyzw || components.Any(c => (rgba ? "rgba" : "xyzw").IndexOf(c) >= inputWidth))
                        throw new InvalidDataException("Invalid channel selection.");
                    return $"{args[0]}.{components}";
                }
                string Construct()
                {
                    if (args.Length == 0) throw new InvalidDataException("Empty vector constructor.");
                    int sum = arguments.Sum(x => Width(x.ValueType));
                    if (args.Length == 1 && sum == 1)
                        return width == 1 ? args[0] : $"{Type(expression.ValueType)}({string.Join(", ", Enumerable.Repeat(args[0], width))})";
                    if (sum < width || sum - Width(arguments[^1].ValueType) >= width)
                        throw new InvalidDataException("Invalid vector constructor.");
                    return $"{Type(expression.ValueType)}({string.Join(", ", args)})";
                }
                string Binary(string operation)
                {
                    Count(2); Compatible(0); Compatible(1);
                    if (Math.Max(Width(arguments[0].ValueType), Width(arguments[1].ValueType)) != width)
                        throw new InvalidDataException("Invalid arithmetic result type.");
                    return $"({args[0]} {operation} {args[1]})";
                }
                string Unary(string operation, bool isOperator = false)
                {
                    Count(1);
                    if (arguments[0].ValueType != expression.ValueType) throw new InvalidDataException("Invalid unary result type.");
                    return isOperator ? $"(-{args[0]})" : $"{operation}({args[0]})";
                }
                string Function(string operation, int count)
                {
                    Count(count);
                    for (int index = 0; index < count; index++) Compatible(index);
                    if (arguments.All(x => x.ValueType == MaterialValueType.Scalar) && width != 1)
                        throw new InvalidDataException("Invalid function result type.");
                    return $"{operation}({string.Join(", ", args)})";
                }
                string Dot()
                {
                    Count(2);
                    if (width != 1 || arguments[0].ValueType != arguments[1].ValueType)
                        throw new InvalidDataException("Invalid dot product.");
                    return $"dot({args[0]}, {args[1]})";
                }
            }
            finally { visiting.Remove(expression); }
        }
    }

    private static int Width(MaterialValueType type) => type is >= MaterialValueType.Scalar and <= MaterialValueType.Vector4
        ? (int)type : throw new InvalidDataException("Unknown material value type.");
    private static string Type(MaterialValueType type) => Width(type) == 1 ? "float" : $"float{Width(type)}";
}
