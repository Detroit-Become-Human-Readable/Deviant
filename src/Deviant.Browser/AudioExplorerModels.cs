using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio.Model;

namespace Deviant.Browser;

public sealed record AudioExplorerProgress(string Phase, long Completed, long Total, string? Detail = null);
public sealed record AudioExplorerUpdate(long Generation, bool IsComplete, IReadOnlyList<AudioBankChoice> Banks,
    long MediaCount, string Phase, string? Detail = null, long ProgressCompleted = 0, long ProgressTotal = 0);

public sealed record AudioBankChoice(string Key, string Label, long MediaCount, uint Id, string Name, string? Reason = null)
{
    public bool CanExportOriginal { get; init; }
}

public sealed record AudioExplorerQuery(
    string? Search = null,
    string? BankKey = null,
    string? Language = null,
    string? Codec = null,
    double? MinimumDuration = null,
    double? MaximumDuration = null,
    int? Channels = null,
    AssetAvailability? Availability = null,
    MediaCompleteness? Completeness = null,
    long Offset = 0,
    int Limit = 100,
    AssetSort Sort = AssetSort.Default,
    bool Descending = false,
    string? Category = null);

public enum AudioMediaHydrationState { SourceDetailsRequired, Ready }

public sealed record AudioExplorerRow
{
    public AudioExplorerRow(AssetDescriptor Descriptor, MediaEntry? Media, string? UserAlias, string? BankLabel = null)
    { this.Descriptor = Descriptor; SourceMedia = Media; this.UserAlias = UserAlias; this.BankLabel = BankLabel; }

    public AssetDescriptor Descriptor { get; init; }
    public MediaEntry? SourceMedia { get; internal init; }
    public MediaEntry Media => SourceMedia ?? throw new ArchiveException("AudioSourceDetailsRequired", "Load the selected clip's source details first.");
    public string? UserAlias { get; init; }
    public string? BankLabel { get; init; }
    public bool IsHydrated => SourceMedia is not null;
    public bool SelectedSourceVerified { get; internal init; }
    internal Deviant.Formats.Audio.SelectedAudioClassificationScope? SelectedClassificationScope { get; init; }
    public string? CachePersistenceErrorCode { get; internal init; }
    public AudioMediaHydrationState HydrationState => IsHydrated ? AudioMediaHydrationState.Ready : AudioMediaHydrationState.SourceDetailsRequired;
    public string? HydrationErrorCode { get; internal init; }
    public ResourceIdentity Identity => Descriptor.Identity;
    public string Label => UserAlias ?? Descriptor.Label;
    public string PlayableLabel => Label;
    public string NaturalSortKey => Descriptor.SortKey ?? NaturalOrder.Key(PlayableLabel);
    public string IdText => SourceMedia?.IdText ?? "";
    public string Bank => BankLabel ?? (SourceMedia is { } media && !string.IsNullOrWhiteSpace(media.BankName) ? media.BankName : "Standalone");
    public string CompletenessBadge => SourceMedia?.Completeness switch
    {
        MediaCompleteness.Complete => "",
        MediaCompleteness.Fragment => "Partial",
        _ => Descriptor.Completeness == AssetCompleteness.Partial ? "Partial" : ""
    };
    public bool CanPreview => IsHydrated && Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview);
    public bool CanPreviewPartial => IsHydrated && Descriptor.Capabilities.HasFlag(AssetCapabilities.PartialPreview);
    public bool CanExportOriginal => IsHydrated && Descriptor.Capabilities.HasFlag(AssetCapabilities.Original);
    public bool CanConvert => IsHydrated && (Descriptor.Capabilities.HasFlag(AssetCapabilities.Wav) || Descriptor.Capabilities.HasFlag(AssetCapabilities.Ogg));
    public string? UnavailableReason => SourceMedia is null ? "Source details are not loaded." :
        MediaPolicy.GetUnavailableReason(SourceMedia) is { Length: > 0 } reason ? reason : null;

    public void Deconstruct(out AssetDescriptor Descriptor, out MediaEntry Media, out string? UserAlias, out string? BankLabel)
    { Descriptor = this.Descriptor; Media = this.Media; UserAlias = this.UserAlias; BankLabel = this.BankLabel; }
}

public sealed record AudioExplorerPage(long Total, IReadOnlyList<AudioExplorerRow> Rows)
{
    public long Generation { get; init; }
}

public sealed record AudioExportReview(
    ArchiveSnapshot Snapshot,
    IReadOnlyList<AudioExplorerRow> Rows,
    long Bytes,
    long CompleteCount,
    long UnverifiedCount,
    long PartialCount,
    long UnavailableCount);
