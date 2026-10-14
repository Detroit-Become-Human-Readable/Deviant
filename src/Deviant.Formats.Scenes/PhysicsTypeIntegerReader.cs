namespace Deviant.Formats.Scenes;

internal static class PhysicsTypeIntegerReader
{
    internal static bool TryRead(ReadOnlySpan<byte> bytes, ref int cursor, out ulong value, out int width, out string? error)
    {
        value = 0; width = 0; error = null;
        if ((uint)cursor >= (uint)bytes.Length) { error = "PhysicsTypeIntegerTruncated"; return false; }
        var lead = bytes[cursor]; var mask = 0x7f; var omitLead = false;
        if (lead < 0x80) width = 1;
        else if (lead < 0xc0) { width = 2; mask = 0x3f; }
        else if (lead < 0xe0) { width = 3; mask = 0x1f; }
        else if (lead < 0xe8) { width = 4; mask = 7; }
        else if (lead < 0xf0) { width = 5; mask = 7; }
        else if (lead < 0xf8) { width = 8; mask = 7; }
        else if (lead == 0xf8) { width = 6; omitLead = true; }
        else if (lead == 0xf9) { width = 9; omitLead = true; }
        else { error = "PhysicsTypeIntegerPrefixUnsupported"; return false; }
        if (width > bytes.Length - cursor) { error = "PhysicsTypeIntegerTruncated"; return false; }
        if (!omitLead) value = (ulong)(lead & mask);
        for (var index = 1; index < width; index++) value = (value << 8) | bytes[cursor + index];
        cursor += width; return true;
    }
}
