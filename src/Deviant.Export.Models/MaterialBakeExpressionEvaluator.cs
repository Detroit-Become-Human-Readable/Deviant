using System.Numerics;
using Deviant.Assets;

namespace Deviant.Export.Models;

internal sealed class MaterialBakeExpressionEvaluator
{
    private readonly MaterialSurfaceExpressionEvaluator? shared, color, opacity, cutoff;

    public MaterialBakeExpressionEvaluator(MaterialSurfaceGraph graph)
    {
        var sourceColor = graph.BaseColor ?? throw new InvalidDataException("The material has no colour expression.");
        if (sourceColor.ValueType is not (MaterialValueType.Scalar or MaterialValueType.Vector3 or MaterialValueType.Vector4) ||
            graph.Opacity is { ValueType: not MaterialValueType.Scalar } || graph.AlphaCutoff is { ValueType: not MaterialValueType.Scalar })
            throw new InvalidDataException("The material output types cannot form a colour map.");
        var rgb = sourceColor.ValueType switch
        {
            MaterialValueType.Scalar => new MaterialExpression(MaterialExpressionOperation.Construct, MaterialValueType.Vector3, [sourceColor]),
            MaterialValueType.Vector4 => new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Vector3, [sourceColor], Components: "xyz"),
            _ => sourceColor
        };
        var alpha = graph.Opacity ?? new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Scalar, [], [1f]);
        if (graph.AlphaCutoff is { } edge)
        {
            var survives = new MaterialExpression(MaterialExpressionOperation.Step, MaterialValueType.Scalar, [edge, alpha]);
            // Evaluate the original alpha first; Step preserves equality at the cutoff.
            alpha = new MaterialExpression(MaterialExpressionOperation.Multiply, MaterialValueType.Scalar, [alpha, survives]);
        }
        var output = new MaterialExpression(MaterialExpressionOperation.Construct, MaterialValueType.Vector4, [rgb, alpha]);
        try { shared = new MaterialSurfaceExpressionEvaluator(output); }
        catch (ArgumentException error) when (error.ParamName == "root" &&
            (error.Message.Contains("exceeds the nesting limit", StringComparison.Ordinal) ||
             error.Message.Contains("exceeds the node limit", StringComparison.Ordinal)))
        {
            // Added output nodes must not reject source roots that individually fit the evaluator limits.
            color = new(sourceColor);
            opacity = graph.Opacity is null ? null : new(graph.Opacity);
            cutoff = graph.AlphaCutoff is null ? null : new(graph.AlphaCutoff);
        }
    }

    public Vector4 Evaluate(Func<MaterialInput, Vector4> input, Func<int, Vector2, Vector4> texture, CancellationToken token)
    {
        if (shared is not null) return shared.Evaluate(input, texture, token);
        var fallbackColor = color!;
        var value = fallbackColor.Evaluate(input, texture, token);
        if (fallbackColor.RootValueType == MaterialValueType.Scalar) value = new(value.X, value.X, value.X, 1);
        var alpha = opacity?.Evaluate(input, texture, token).X ?? 1;
        if (cutoff is not null && alpha < cutoff.Evaluate(input, texture, token).X) alpha = 0;
        return new(value.X, value.Y, value.Z, alpha);
    }
}
