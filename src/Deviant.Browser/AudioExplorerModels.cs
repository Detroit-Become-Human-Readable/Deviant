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
    bool Descending = false);

public sealed record AudioExplorerRow(AssetDescriptor Descriptor, MediaEntry Media, string? UserAlias, string? BankLabel = null)
{
    public ResourceIdentity Identity => Descriptor.Identity;
    public string Label => UserAlias ?? Descriptor.Label;
    public string PlayableLabel => Label;
    public string NaturalSortKey => Descriptor.SortKey ?? NaturalOrder.Key(PlayableLabel);
    public string IdText => Media.IdText;
    public string Bank => BankLabel ?? (string.IsNullOrWhiteSpace(Media.BankName) ? "Standalone" : Media.BankName);
    public string CompletenessBadge => Media.Completeness switch
    {
        MediaCompleteness.Complete => "",
        MediaCompleteness.Fragment => "Partial",
        _ => ""
    };
    public bool CanPreview => Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview);
    public bool CanPreviewPartial => Descriptor.Capabilities.HasFlag(AssetCapabilities.PartialPreview);
    public bool CanExportOriginal => Descriptor.Capabilities.HasFlag(AssetCapabilities.Original);
    public bool CanConvert => Descriptor.Capabilities.HasFlag(AssetCapabilities.Wav) || Descriptor.Capabilities.HasFlag(AssetCapabilities.Ogg);
    public string? UnavailableReason => MediaPolicy.GetUnavailableReason(Media) is { Length: > 0 } reason ? reason : null;
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
