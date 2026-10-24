using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Deviant.Assets;

namespace Deviant.Export.Models;

/// <summary>Compiles and evaluates a material expression graph on the CPU.</summary>
/// <remarks>The instance reuses its scratch storage and must not be evaluated concurrently.</remarks>
public sealed class MaterialSurfaceExpressionEvaluator
{
    private const int MaximumNodes = 4096;

    private readonly CompiledNode[] _nodes;
    private readonly int _rootIndex;
    private readonly Vector4[] _values;
    private readonly bool[] _constantNodes;
    private readonly bool[] _constantCached;

    public MaterialSurfaceExpressionEvaluator(MaterialExpression root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var topological = CompileOrder(root);
        _nodes = new CompiledNode[topological.Count];
        var indices = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);

        for (var index = 0; index < topological.Count; index++)
        {
            var expression = topological[index];
            var width = Width(expression.ValueType);
            var arguments = new int[expression.Arguments.Count];
            for (var argumentIndex = 0; argumentIndex < arguments.Length; argumentIndex++)
            {
                var argument = expression.Arguments[argumentIndex];
                arguments[argumentIndex] = indices[argument];
            }

            ValidateNode(expression, width);
            _nodes[index] = new CompiledNode(expression, arguments, width);
            indices.Add(expression, index);
        }

        _rootIndex = indices[root];
        _values = new Vector4[_nodes.Length];
        _constantNodes = new bool[_nodes.Length];
        _constantCached = new bool[_nodes.Length];
        for (int index = 0; index < _nodes.Length; index++)
            _constantNodes[index] = _nodes[index].Expression.Operation is not (MaterialExpressionOperation.Input or MaterialExpressionOperation.TextureSample) &&
                _nodes[index].Arguments.All(argument => _constantNodes[argument]);
        RootValueType = root.ValueType;
    }

    public MaterialValueType RootValueType { get; }

    public Vector4 Evaluate(
        Func<MaterialInput, Vector4> input,
        Func<int, Vector2, Vector4> texture,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(texture);

        for (var index = 0; index < _nodes.Length; index++)
        {
            if ((index & 63) == 0) token.ThrowIfCancellationRequested();
            if (_constantCached[index]) continue;
            var node = _nodes[index];
            var expression = node.Expression;
            var arguments = node.Arguments;
            Vector4 result;

            switch (expression.Operation)
            {
                case MaterialExpressionOperation.Constant:
                {
                    var constants = expression.Constants!;
                    result = new Vector4(
                        constants[0],
                        node.Width > 1 ? constants[1] : 0f,
                        node.Width > 2 ? constants[2] : 0f,
                        node.Width > 3 ? constants[3] : 0f);
                    break;
                }
                case MaterialExpressionOperation.Input:
                    result = Fit(input(expression.Input!.Value), node.Width);
                    break;
                case MaterialExpressionOperation.TextureSample:
                {
                    var uv = _values[arguments[0]];
                    var sample = texture(expression.TextureSlot!.Value, new Vector2(uv.X, uv.Y));
                    EnsureFinite(sample, 4);
                    result = sample;
                    break;
                }
                case MaterialExpressionOperation.Swizzle:
                    result = Swizzle(_values[arguments[0]], Width(expression.Arguments[0].ValueType), expression.Components!, node.Width);
                    break;
                case MaterialExpressionOperation.Construct:
                    result = Construct(expression, arguments, node.Width);
                    break;
                case MaterialExpressionOperation.Negate:
                case MaterialExpressionOperation.Abs:
                case MaterialExpressionOperation.Sin:
                case MaterialExpressionOperation.Cos:
                case MaterialExpressionOperation.Floor:
                case MaterialExpressionOperation.Fract:
                    result = Unary(_values[arguments[0]], node.Width, expression.Operation);
                    break;
                case MaterialExpressionOperation.Normalize:
                    result = Normalize(_values[arguments[0]], node.Width);
                    break;
                case MaterialExpressionOperation.Add:
                case MaterialExpressionOperation.Subtract:
                case MaterialExpressionOperation.Multiply:
                case MaterialExpressionOperation.Divide:
                case MaterialExpressionOperation.Min:
                case MaterialExpressionOperation.Max:
                case MaterialExpressionOperation.Pow:
                    result = Binary(expression, arguments);
                    break;
                case MaterialExpressionOperation.Mix:
                    result = Mix(expression, arguments);
                    break;
                case MaterialExpressionOperation.Clamp:
                    result = Clamp(expression, arguments);
                    break;
                case MaterialExpressionOperation.Dot:
                    result = Dot(_values[arguments[0]], _values[arguments[1]], Width(expression.Arguments[0].ValueType));
                    break;
                case MaterialExpressionOperation.Step:
                    result = Step(expression, arguments);
                    break;
                case MaterialExpressionOperation.Smoothstep:
                    result = Smoothstep(expression, arguments);
                    break;
                default:
                    throw InvalidGraph($"Unsupported operation '{expression.Operation}'.");
            }

            EnsureFinite(result, node.Width);
            _values[index] = Fit(result, node.Width);
            _constantCached[index] = _constantNodes[index];
        }

        token.ThrowIfCancellationRequested();
        return Fit(_values[_rootIndex], Width(RootValueType));
    }

    private Vector4 Binary(MaterialExpression expression, int[] arguments)
    {
        var leftNode = _nodes[arguments[0]];
        var rightNode = _nodes[arguments[1]];
        var left = _values[arguments[0]];
        var right = _values[arguments[1]];
        var width = Width(expression.ValueType);
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
        {
            var first = Lane(left, leftNode.Width, lane);
            var second = Lane(right, rightNode.Width, lane);
            var value = expression.Operation switch
            {
                MaterialExpressionOperation.Add => (double)first + second,
                MaterialExpressionOperation.Subtract => (double)first - second,
                MaterialExpressionOperation.Multiply => (double)first * second,
                MaterialExpressionOperation.Divide when second != 0f => (double)first / second,
                MaterialExpressionOperation.Divide => throw InvalidValue("Division by zero is not allowed."),
                MaterialExpressionOperation.Min => Math.Min(first, second),
                MaterialExpressionOperation.Max => Math.Max(first, second),
                MaterialExpressionOperation.Pow => Math.Pow(first, second),
                _ => throw InvalidGraph("The binary operation is unsupported.")
            };
            components[lane] = ToFiniteFloat(value);
        }

        return Pack(components, width);
    }

    private Vector4 Mix(MaterialExpression expression, int[] arguments)
    {
        var firstNode = _nodes[arguments[0]];
        var secondNode = _nodes[arguments[1]];
        var amountNode = _nodes[arguments[2]];
        var first = _values[arguments[0]];
        var second = _values[arguments[1]];
        var amount = _values[arguments[2]];
        var width = Width(expression.ValueType);
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
        {
            var a = Lane(amount, amountNode.Width, lane);
            var x = Lane(first, firstNode.Width, lane);
            var y = Lane(second, secondNode.Width, lane);
            components[lane] = ToFiniteFloat(((double)x * (1d - a)) + ((double)y * a));
        }
        return Pack(components, width);
    }

    private Vector4 Clamp(MaterialExpression expression, int[] arguments)
    {
        var valueNode = _nodes[arguments[0]];
        var lowNode = _nodes[arguments[1]];
        var highNode = _nodes[arguments[2]];
        var value = _values[arguments[0]];
        var low = _values[arguments[1]];
        var high = _values[arguments[2]];
        var width = Width(expression.ValueType);
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
        {
            var lower = Lane(low, lowNode.Width, lane);
            var upper = Lane(high, highNode.Width, lane);
            components[lane] = ToFiniteFloat(Math.Min(Math.Max(Lane(value, valueNode.Width, lane), lower), upper));
        }
        return Pack(components, width);
    }

    private Vector4 Step(MaterialExpression expression, int[] arguments)
    {
        var edgeNode = _nodes[arguments[0]];
        var valueNode = _nodes[arguments[1]];
        var edge = _values[arguments[0]];
        var value = _values[arguments[1]];
        var width = Width(expression.ValueType);
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
            components[lane] = Lane(value, valueNode.Width, lane) < Lane(edge, edgeNode.Width, lane) ? 0f : 1f;
        return Pack(components, width);
    }

    private Vector4 Smoothstep(MaterialExpression expression, int[] arguments)
    {
        var lowNode = _nodes[arguments[0]];
        var highNode = _nodes[arguments[1]];
        var valueNode = _nodes[arguments[2]];
        var low = _values[arguments[0]];
        var high = _values[arguments[1]];
        var value = _values[arguments[2]];
        var width = Width(expression.ValueType);
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
        {
            var edge0 = Lane(low, lowNode.Width, lane);
            var edge1 = Lane(high, highNode.Width, lane);
            if (edge1 <= edge0)
                throw InvalidValue("Smoothstep requires an upper edge greater than its lower edge.");
            var t = Math.Clamp(((double)Lane(value, valueNode.Width, lane) - edge0) / (edge1 - edge0), 0d, 1d);
            components[lane] = ToFiniteFloat(t * t * (3d - (2d * t)));
        }
        return Pack(components, width);
    }

    private Vector4 Construct(MaterialExpression expression, int[] arguments, int width)
    {
        if (arguments.Length == 1 && _nodes[arguments[0]].Width == 1 && width > 1)
        {
            var scalar = _values[arguments[0]].X;
            return new Vector4(scalar);
        }

        Span<float> components = stackalloc float[4];
        var output = 0;
        foreach (var argumentIndex in arguments)
        {
            var argument = _values[argumentIndex];
            var argumentWidth = _nodes[argumentIndex].Width;
            for (var lane = 0; lane < argumentWidth; lane++)
                components[output++] = Lane(argument, argumentWidth, lane);
        }

        return Pack(components, width);
    }

    private static Vector4 Dot(Vector4 left, Vector4 right, int width)
    {
        var value = 0d;
        for (var lane = 0; lane < width; lane++)
            value += (double)Lane(left, width, lane) * Lane(right, width, lane);
        return new Vector4(ToFiniteFloat(value), 0f, 0f, 0f);
    }

    private static Vector4 Normalize(Vector4 value, int width)
    {
        var lengthSquared = 0d;
        for (var lane = 0; lane < width; lane++)
        {
            var component = Lane(value, width, lane);
            lengthSquared += (double)component * component;
        }

        var length = Math.Sqrt(lengthSquared);
        if (length == 0d)
            throw InvalidValue("A zero vector has no normalized direction.");

        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
            components[lane] = ToFiniteFloat(Lane(value, width, lane) / length);
        return Pack(components, width);
    }

    private static Vector4 Swizzle(Vector4 value, int sourceWidth, string components, int resultWidth)
    {
        Span<float> result = stackalloc float[4];
        for (var lane = 0; lane < resultWidth; lane++)
            result[lane] = Lane(value, sourceWidth, ComponentIndex(components[lane]));

        return Pack(result, resultWidth);
    }

    private static Vector4 Unary(Vector4 value, int width, MaterialExpressionOperation operation)
    {
        Span<float> components = stackalloc float[4];
        for (var lane = 0; lane < width; lane++)
        {
            var component = (double)Lane(value, width, lane);
            var result = operation switch
            {
                MaterialExpressionOperation.Negate => -component,
                MaterialExpressionOperation.Abs => Math.Abs(component),
                MaterialExpressionOperation.Sin => Math.Sin(component),
                MaterialExpressionOperation.Cos => Math.Cos(component),
                MaterialExpressionOperation.Floor => Math.Floor(component),
                MaterialExpressionOperation.Fract => component - Math.Floor(component),
                _ => throw InvalidGraph("The unary operation is unsupported.")
            };
            components[lane] = ToFiniteFloat(result);
        }
        return Pack(components, width);
    }

    private static Vector4 Pack(ReadOnlySpan<float> components, int width) => new(
        components[0],
        width > 1 ? components[1] : 0f,
        width > 2 ? components[2] : 0f,
        width > 3 ? components[3] : 0f);

    private static Vector4 Fit(Vector4 value, int width) => new(
        value.X,
        width > 1 ? value.Y : 0f,
        width > 2 ? value.Z : 0f,
        width > 3 ? value.W : 0f);

    private static float Lane(Vector4 value, int width, int lane) => width == 1
        ? value.X
        : lane switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => throw InvalidGraph("A vector component index is outside the supported range.")
        };

    private static int ComponentIndex(char component) => component switch
    {
        'x' or 'r' or 's' => 0,
        'y' or 'g' or 't' => 1,
        'z' or 'b' or 'p' => 2,
        'w' or 'a' or 'q' => 3,
        _ => throw InvalidGraph("A swizzle contains an unsupported component."),
    };

    private static float ToFiniteFloat(double value)
    {
        if (!double.IsFinite(value))
            throw InvalidValue("An expression produced a nonfinite value.");
        var result = (float)value;
        if (!float.IsFinite(result))
            throw InvalidValue("An expression result is outside the finite Float32 range.");
        return result;
    }

    private static void EnsureFinite(Vector4 value, int width)
    {
        for (var lane = 0; lane < width; lane++)
        {
            if (!float.IsFinite(Lane(value, width, lane)))
                throw InvalidValue("An expression input or output is not finite.");
        }
    }

    private static List<MaterialExpression> CompileOrder(MaterialExpression root)
        => MaterialExpressionGraph.GetPostOrder(root, MaximumNodes).ToList();

    private static void ValidateNode(MaterialExpression node, int width)
    {
        var arguments = node.Arguments;
        switch (node.Operation)
        {
            case MaterialExpressionOperation.Constant:
                RequireArity(node, 0);
                if (node.Constants is null || node.Constants.Count != width ||
                    node.Constants.Any(component => !float.IsFinite(component)))
                    throw InvalidGraph("A constant must contain one finite value per component.");
                RequireNoOtherData(node, allowConstants: true);
                return;
            case MaterialExpressionOperation.Input:
                RequireArity(node, 0);
                if (node.Input is null || !Enum.IsDefined(node.Input.Value) || WidthOfInput(node.Input.Value) != width)
                    throw InvalidGraph("An input expression has an invalid input or value type.");
                RequireNoOtherData(node, allowInput: true);
                return;
            case MaterialExpressionOperation.TextureSample:
                RequireArity(node, 1);
                if (node.TextureSlot is null || node.TextureSlot.Value < 0 || width != 4 ||
                    node.Arguments[0].ValueType != MaterialValueType.Vector2)
                    throw InvalidGraph("A texture sample requires a nonnegative slot, Vector2 coordinates, and Vector4 output.");
                RequireNoOtherData(node, allowTextureSlot: true);
                return;
            case MaterialExpressionOperation.Swizzle:
            {
                RequireArity(node, 1);
                var components = node.Components;
                var sourceWidth = Width(node.Arguments[0].ValueType);
                if (components is null || components.Length != width ||
                    components.Any(component => ComponentIndex(component) >= sourceWidth))
                    throw InvalidGraph("A swizzle does not match its declared result width or source vector.");
                if (!(components.All(component => "xyzw".Contains(component)) ||
                    components.All(component => "rgba".Contains(component)) || components.All(component => "stpq".Contains(component))))
                    throw InvalidGraph("A swizzle must use one component naming family.");
                RequireNoOtherData(node, allowComponents: true);
                return;
            }
            case MaterialExpressionOperation.Construct:
            {
                if (arguments.Count == 0)
                    throw InvalidGraph("A vector constructor requires at least one argument.");
                RequireNoOtherData(node);
                var argumentWidth = arguments.Sum(argument => Width(argument.ValueType));
                var scalarSplat = arguments.Count == 1 && Width(arguments[0].ValueType) == 1 && width > 1;
                if (!scalarSplat && argumentWidth != width)
                    throw InvalidGraph("Constructor arguments do not match the declared vector width.");
                return;
            }
            case MaterialExpressionOperation.Negate:
            case MaterialExpressionOperation.Abs:
            case MaterialExpressionOperation.Normalize:
            case MaterialExpressionOperation.Sin:
            case MaterialExpressionOperation.Cos:
            case MaterialExpressionOperation.Floor:
            case MaterialExpressionOperation.Fract:
                RequireArity(node, 1);
                RequireNoOtherData(node);
                if (node.Arguments[0].ValueType != node.ValueType)
                    throw InvalidGraph("A unary operation must preserve its operand width.");
                if (node.Operation == MaterialExpressionOperation.Normalize && width == 1)
                    throw InvalidGraph("Normalize requires a vector operand.");
                return;
            case MaterialExpressionOperation.Add:
            case MaterialExpressionOperation.Subtract:
            case MaterialExpressionOperation.Multiply:
            case MaterialExpressionOperation.Divide:
            case MaterialExpressionOperation.Min:
            case MaterialExpressionOperation.Max:
            case MaterialExpressionOperation.Pow:
                RequireArity(node, 2);
                RequireNoOtherData(node);
                RequireBroadcastResult(node, node.Arguments[0].ValueType, node.Arguments[1].ValueType);
                return;
            case MaterialExpressionOperation.Mix:
                RequireArity(node, 3);
                RequireNoOtherData(node);
                var mixWidth = BroadcastWidth(node.Arguments[0].ValueType, node.Arguments[1].ValueType);
                RequireBroadcastResult(node, (MaterialValueType)mixWidth, node.Arguments[2].ValueType);
                return;
            case MaterialExpressionOperation.Clamp:
            case MaterialExpressionOperation.Smoothstep:
                RequireArity(node, 3);
                RequireNoOtherData(node);
                var firstWidth = BroadcastWidth(node.Arguments[0].ValueType, node.Arguments[1].ValueType);
                RequireBroadcastResult(node, (MaterialValueType)firstWidth, node.Arguments[2].ValueType);
                return;
            case MaterialExpressionOperation.Step:
                RequireArity(node, 2);
                RequireNoOtherData(node);
                RequireBroadcastResult(node, node.Arguments[0].ValueType, node.Arguments[1].ValueType);
                return;
            case MaterialExpressionOperation.Dot:
                RequireArity(node, 2);
                RequireNoOtherData(node);
                if (node.Arguments[0].ValueType != node.Arguments[1].ValueType || width != 1 ||
                    Width(node.Arguments[0].ValueType) == 1)
                    throw InvalidGraph("Dot requires matching vector operands and a scalar result.");
                return;
            default:
                throw InvalidGraph($"Unsupported operation '{node.Operation}'.");
        }
    }

    private static void RequireBroadcastResult(MaterialExpression node, MaterialValueType left, MaterialValueType right)
    {
        var resultWidth = BroadcastWidth(left, right);
        if (Width(node.ValueType) != resultWidth)
            throw InvalidGraph("The operation result type does not match its operands.");
    }

    private static int BroadcastWidth(MaterialValueType left, MaterialValueType right)
    {
        var leftWidth = Width(left);
        var rightWidth = Width(right);
        if (leftWidth != rightWidth && leftWidth != 1 && rightWidth != 1)
            throw InvalidGraph("Vector operands must have matching widths or include a scalar.");
        return Math.Max(leftWidth, rightWidth);
    }

    private static void RequireArity(MaterialExpression node, int count)
    {
        if (node.Arguments.Count != count)
            throw InvalidGraph($"Operation '{node.Operation}' requires {count} argument(s).");
    }

    private static void RequireNoOtherData(
        MaterialExpression node,
        bool allowConstants = false,
        bool allowInput = false,
        bool allowTextureSlot = false,
        bool allowComponents = false)
    {
        if ((!allowConstants && node.Constants is not null) ||
            (!allowInput && node.Input is not null) ||
            (!allowTextureSlot && node.TextureSlot is not null) ||
            (!allowComponents && node.Components is not null))
            throw InvalidGraph($"Operation '{node.Operation}' contains unsupported data.");
    }

    private static int WidthOfInput(MaterialInput input) => input switch
    {
        MaterialInput.Uv0 or MaterialInput.Uv1 or MaterialInput.Uv2 or MaterialInput.Uv3 => 2,
        MaterialInput.WorldPosition or MaterialInput.WorldNormal or MaterialInput.ViewDirection or MaterialInput.ViewNormal => 3,
        MaterialInput.VertexColor => 4,
        MaterialInput.ViewDepth or MaterialInput.SceneViewDepth => 1,
        _ => throw InvalidGraph("The material input is unsupported.")
    };

    private static int Width(MaterialValueType type) => type switch
    {
        MaterialValueType.Scalar => 1,
        MaterialValueType.Vector2 => 2,
        MaterialValueType.Vector3 => 3,
        MaterialValueType.Vector4 => 4,
        _ => throw InvalidGraph("The material value type is unsupported.")
    };

    private static ArgumentException InvalidGraph(string message) => new(message, "root");
    private static InvalidOperationException InvalidValue(string message) => new(message);

    private sealed record CompiledNode(MaterialExpression Expression, int[] Arguments, int Width);
}
