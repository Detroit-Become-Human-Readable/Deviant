using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Deviant.Formats.Audio.Model;

// Adapted from Detroit Audio Extractor's AutomaticNames identifier recovery.
// Copyright (c) 2026 Detroit Audio contributors.
// Licensed under the MIT License:
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions: The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
// NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
/// <summary>
/// Recovers Wwise event names from identifiers found in catalog metadata and localization text.
/// </summary>
public static partial class AudioEventNameRecovery
{
    private const int MaximumUniqueIdentifiers = 500_000;
    private const string HashMethod = "FNV-1 lowercase UTF-8";

    public static void Apply(AudioCatalog catalog, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        token.ThrowIfCancellationRequested();

        var eventsById = catalog.Events
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var observedIds = eventsById.Keys.ToHashSet();
        var candidates = new HashSet<NameEvidence>();
        var existingCatalogEvidence = catalog.Names.ToHashSet();

        // Directly scoped evidence is useful even when a source string is not itself the hashed name.
        foreach (var evidence in catalog.Names)
        {
            token.ThrowIfCancellationRequested();
            if (evidence.Namespace == "WwiseEvent" && evidence.Kind == NameKind.Candidate && observedIds.Contains(evidence.Id))
                candidates.Add(evidence);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = catalog.Names.Select(name => (name.Text, name.Source, name.Offset, name.InputSha256))
            .Concat(catalog.Localization.Select(text => (Text: text.Key, text.Source, text.Offset, InputSha256: (string?)null)))
            .Concat(catalog.Localization.Select(text => (text.Text, text.Source, text.Offset, InputSha256: (string?)null)));
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (seen.Count >= MaximumUniqueIdentifiers) break;
            foreach (Match match in Identifier().Matches(source.Text))
            {
                token.ThrowIfCancellationRequested();
                if (seen.Count >= MaximumUniqueIdentifiers) break;
                var value = match.Value;
                var variants = new List<string> { value };
                for (var index = value.IndexOf('_'); index >= 0 && variants.Count < 32; index = value.IndexOf('_', index + 1))
                    if (value.Length - index > 2) variants.Add(value[(index + 1)..]);

                foreach (var candidate in variants)
                {
                    if (seen.Count >= MaximumUniqueIdentifiers) break;
                    if (candidate.Length > 256 || !seen.Add(candidate)) continue;
                    var id = Hash(candidate);
                    if (!observedIds.Contains(id)) continue;
                    candidates.Add(new NameEvidence(candidate, "WwiseEvent", id, NameKind.Candidate,
                        source.Source, source.Offset, "Medium", HashMethod, InputSha256: source.InputSha256));
                }
            }
        }

        foreach (var evidence in candidates.OrderBy(item => item.Id)
                     .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Text, StringComparer.Ordinal)
                     .ThenBy(item => item.Source, StringComparer.Ordinal)
                     .ThenBy(item => item.Offset)
                     .ThenBy(item => item.Confidence, StringComparer.Ordinal)
                     .ThenBy(item => item.HashMethod, StringComparer.Ordinal)
                     .ThenBy(item => item.InputSha256, StringComparer.Ordinal)
                     .ThenBy(item => item.CollisionGroup, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (existingCatalogEvidence.Add(evidence)) catalog.Names.Add(evidence);
            if (eventsById.TryGetValue(evidence.Id, out var entries))
                foreach (var entry in entries)
                    if (!entry.Names.Contains(evidence)) entry.Names.Add(evidence);
        }

        foreach (var entry in catalog.Events)
        {
            token.ThrowIfCancellationRequested();
            var hasMeaningfulName = IsMeaningful(entry.Name);
            var eventNames = entry.Names
                .Where(name => name.Namespace == "WwiseEvent" && name.Id == entry.Id && IsMeaningful(name.Text))
                .ToArray();
            var unique = eventNames
                .Select(name => name.Text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unique.Length == 1 && !hasMeaningfulName) entry.Name = unique[0];
            else if (unique.Length > 1 && hasMeaningfulName)
            {
                var exact = eventNames.Where(name => string.Equals(name.Text.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Any(name => name.Kind == NameKind.Candidate) &&
                    !exact.Any(name => name.Kind is NameKind.Stored or NameKind.Verified))
                    entry.Name = entry.Id.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static uint Hash(string value)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash = unchecked(hash * 16777619);
            hash ^= b;
        }
        return hash;
    }

    private static bool IsMeaningful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        if (text.All(char.IsDigit) || (text.Length > 1 && (text[0] == '+' || text[0] == '-') && text[1..].All(char.IsDigit)))
            return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text.Length > 2 && !text[2..].All(Uri.IsHexDigit);
        return true;
    }

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_\-.]{1,255}")]
    private static partial Regex Identifier();
}
