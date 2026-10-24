using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private static TextureDiscoveryEntry ThinTextureProjection(TextureDiscoveryEntry entry) => entry with
    {
        NameCandidates = [], ProvenanceLines = [], AdvancedLines = [],
        Owners = entry.Owners.Select(link => link with { Evidence = [] }).ToArray(),
        Related = entry.Related.Select(link => link with { Evidence = [] }).ToArray()
    };

    private static (string Column, string Parameter, object? Value)[] TextureProjectionScalars(TextureDiscoveryEntry entry) =>
    [
        ("ordinal", "$ordinal", entry.Identity.Ordinal), ("name_kind", "$nameKind", (int)entry.NameKind),
        ("kind", "$kind", (int)entry.Kind), ("label", "$label", entry.Label),
        ("sort_key", "$sort", NaturalOrder.Key(TextureNameDisplay.Project(entry.Label))), ("search", "$search", entry.SearchText),
        ("width", "$width", entry.Width), ("height", "$height", entry.Height), ("format", "$format", entry.Format),
        ("context_key", "$context", entry.ContextKey), ("context_label", "$contextLabel", entry.ContextLabel),
        ("has_owner", "$owner", entry.Owners.Any(link => link.Verified) ? 1 : 0),
        ("has_conflict", "$conflict", entry.ConflictLines.Count > 0 ? 1 : 0),
        ("has_metadata", "$metadata", entry.MetadataIdentities.Count > 0 ? 1 : 0)
    ];

    private static IEnumerable<(int Relation, string Key, string Role, string Label, bool Verified)> TextureProjectionLinks(TextureDiscoveryEntry entry)
    {
        foreach (var link in entry.Owners) yield return (0, link.Identity.Key, link.Role ?? "", link.Label, link.Verified);
        foreach (var link in entry.Related) yield return (1, link.Identity.Key, link.Role ?? "", link.Label, link.Verified);
        foreach (var catalog in entry.CatalogKeys.Distinct(StringComparer.Ordinal)) yield return (2, catalog, "", "", true);
        foreach (var metadata in entry.MetadataIdentities.Distinct()) yield return (3, metadata.Key, "", "", true);
    }

    private static byte[] EncodeTextureProjectionNameProof(TextureDiscoveryEntry entry, TextureDiscoveryEntry thin)
    {
        var sources = entry.NameCandidates.SelectMany(candidate => candidate.Evidence)
            .Concat(entry.Owners.Concat(entry.Related).SelectMany(link => link.Evidence))
            .DistinctBy(evidence => (evidence.Source, evidence.Revision, evidence.Reader, evidence.ReaderVersion,
                evidence.SourceSha256, evidence.PhysicalExtent)).ToArray();
        return EncodeCompactTextureNameProof(thin, sources);
    }

    private bool TextureOwnerPublicationUnchanged(string profile, TextureProjectionOwnerCheckpoint checkpoint,
        IReadOnlyList<TextureDiscoveryEntry> entries, long head, SqliteTransaction transaction, CancellationToken token)
    {
        using (var pending = TextureCommand("SELECT 1 FROM texture_projection_owner_rebuilds " +
            "WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction))
        {
            pending.Parameters.AddWithValue("$identity", checkpoint.Identity.Key);
            if (pending.ExecuteScalar() is not null) return false;
        }
        using (var owner = TextureCommand("SELECT payload FROM texture_projection_owners " +
            "WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction))
        {
            owner.Parameters.AddWithValue("$identity", checkpoint.Identity.Key);
            if (owner.ExecuteScalar() is not string stored || !StringComparer.Ordinal.Equals(stored, JsonSerializer.Serialize(checkpoint))) return false;
        }
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var thin = ThinTextureProjection(entry);
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(entry with { Alias = null });
            if (encoded.Length > MaximumTextureEvidenceBytes) return false;
            var scalars = TextureProjectionScalars(entry);
            using var read = TextureCommand($$"""
                SELECT p.thin,CASE WHEN length(e.payload)<=$maximum THEN e.payload ELSE NULL END,e.sha256,
                    CASE WHEN length(n.payload)<=$proofMaximum THEN n.payload ELSE NULL END,n.sha256,n.evidence_sha256,{{string.Join(",", scalars.Select(value => "p." + value.Column))}}
                FROM texture_projection_rows p
                JOIN texture_projection_evidence e ON e.revision=p.revision AND e.profile=p.profile AND e.identity=p.identity AND e.generation=p.generation
                JOIN texture_projection_name_proofs n ON n.revision=p.revision AND n.profile=p.profile AND n.identity=p.identity AND n.generation=p.generation
                WHERE p.revision=$revision AND p.profile=$profile AND p.identity=$identity AND p.generation=(
                    SELECT MAX(v.generation) FROM texture_projection_rows v WHERE v.revision=p.revision AND v.profile=p.profile AND v.identity=p.identity AND v.generation<=$head)
                AND EXISTS(SELECT 1 FROM texture_projection_links l WHERE l.revision=p.revision AND l.profile=p.profile
                    AND l.identity=p.identity AND l.generation=p.generation AND l.relation=4 AND l.link_key=$owner)
                """, profile, transaction);
            read.Parameters.AddWithValue("$identity", entry.Identity.Key); read.Parameters.AddWithValue("$head", head);
            read.Parameters.AddWithValue("$owner", checkpoint.Identity.Key);
            read.Parameters.AddWithValue("$maximum", MaximumTextureEvidenceBytes); read.Parameters.AddWithValue("$proofMaximum", MaximumTextureNameProofBytes);
            using (var rows = read.ExecuteReader())
            {
                if (!rows.Read() || rows.IsDBNull(1) || rows.IsDBNull(3) || rows.GetString(0) != JsonSerializer.Serialize(thin) ||
                    !encoded.AsSpan().SequenceEqual((byte[])rows[1])) return false;
                string evidenceHash = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
                if (rows.GetString(2) != evidenceHash || rows.GetString(5) != evidenceHash) return false;
                var proof = (byte[])rows[3];
                if (rows.GetString(4) != Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant() ||
                    !proof.AsSpan().SequenceEqual(EncodeTextureProjectionNameProof(entry, thin))) return false;
                for (int index = 0; index < scalars.Length; index++)
                {
                    object expected = scalars[index].Value is int number ? (long)number : scalars[index].Value ?? DBNull.Value;
                    if (!Equals(rows.GetValue(index + 6), expected)) return false;
                }
            }
            var expectedLinks = TextureProjectionLinks(entry).DistinctBy(link => (link.Relation, link.Key, link.Role)).ToHashSet();
            using var links = TextureCommand("SELECT relation,link_key,role,label,verified FROM texture_projection_links " +
                "WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation=(SELECT MAX(generation) " +
                "FROM texture_projection_rows WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation<=$head) " +
                "AND relation IN(0,1,2,3)", profile, transaction);
            links.Parameters.AddWithValue("$identity", entry.Identity.Key); links.Parameters.AddWithValue("$head", head);
            using var storedLinks = links.ExecuteReader();
            int count = 0;
            while (storedLinks.Read())
            {
                token.ThrowIfCancellationRequested(); count++;
                long verified = storedLinks.GetInt64(4);
                if (verified is not (0 or 1)) return false;
                if (!expectedLinks.Contains((storedLinks.GetInt32(0), storedLinks.GetString(1), storedLinks.GetString(2),
                    storedLinks.GetString(3), verified == 1))) return false;
            }
            if (count != expectedLinks.Count) return false;
        }
        return true;
    }
}
