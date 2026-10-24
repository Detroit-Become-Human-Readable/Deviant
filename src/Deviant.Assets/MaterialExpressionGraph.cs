namespace Deviant.Assets;

/// <summary>Reference-identity traversal of a bounded material expression DAG.</summary>
public static class MaterialExpressionGraph
{
    public static IReadOnlyList<MaterialExpression> GetPostOrder(MaterialExpression root,
        int maximumNodes = 4096, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (maximumNodes < 1) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        token.ThrowIfCancellationRequested();
        var states = new Dictionary<MaterialExpression, byte>(ReferenceEqualityComparer.Instance);
        var result = new List<MaterialExpression>();
        var pending = new Stack<(MaterialExpression Node, IReadOnlyList<MaterialExpression> Arguments, int NextArgument)>();
        Enter(root);
        while (pending.TryPop(out var frame))
        {
            token.ThrowIfCancellationRequested();
            if (frame.NextArgument < frame.Arguments.Count)
            {
                var argument = frame.Arguments[frame.NextArgument];
                if (argument is null) throw Invalid("An expression contains a missing argument.");
                pending.Push((frame.Node, frame.Arguments, frame.NextArgument + 1));
                if (states.TryGetValue(argument, out byte state))
                {
                    if (state == 1) throw Invalid("The expression graph contains a cycle.");
                }
                else Enter(argument);
            }
            else
            {
                states[frame.Node] = 2;
                result.Add(frame.Node);
            }
        }
        token.ThrowIfCancellationRequested();
        return result.AsReadOnly();

        void Enter(MaterialExpression node)
        {
            token.ThrowIfCancellationRequested();
            if (node.Arguments is not { } arguments) throw Invalid("An expression has no argument list.");
            if (states.Count >= maximumNodes) throw Invalid("The expression graph exceeds the node limit.");
            states.Add(node, 1);
            pending.Push((node, arguments, 0));
        }
        static ArgumentException Invalid(string message) => new(message, nameof(root));
    }
}
