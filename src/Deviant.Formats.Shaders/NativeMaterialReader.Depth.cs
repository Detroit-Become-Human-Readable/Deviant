namespace Deviant.Formats.Shaders;

public sealed partial class NativeMaterialReader
{
    private sealed record DepthFunction(string Name, string ReturnType, string[] ParameterTypes,
        string[] ParameterNames, IReadOnlyList<Token> Body);

    private sealed record ProjectionDepthSource(string LinearizeFunction, string DepthVariable,
        float NormalizationDistance);

    private static ProjectionDepthSource? TryProjectionDepthSource(IReadOnlyList<Token> module,
        IReadOnlyList<Token> body, float? normalizationDistance)
    {
        if (normalizationDistance is not { } distance || !float.IsFinite(distance) || distance <= 0)
            return null;

        var functions = DepthFunctions(module);
        var linearizers = functions.Where(function => function.ReturnType == "float" &&
            function.ParameterTypes.SequenceEqual(["float", "vec3"], StringComparer.Ordinal) &&
            IsLinearizeDepthBody(function)).ToArray();
        if (linearizers.Length != 1) return null;
        var linearizer = linearizers[0];

        var identities = functions.Where(function => function.ReturnType == "float" &&
            function.ParameterTypes.SequenceEqual(["float"], StringComparer.Ordinal) &&
            IsFloatIdentityBody(function)).Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        var decoders = functions.Where(function => function.ReturnType == "float" &&
            function.ParameterTypes.SequenceEqual(["BINDLESS_TEXTURE_AND_SAMPLER", "vec2"], StringComparer.Ordinal) &&
            IsBindlessDepthDecoderBody(function, identities) && functions.Count(candidate =>
                candidate.Name == function.Name && candidate.ReturnType == function.ReturnType &&
                candidate.ParameterTypes.SequenceEqual(function.ParameterTypes, StringComparer.Ordinal)) == 1).ToArray();
        if (decoders.Length != 1) return null;
        var decoder = decoders[0];
        if (!HasBindlessPassTextureArray(module)) return null;

        string? depthVariable = null;
        var callCount = 0;
        var firstCall = int.MaxValue;
        for (var index = 0; index + 1 < body.Count; index++)
        {
            if (body[index].Text != linearizer.Name || body[index + 1].Text != "(") continue;
            if (!TryArguments(body, index + 1, out var arguments, out _) || arguments.Count != 2 ||
                arguments[0].Count != 1 || !IsIdentifier(arguments[0][0].Text) ||
                Concat(arguments[1]) != "g_P._vProjectionSetup.xyz") return null;
            if (depthVariable is not null && depthVariable != arguments[0][0].Text) return null;
            depthVariable = arguments[0][0].Text;
            firstCall = Math.Min(firstCall, index);
            callCount++;
        }
        if (callCount == 0 || depthVariable is null ||
            !TrySingleAssignment(body, depthVariable, out _, out var sampleRhs, out var sampleWrite) || sampleWrite >= firstCall) return null;

        if (sampleRhs.Count < 3 || sampleRhs[0].Text != decoder.Name || sampleRhs[1].Text != "(" ||
            !TryArguments(sampleRhs, 1, out var sampleArguments, out var sampleClose) ||
            sampleClose != sampleRhs.Count - 1 || sampleArguments.Count != 2 ||
            Concat(sampleArguments[0]) != "g_rb2DPassTextures[0+glslHack]" ||
            sampleArguments[1].Count != 1 || !IsIdentifier(sampleArguments[1][0].Text)) return null;

        var uvVariable = sampleArguments[1][0].Text;
        if (!TrySingleAssignment(body, uvVariable, out var uvType, out var uvRhs, out var uvWrite) || uvType != "vec2" || uvWrite >= sampleWrite ||
            !string.Equals(Concat(StripWrappingParentheses(uvRhs)),
                "Surface.fFragCoord.xy/g_P._EyeData._vFrameBufferViewportSize.xy", StringComparison.Ordinal))
            return null;

        return new(linearizer.Name, depthVariable, distance);
    }

    private static DepthFunction[] DepthFunctions(IReadOnlyList<Token> tokens)
    {
        var result = new List<DepthFunction>();
        var braceDepth = 0;
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Text == "}") { braceDepth--; continue; }
            if (braceDepth == 0 && index > 0 && IsIdentifier(tokens[index].Text) &&
                IsIdentifier(tokens[index - 1].Text) && tokens[index + 1].Text == "(")
            {
                if (!TryArguments(tokens, index + 1, out var parameters, out var close) ||
                    close + 1 >= tokens.Count || tokens[close + 1].Text != "{") continue;
                var parameterTypes = new string[parameters.Count];
                var parameterNames = new string[parameters.Count];
                var valid = true;
                for (var parameter = 0; parameter < parameters.Count; parameter++)
                {
                    var tokensForParameter = parameters[parameter];
                    if (tokensForParameter.Count == 3 && tokensForParameter[0].Text == "const")
                        tokensForParameter = tokensForParameter.Skip(1).ToList();
                    if (tokensForParameter.Count != 2 || !IsIdentifier(tokensForParameter[0].Text) ||
                        !IsIdentifier(tokensForParameter[1].Text)) { valid = false; break; }
                    parameterTypes[parameter] = tokensForParameter[0].Text;
                    parameterNames[parameter] = tokensForParameter[1].Text;
                }
                if (!valid) continue;
                var start = close + 2;
                var depth = 1;
                var end = start;
                for (; end < tokens.Count && depth > 0; end++)
                { if (tokens[end].Text == "{") depth++; else if (tokens[end].Text == "}") depth--; }
                if (depth != 0) continue;
                result.Add(new(tokens[index].Text, tokens[index - 1].Text, parameterTypes,
                    parameterNames, tokens.Skip(start).Take(end - start - 1).ToArray()));
                index = end - 1;
                continue;
            }
            if (tokens[index].Text == "{") braceDepth++;
        }
        return result.ToArray();
    }

    private static bool IsLinearizeDepthBody(DepthFunction function)
    {
        if (function.Body.Count != 18) return false;
        var depth = function.ParameterNames[0];
        var setup = function.ParameterNames[1];
        var local = function.Body[1].Text;
        string[] expected = ["float", local, "=", setup, ".", "y", "/", "(", depth, "+", setup,
            ".", "x", ")", ";", "return", local, ";"];
        return function.Body.Select(token => token.Text).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool IsFloatIdentityBody(DepthFunction function) =>
        function.Body.Count == 3 && function.Body[0].Text == "return" &&
        function.Body[1].Text == function.ParameterNames[0] && function.Body[2].Text == ";";

    private static bool IsBindlessDepthDecoderBody(DepthFunction function, IReadOnlySet<string> identityFunctions)
    {
        if (function.Body.Count != 20 || function.Body[0].Text != "return") return false;
        var identity = function.Body[1].Text;
        if (!identityFunctions.Contains(identity)) return false;
        string[] expected = ["return", identity, "(", "texture", "(", "sampler2D", "(",
            function.ParameterNames[0], ".", "rHandle", ".", "xy", ")", ",", function.ParameterNames[1],
            ")", ".", "r", ")", ";"];
        return function.Body.Select(token => token.Text).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool HasBindlessPassTextureArray(IReadOnlyList<Token> module)
    {
        var declarations = 0;
        for (var index = 1; index + 2 < module.Count; index++)
            if (module[index - 1].Text == "BINDLESS_TEXTURE_AND_SAMPLER" &&
                module[index].Text == "g_rb2DPassTextures" && module[index + 1].Text == "[" &&
                module[index + 2].Text == "]") declarations++;
        return declarations == 1;
    }

    private static bool TrySingleAssignment(IReadOnlyList<Token> body, string variable, out string? declaredType,
        out List<Token> rightHandSide, out int writeIndex)
    {
        declaredType = null;
        rightHandSide = [];
        writeIndex = -1;
        var matches = new List<(int Variable, int Operator, int End, int Start)>();
        for (var index = 0; index + 1 < body.Count; index++)
        {
            if (body[index].Text != variable || !IsAssignmentOperator(body[index + 1].Text)) continue;
            var start = index;
            while (start > 0 && body[start - 1].Text is not (";" or "{" or "}")) start--;
            var end = FindStatementEnd(body, index + 2);
            if (end >= body.Count) return false;
            matches.Add((index, index + 1, end, start));
        }
        if (matches.Count != 1) return false;
        var match = matches[0];
        if (body[match.Operator].Text != "=" || IsConditionalWrite(body, match.Variable)) return false;
        if (match.Variable - match.Start == 1 && body[match.Start].Text is "float" or "vec2")
            declaredType = body[match.Start].Text;
        else if (match.Variable != match.Start) return false;
        rightHandSide = body.Skip(match.Operator + 1).Take(match.End - match.Operator - 1).ToList();
        writeIndex = match.Variable;
        return true;
    }

    private static IReadOnlyList<Token> StripWrappingParentheses(IReadOnlyList<Token> tokens)
    {
        var start = 0;
        var end = tokens.Count;
        while (end - start >= 2 && tokens[start].Text == "(" && tokens[end - 1].Text == ")")
        {
            var depth = 0;
            var closesAtEnd = true;
            for (var index = start; index < end; index++)
            {
                if (tokens[index].Text == "(") depth++;
                else if (tokens[index].Text == ")" && --depth == 0 && index != end - 1)
                { closesAtEnd = false; break; }
            }
            if (!closesAtEnd) break;
            start++; end--;
        }
        return tokens.Skip(start).Take(end - start).ToArray();
    }

    private static string Concat(IEnumerable<Token> tokens) => string.Concat(tokens.Select(token => token.Text));
}
