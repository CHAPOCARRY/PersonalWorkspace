using System.Globalization;
using System.Numerics;

namespace PersonalWorkspace.Core;

// Shared numeric text boundary; no Task/Tracker business rules or floating-point conversions.
public static class ExactValueText
{
    public static string Store(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    public static decimal Restore(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    public static string Input(decimal value) => value.ToString("0.############################", CultureInfo.CurrentCulture);
    public static string Duration(long seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    public static decimal ParseInput(string text, bool duration = false)
    {
        text = text.Trim();
        if (!duration)
        {
            if (text.Length <= 128 && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value))
            {
                // Decimal.TryParse otherwise silently rounds inputs beyond decimal precision.
                var format = CultureInfo.CurrentCulture.NumberFormat;
                var unsigned = text;
                if (unsigned.StartsWith(format.NegativeSign, StringComparison.Ordinal)) unsigned = unsigned[format.NegativeSign.Length..];
                else if (unsigned.StartsWith(format.PositiveSign, StringComparison.Ordinal)) unsigned = unsigned[format.PositiveSign.Length..];
                var point = unsigned.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
                var inputScale = point < 0 ? 0 : unsigned.Length - point - format.NumberDecimalSeparator.Length;
                var digits = unsigned.Replace(format.NumberDecimalSeparator, "", StringComparison.Ordinal);
                var bits = decimal.GetBits(value);
                var coefficient = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
                if (BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var input) &&
                    input * BigInteger.Pow(10, (bits[3] >> 16) & 255) == coefficient * BigInteger.Pow(10, inputScale)) return value;
                throw new FormatException("The number exceeds the supported exact decimal precision.");
            }
            throw new FormatException("Enter a decimal number using your Windows decimal separator, without thousands separators.");
        }
        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(part => part.Length > 0 && part.All(c => c is >= '0' and <= '9')))
        {
            try
            {
                var numbers = parts.Select(part => long.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                if (numbers[^1] < 60 && (parts.Length == 2 || numbers[1] < 60))
                {
                    var seconds = parts.Length == 2 ? checked(numbers[0] * 60 + numbers[1]) : checked(numbers[0] * 3600 + numbers[1] * 60 + numbers[2]);
                    if (seconds <= long.MaxValue / TimeSpan.TicksPerSecond) return seconds;
                }
            }
            catch (OverflowException) { }
        }
        throw new FormatException("Enter duration as minutes:seconds or hours:minutes:seconds, such as 30:00 or 1:15:30.");
    }
}
