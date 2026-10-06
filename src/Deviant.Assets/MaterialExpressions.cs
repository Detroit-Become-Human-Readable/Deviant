namespace Deviant.Assets;

public enum MaterialValueType { Scalar = 1, Vector2 = 2, Vector3 = 3, Vector4 = 4 }
public enum MaterialExpressionOperation
{
    Constant, Input, TextureSample, Swizzle, Construct, Add, Subtract, Multiply, Divide,
    Negate, Mix, Clamp, Pow, Min, Max, Abs, Dot, Normalize, Sin, Cos, Floor, Fract, Step, Smoothstep
}
public enum MaterialInput { Uv0, Uv1, Uv2, Uv3, WorldPosition, VertexColor, ViewDepth, WorldNormal, ViewDirection }

public sealed record MaterialExpression(MaterialExpressionOperation Operation, MaterialValueType ValueType,
    IReadOnlyList<MaterialExpression> Arguments, IReadOnlyList<float>? Constants = null,
    MaterialInput? Input = null, int? TextureSlot = null, string? Components = null,
    DecodedExtent? SourceExtent = null);

public sealed record MaterialSurfaceGraph(MaterialExpression? BaseColor, MaterialExpression? Opacity,
    MaterialExpression? AlphaCutoff, IReadOnlyList<MaterialInput> RequiredInputs,
    IReadOnlyList<int> TextureSlots, IReadOnlyList<string> Diagnostics);
