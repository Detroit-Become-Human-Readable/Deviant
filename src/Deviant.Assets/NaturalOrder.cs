using System.Globalization;
using System.Text;
namespace Deviant.Assets;

public static class NaturalOrder
{
    public static string Key(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] is >= '0' and <= '9')
            {
                var start = i; while (i < text.Length && text[i] is >= '0' and <= '9') i++;
                var digits = text[start..i].TrimStart('0'); if (digits.Length == 0) digits = "0";
                result.Append('\u0001').Append(digits.Length.ToString("D8", CultureInfo.InvariantCulture)).Append(digits).Append('\u0002');
            }
            else result.Append(char.ToUpperInvariant(text[i++]));
        }
        return result.ToString();
    }
}
