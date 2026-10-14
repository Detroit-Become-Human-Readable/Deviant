using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deviant.Assets;

/// <summary>Build evidence joined to census observations without crossing identifier namespaces.</summary>
public sealed class TypeRegistry
{
    private readonly IReadOnlyList<TypeDefinition> definitions;
    public TypeRegistry(IEnumerable<TypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        this.definitions = definitions.GroupBy(item => item.Type).Select(group =>
        {
            var first = group.First();
            var names = group.SelectMany(item => new[] { item.Name }.Concat(item.CompetingNames ?? [])).Distinct(StringComparer.Ordinal).ToArray();
            string Merge(Func<TypeSupport, string> value) { var values = group.Select(item => value(item.Support)).Distinct().ToArray(); return values.Length == 1 ? values[0] : "Mixed profiles"; }
            return first with { CompetingNames = names.Length > 1 ? names : [], Registered = group.Any(item => item.Registered),
                Fields = group.SelectMany(item => item.Fields).Distinct().ToArray(),
                Tags = group.SelectMany(item => item.Tags ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                Support = new(Merge(item => item.Structure), Merge(item => item.Properties), Merge(item => item.References),
                    Merge(item => item.Placement), Merge(item => item.Visualization), Merge(item => item.Export)),
                Evidence = group.SelectMany(item => item.Evidence).Distinct().ToArray(),
                Questions = group.SelectMany(item => item.Questions).Distinct().ToArray() };
        }).ToArray();
    }

    public IReadOnlyList<TypeRegistryEntry> Reconcile(IEnumerable<TypeObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var materialized = observations.ToArray();
        if (materialized.Any(item => item.Count < 0 || item.ContainerCount < 0)) throw new ArgumentException("Observation counts cannot be negative.", nameof(observations));
        var observed = materialized.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => new TypeObservation(group.Key,
            group.Sum(item => item.Count), group.Sum(item => item.ContainerCount),
            group.SelectMany(item => item.Samples).Distinct().Take(100).ToArray(), group.All(item => item.ValidatedHeader)));
        if (observed.Values.Any(item => item.Count < 0 || item.ContainerCount < 0)) throw new ArgumentException("Observation counts cannot be negative.", nameof(observations));
        var known = definitions.ToDictionary(item => item.Type);
        return known.Keys.Concat(observed.Keys).Distinct().Select(key =>
        {
            observed.TryGetValue(key, out var occurrence);
            if (!known.TryGetValue(key, out var definition))
            {
                var label = key.Domain switch { TypeDomain.SerializedClass => key.Tag ?? "Class", TypeDomain.Native => $"Native class {key.Id}", _ => $"Resource type {key.Id}" };
                definition = new(key, label, "Unclassified", [], [], new(), ["Schema has not been mapped."]);
            }
            return new TypeRegistryEntry(definition, occurrence?.Count ?? 0, occurrence?.ContainerCount ?? 0, occurrence?.Samples ?? []);
        }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Type.Key, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<TypeRegistryEntry> Search(IEnumerable<TypeRegistryEntry> entries, string? search = null,
        TypeDomain? domain = null, string? category = null, bool observedOnly = false)
    {
        var terms = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Where(entry => (!domain.HasValue || entry.Type.Domain == domain) && (!observedOnly || entry.Count > 0) &&
            (string.IsNullOrEmpty(category) || entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
            terms.All(term => string.Join(' ', new[] { entry.Name, entry.Category, entry.Type.Key }
                .Concat(entry.Definition.CompetingNames ?? []).Concat(entry.Definition.Tags ?? [])).Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}

