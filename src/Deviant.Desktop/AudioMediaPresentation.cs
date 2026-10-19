using System.Globalization;
using Deviant.Browser;

namespace Deviant.Desktop;

internal static class AudioMediaPresentation
{
    public static string Details(AudioExplorerRow row)
    {
        if (row.SourceMedia is not { } media)
            return string.Join(" · ", new[] { row.Bank, row.Descriptor.Language, row.Descriptor.Codec }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var heading = $"{row.Bank} · {media.Language} · {media.Codec}";
        if (media.Midi is not { } midi)
            return $"{heading} · {media.Duration?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} s · {media.Channels} ch\n{row.CompletenessBadge}";

        var division = (midi.Division & 0x8000) == 0
            ? $"{midi.Division} PPQN"
            : SmpteDivision(midi.Division);
        var details = $"{heading}\nFormat {midi.Format} · {midi.TrackCount} {(midi.TrackCount == 1 ? "track" : "tracks")} · {division}";
        return row.CompletenessBadge.Length == 0 ? details : details + "\n" + row.CompletenessBadge;
    }

    private static string SmpteDivision(ushort division)
    {
        var frameCode = unchecked((sbyte)(division >> 8));
        var rate = frameCode == -29 ? "29.97" : (-frameCode).ToString(CultureInfo.InvariantCulture);
        return $"SMPTE {rate} fps · {division & 0xff} ticks/frame";
    }
}
