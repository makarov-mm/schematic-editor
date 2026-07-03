using System.Globalization;

namespace SchematicEditor.Core;

/// <summary>
/// Parses component values like "10k", "4.7k", "100n", "12V", "50Hz", "12V 5W".
/// Multiplier prefixes are case-sensitive where it matters (m = milli, M = mega).
/// </summary>
public static class Units
{
    /// <summary>Parse a single magnitude with an optional SI prefix and unit ("4.7kOhm" → 4700).</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or '-' or '+'))
            i++;
        if (i == 0) return false;

        if (!double.TryParse(text[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;

        string rest = text[i..].Trim();
        double multiplier = 1;

        if (rest.Length > 0)
        {
            // "Meg" is the only multi-letter multiplier worth supporting.
            if (rest.StartsWith("Meg", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1e6;
                rest = rest[3..];
            }
            else if (!IsUnitWord(rest))
            {
                multiplier = rest[0] switch
                {
                    'p' => 1e-12,
                    'n' => 1e-9,
                    'u' or '\u00b5' => 1e-6,
                    'm' => 1e-3,
                    'k' or 'K' => 1e3,
                    'M' => 1e6,
                    'G' => 1e9,
                    _ => double.NaN,
                };
                if (double.IsNaN(multiplier)) return false;
                rest = rest[1..];
            }
        }

        if (rest.Length > 0 && !IsUnitWord(rest)) return false;

        value = number * multiplier;
        return true;
    }

    private static bool IsUnitWord(string s) =>
        s.Equals("V", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("A", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("W", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Hz", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("F", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("H", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Ohm", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("R", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("\u03a9", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse "12V 5W" style lamp ratings. Returns resistance and rated power.</summary>
    public static bool TryParseLampRating(string? text, out double resistance, out double ratedPower)
    {
        resistance = 100;
        ratedPower = 1;
        if (string.IsNullOrWhiteSpace(text)) return false;

        double? volts = null, watts = null, ohms = null;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith('W') && TryParse(part, out double w)) watts = w;
            else if (part.EndsWith('V') && TryParse(part, out double v)) volts = v;
            else if (TryParse(part, out double r)) ohms = r;
        }

        if (volts is { } vv && watts is { } ww && ww > 0)
        {
            resistance = vv * vv / ww;
            ratedPower = ww;
            return true;
        }
        if (ohms is { } rr and > 0)
        {
            resistance = rr;
            ratedPower = watts ?? 1;
            return true;
        }
        return false;
    }

    /// <summary>Parse "5V 50Hz" style AC source specs (amplitude + frequency).</summary>
    public static bool TryParseAcSpec(string? text, out double amplitude, out double frequency)
    {
        amplitude = 5;
        frequency = 50;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool any = false;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith("Hz", StringComparison.OrdinalIgnoreCase) && TryParse(part, out double f))
            {
                frequency = f;
                any = true;
            }
            else if (TryParse(part, out double a))
            {
                amplitude = a;
                any = true;
            }
        }
        return any;
    }
}
