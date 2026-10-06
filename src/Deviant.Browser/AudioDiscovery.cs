using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using AudioNameEvidence = Deviant.Formats.Audio.Model.NameEvidence;
using AudioNameKind = Deviant.Formats.Audio.Model.NameKind;

namespace Deviant.Browser;

public enum AudioRelationshipStatus { Resolved, Ambiguous, Missing, Unverified }

public sealed record AudioBankGroup(
    string Key,
    string Label,
    uint BankId,
    ResourceIdentity? DescriptorIdentity,
    ResourceIdentity? ContainerIdentity,
    AudioRelationshipStatus ContainerStatus,
    int MediaCount,
    string Evidence);

public sealed record AudioLanguageGroup(
    string? LanguageCode,
    string Label,
    IReadOnlyList<ResourceIdentity> Media,
    IReadOnlyList<string> Evidence);

/// <summary>Creates explicit bank and language groupings from one already-read audio snapshot.</summary>
public sealed class AudioDiscovery
{
    private readonly ArchiveSnapshot snapshot;
    private readonly AudioCatalog catalog;
    private readonly AudioRelationships relationships;

    public AudioDiscovery(ArchiveSnapshot snapshot, AudioCatalog catalog, DetroitAudioSnapshotReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.IndexContentId != snapshot.IndexContentId || catalog.PhysicalSourceStamp != snapshot.PhysicalSourceStamp)
            throw new ArchiveException("SnapshotMismatch", "Audio discovery requires catalog metadata from the selected archive revision.");
        this.snapshot = snapshot;
        this.catalog = catalog;
        relationships = new AudioRelationships(snapshot, catalog, reader);
    }

    public IReadOnlyList<AudioBankGroup> GetBanks()
    {
        var result = new List<AudioBankGroup>(catalog.Banks.Count);
        foreach (var bank in catalog.Banks.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id))
        {
            var identity = IsTopLevelIdentity(bank.Identity, expectedType: 1022) ? bank.Identity : null;
            var labelEvidence = FindVerifiedBankName(bank);
            var label = labelEvidence?.Text ?? $"Bank 0x{bank.Id:X8}";
            var containerStatus = GetContainerStatus(bank, out var containerIdentity, out var containerEvidence);
            var mediaCount = catalog.Media.Count(media => media.BankKey == bank.Key);
            var evidence = labelEvidence is null
                ? "Displayed by stored BKHD bank ID; no verified bank name was available."
                : $"Verified Wwise bank name from {labelEvidence.Source} using {labelEvidence.HashMethod ?? "stored parser evidence"}.";
            if (containerEvidence.Length > 0) evidence += " " + containerEvidence;
            result.Add(new(bank.Key, label, bank.Id, identity, containerIdentity, containerStatus, mediaCount, evidence));
        }
        return result.AsReadOnly();
    }

    public IReadOnlyList<AudioLanguageGroup> GetLanguages()
    {
        var groups = catalog.Media.Where(IsCurrentMediaIdentity)
            .GroupBy(media => NormalizeLanguageKey(media.Language), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.Select(media => media.Identity!).DistinctBy(identity => identity.Key)
                    .OrderBy(identity => identity.Ordinal).ThenBy(identity => identity.MemberPath, StringComparer.Ordinal).ToArray();
                var language = string.IsNullOrEmpty(group.Key) ? null : group.Key;
                var evidence = group.Select(media => media.RawLanguageId is { } raw
                        ? $"Media language field '{media.Language}' with raw bank language ID {raw}."
                        : $"Media language field '{media.Language}'.")
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new AudioLanguageGroup(language, language ?? "No language metadata", members, evidence);
            }).ToArray();
        return Array.AsReadOnly(groups);
    }

    public AudioRelationships Relationships => relationships;

    private AudioNameEvidence? FindVerifiedBankName(BankInfo bank)
    {
        var identity = bank.Identity;
        if (!IsTopLevelIdentity(identity, expectedType: 1022) || identity is null)
            return null;
        var row = snapshot.Records[(int)identity.Ordinal];
        if (row.Type != 1022) return null;
        var resources = catalog.Resources.Where(item => item.IndexOrdinal == row.Ordinal && item.Type == 1022).Take(2).ToArray();
        if (resources.Length != 1) return null;
        var names = catalog.Names.Where(name => name.Namespace == "WwiseBank" && name.Id == bank.Id &&
            name.Kind == AudioNameKind.Verified && name.CollisionGroup is null && name.Source == resources[0].Key).Take(2).ToArray();
        return names.Length == 1 ? names[0] : null;
    }

    private AudioRelationshipStatus GetContainerStatus(BankInfo bank, out ResourceIdentity? containerIdentity, out string evidence)
    {
        containerIdentity = bank.ContainerIdentity;
        if (!IsTopLevelIdentity(bank.Identity, expectedType: 1022))
        { containerIdentity = null; evidence = "The bank descriptor identity does not belong to this archive revision."; return AudioRelationshipStatus.Unverified; }
        var ambiguity = bank.Diagnostics.Where(item => item.Code == "AmbiguousBankContainer").Select(item => item.Message).Distinct(StringComparer.Ordinal).ToArray();
        if (ambiguity.Length > 0) { containerIdentity = null; evidence = string.Join(" ", ambiguity); return AudioRelationshipStatus.Ambiguous; }
        var missing = bank.Diagnostics.Where(item => item.Code == "MissingBankContainer").Select(item => item.Message).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) { containerIdentity = null; evidence = string.Join(" ", missing); return AudioRelationshipStatus.Missing; }
        if (containerIdentity is null) { evidence = "No unique container identity was recorded for this bank."; return AudioRelationshipStatus.Missing; }
        if (containerIdentity.IndexContentId != snapshot.IndexContentId || containerIdentity.Ordinal < 0 ||
            containerIdentity.Ordinal >= snapshot.Records.Count || containerIdentity.MemberPath != "bank")
        { containerIdentity = null; evidence = "The bank payload identity is outside this archive revision or has an unsupported member path."; return AudioRelationshipStatus.Unverified; }
        var row = snapshot.Records[(int)containerIdentity.Ordinal];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid)
        { containerIdentity = null; evidence = "The bank payload identity does not resolve to a readable type-29 container row."; return AudioRelationshipStatus.Unverified; }
        evidence = $"Bank descriptor resource {bank.Identity?.Ordinal.ToString() ?? "unknown"} resolved to unique type-29 container resource {row.Ordinal} by the stored descriptor reference.";
        return AudioRelationshipStatus.Resolved;
    }

    private static string NormalizeLanguageKey(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    internal bool IsCurrentMediaIdentity(MediaEntry media) => IsCurrentMediaIdentity(snapshot, catalog, media);

    internal static bool IsCurrentMediaIdentity(ArchiveSnapshot snapshot, AudioCatalog catalog, MediaEntry media)
    {
        var identity = media.Identity;
        if (identity is null || identity.IndexContentId != snapshot.IndexContentId || identity.Ordinal < 0 ||
            identity.Ordinal >= snapshot.Records.Count) return false;
        var row = snapshot.Records[(int)identity.Ordinal];
        if (identity.MemberPath is null or "wem") return row.Type == 1033 && media.BankKey.Length == 0;
        if (identity.MemberPath.StartsWith("source/", StringComparison.Ordinal))
        {
            var banks = catalog.Banks.Where(bank => bank.Key == media.BankKey).ToArray();
            return row.Type == 1022 && banks.Length == 1 && banks[0].Identity?.Ordinal == row.Ordinal;
        }
        if (identity.MemberPath.StartsWith("media/", StringComparison.Ordinal))
        {
            var banks = catalog.Banks.Where(bank => bank.Key == media.BankKey).ToArray();
            return row.Type == 29 && banks.Length == 1 && banks[0].ContainerIdentity?.Ordinal == row.Ordinal;
        }
        return false;
    }

    private bool IsTopLevelIdentity(ResourceIdentity? identity, uint expectedType) =>
        identity is not null && identity.IndexContentId == snapshot.IndexContentId && identity.MemberPath is null &&
        identity.Ordinal >= 0 && identity.Ordinal < snapshot.Records.Count && snapshot.Records[(int)identity.Ordinal].Type == expectedType;

}
