using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DrawingTakeoff;

/// <summary>
/// Conservative normalized identity for consolidating the same material across callouts
/// and sheets. Product-critical dimensions and treatments are retained; installation notes
/// such as spacing, side-of-wall wording and TYPICAL are excluded.
/// </summary>
internal static class MaterialIdentity
{
    public static string Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var s = value.Trim().ToUpperInvariant()
            .Replace('×', 'X').Replace('–', '-').Replace('—', '-').Replace('”', '"');
        s = Regex.Replace(s, @"^\s*\[NOT IN CATALOG\]\s*", "", RegexOptions.IgnoreCase);

        // Parenthetical layer count is an assembly note, not a different product.
        s = Regex.Replace(s, @"^\s*\(\s*\d+\s*\)\s*", "");
        s = Regex.Replace(s, @"^\s*\d+\s+(?:ST|ND|RD|TH)?\s*LAYERS?\s+(?:OF\s+)?", "");
        s = Regex.Replace(s, @"^\s*(?:FIRST|SECOND|THIRD|FOURTH)\s+LAYER\s+OF\s+", "");

        // Keep blocking types distinct before stripping their installation wording.
        if (Regex.IsMatch(s, @"\bFIRE\s+BLOCKING\b")) return "FIREBLOCKING";
        if (Regex.IsMatch(s, @"\b(?:WOOD\s+)?BLOCKING\b|\bBLOCKING\s+BETWEEN\s+JOISTS\b")) return "WOODBLOCKING";

        // Ignore placement/assembly context so an identical material is consolidated
        // whether the note says 'on each side', 'at 16 in. O.C.' or 'typical'.
        s = Regex.Replace(s, @"\s+(?:AT|@)\s*[\d\s/\.\x22'-]+\s*O\s*\.?\s*C\s*\.?\b.*$", "");
        s = Regex.Replace(s, @"\s+(?:ON|OVER)\s+(?:EA\.?\s+SIDE|EACH\s+SIDE|RIGID|RESILIENT|\d|WOOD\s+STUD|CONCRETE|CMU).*?$", "");
        s = Regex.Replace(s, @"\b(?:TYPICAL|TYP\.?|U\.?N\.?O\.?)\b.*$", "");

        // Normalise common abbreviations before type-specific identities are calculated.
        s = Regex.Replace(s, @"\bF\s*\.?\s*R\s*\.?\b|\bFIRE[\s-]*RESISTANT\b", "FIRERESISTANT");
        s = Regex.Replace(s, @"\bMOISTURE[\s-]*RESISTANT\b", "MOISTURERESISTANT");
        s = Regex.Replace(s, @"\bGYP(?:SUM)?\s*\.?\s*BD\s*\.?\b", "GYPSUM BOARD");
        s = Regex.Replace(s, @"\bWD\s*\.?\b", "WOOD");
        s = Regex.Replace(s, @"\bT\s*&\s*G\b|\bTONGUE(?:[\s-]+AND)?[\s-]+GROOVE\b", "TONGUE GROOVE");
        s = Regex.Replace(s, @"\bCMU\b", "CONCRETE MASONRY UNIT");
        s = Regex.Replace(s, @"\bSOUND\s+BATT(?:\s+INSULATION)?\b", "SOUND BATT INSULATION");
        s = Regex.Replace(s, @"\bRESILIENT\s+CHANNELS?\b", "RESILIENT CHANNEL");
        s = Regex.Replace(s, @"\bJOISTS?\b", "JOIST");
        s = Regex.Replace(s, @"\bSTUDS?\b", "STUD");
        s = Regex.Replace(s, @"\bTRUSSES?\b", "TRUSS");
        s = Regex.Replace(s, @"\bRAFTERS?\b", "RAFTER");
        s = Regex.Replace(s, @"\bTREADS?\b", "TREAD");

        // Additional domain aliases: one canonical line for the same construction item.
        s = Regex.Replace(s, @"\bBUILDING\s+WRAP\b|\bHOUSE\s*WRAP\b|\bHOUSEWRAP\b", "BUILDING WRAP");
        s = Regex.Replace(s, @"\bFIRE\s+BLOCKING\b", "FIRE BLOCKING");
        s = Regex.Replace(s, @"\b(?:WOOD\s+)?BLOCKING\b", "WOOD BLOCKING");
        s = Regex.Replace(s, @"\b(?:HARDI|HARDIE|VINYL)\s+SOFFIT\b|\bSOFFIT\s+PANEL\b", "SOFFIT PANEL");

        // Finish-schedule abbreviations are equivalent to their written descriptions.
        s = Regex.Replace(s, @"\b(?:LVT(?:\s*[-–]\s*\d+)?|LAMINATE\s+VINYL\s+TILE)\b", "LAMINATE VINYL TILE");
        s = Regex.Replace(s, @"\b(?:LVP|LAMINATE\s+VINYL\s+PLANK)\b", "LAMINATE VINYL PLANK");
        s = Regex.Replace(s, @"\b(?:VCT|VINYL\s+COMPOSITION\s+TILE)\b", "VINYL COMPOSITION TILE");
        s = Regex.Replace(s, @"\b(?:PLAM|PLASTIC\s+LAMINATE)\b", "PLASTIC LAMINATE");
        s = Regex.Replace(s, @"\b(?:FRP|FIBERGLASS\s+REINFORCED\s+PANEL)\b", "FIBERGLASS REINFORCED PANEL");
        s = Regex.Replace(s, @"\b(?:ACT|ACOUSTICAL\s+CEILING\s+TILE)\b", "ACOUSTICAL CEILING TILE");
        s = Regex.Replace(s, @"\b(?:GWT|GYPSUM\s+WALL\s+TEXTURE)\b", "GYPSUM WALL TEXTURE");
        s = Regex.Replace(s, @"\b(?:LSC|LEXAN\s+SUSPENDED\s+CEILING)\b", "LEXAN SUSPENDED CEILING");
        s = Regex.Replace(s, @"\b(?:WB(?:\s*[-–]\s*\d+)?|WALL\s+BASE)\b", "WALL BASE");
        s = Regex.Replace(s, @"\b(?:GB|GYPSUM\s+BOARD)\b", "GYPSUM BOARD");
        s = Regex.Replace(s, @"\bSLD\.?\s*CONC\.?\b", "SEALED CONCRETE");
        s = Regex.Replace(s, @"\bPTD\.?\s*CONC\.?\b", "PAINTED CONCRETE");
        s = Regex.Replace(s, @"\bPNT\b", "PAINT");
        s = Regex.Replace(s, @"\bCONC\.?\b", "CONCRETE");
        s = Regex.Replace(s, @"\bHOUSE\s*WRAP\b|\bHOUSEWRAP\b", "BUILDING WRAP");
        s = Regex.Replace(s, @"\bSHOWER\s*/\s*TUB\s+UNIT\b|\bSHOWER\s+TUB\s+UNIT\b", "SHOWER TUB UNIT");

        // Gypsum board: preserve thickness, fire/moisture rating and board type.
        if (s.Contains("GYPSUM BOARD", StringComparison.Ordinal))
        {
            var thickness = Regex.Match(s, @"(?<t>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*(?:INCH(?:ES)?\s*)?[\x22]?");
            var thk = thickness.Success ? Regex.Replace(thickness.Groups["t"].Value, @"\s+", "") : "";
            var rating = s.Contains("FIRERESISTANT", StringComparison.Ordinal) ? "FIRE" :
                         s.Contains("MOISTURERESISTANT", StringComparison.Ordinal) ? "MOISTURE" :
                         s.Contains("TYPE X", StringComparison.Ordinal) ? "TYPEX" :
                         s.Contains("TYPE C", StringComparison.Ordinal) ? "TYPEC" : "STANDARD";
            return $"GYPSUMBOARD|{thk}|{rating}";
        }

        // Convert nominal 2x4 / 2x6 stud notation to actual 3-1/2 / 5-1/2 in. dimensions
        // so architect's nominal and actual-size callouts consolidate to one item.
        if (s.Contains("STUD", StringComparison.Ordinal))
        {
            string size = "";
            var nominal = Regex.Match(s, @"(?<a>\d+)\s*[X]\s*(?<b>\d+(?:\s+\d+/\d+)?)");
            if (nominal.Success)
            {
                var b = nominal.Groups["b"].Value.Replace(" ", "");
                size = b switch
                {
                    "2" => "1.5",
                    "3" => "2.5",
                    "4" => "3.5",
                    "6" => "5.5",
                    "8" => "7.25",
                    "10" => "9.25",
                    "12" => "11.25",
                    _ => NormalizeDimension(b)
                };
            }
            else
            {
                var actual = Regex.Match(s, @"(?<d>\d+\s+\d+/\d+|\d+(?:\.\d+)?)(?:\s*INCH(?:ES)?)?\s*[\x22]?");
                if (actual.Success) size = NormalizeDimension(actual.Groups["d"].Value);
            }

            string[] attributes = { "SYP", "SPF", "DFL", "CEDAR", "PT", "FRT", "#1", "#2" };
            var attrs = attributes.Where(a => s.Contains(a, StringComparison.Ordinal));
            return "WOODSTUD|" + size + "|" + string.Join("|", attrs);
        }

        var tji = Regex.Match(s, @"(?<d>\d+(?:\.\d+)?)\s*[\x22]?\s*TJI\s+FLOOR\s+TRUSS");
        if (tji.Success) return "TJIFLOORTRUSS|" + tji.Groups["d"].Value;

        var joist = Regex.Match(s, @"(?<size>\d+\s*[X]\s*\d+(?:\s+\d+/\d+)?)\s*(?:WOOD\s*)?JOIST");
        if (joist.Success) return "WOODJOIST|" + Regex.Replace(joist.Groups["size"].Value, @"\s+", "").Replace("X", "X");

        if (s.Contains("TONGUE GROOVE", StringComparison.Ordinal) && s.Contains("PLYWOOD", StringComparison.Ordinal))
        {
            var thk = Regex.Match(s, @"(?<t>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22]?");
            return "T&GPLYWOOD|" + (thk.Success ? Regex.Replace(thk.Groups["t"].Value, @"\s+", "") : "");
        }
        if (s.Contains("GYPCRETE", StringComparison.Ordinal))
        {
            var thk = Regex.Match(s, @"(?<t>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22]?");
            return "GYPCRETE|" + (thk.Success ? Regex.Replace(thk.Groups["t"].Value, @"\s+", "") : "");
        }
        if (s.Contains("SOUND BATT INSULATION", StringComparison.Ordinal))
        {
            var thk = Regex.Match(s, @"(?<t>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22]?");
            return "SOUNDBATT|" + (thk.Success ? Regex.Replace(thk.Groups["t"].Value, @"\s+", "") : "");
        }
        if (s.Contains("RESILIENT CHANNEL", StringComparison.Ordinal))
        {
            var thk = Regex.Match(s, @"(?<t>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22]?");
            return "RESILIENTCHANNEL|" + (thk.Success ? Regex.Replace(thk.Groups["t"].Value, @"\s+", "") : "") +
                   (s.Contains("RIGID", StringComparison.Ordinal) ? "|RIGID" : "");
        }

        // General case: preserve all numbers, sizes, codes and product modifiers.
        return Regex.Replace(s, @"[^A-Z0-9/#]+", "");
    }

    private static string NormalizeDimension(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
        var mixed = Regex.Match(cleaned, @"^(?<whole>\d+)\s+(?<num>\d+)/(?<den>\d+)$");
        if (mixed.Success && decimal.TryParse(mixed.Groups["whole"].Value, out var whole) &&
            decimal.TryParse(mixed.Groups["num"].Value, out var numerator) &&
            decimal.TryParse(mixed.Groups["den"].Value, out var denominator) && denominator != 0)
            return (whole + numerator / denominator).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var fraction = Regex.Match(cleaned, @"^(?<num>\d+)/(?<den>\d+)$");
        if (fraction.Success && decimal.TryParse(fraction.Groups["num"].Value, out var n) &&
            decimal.TryParse(fraction.Groups["den"].Value, out var d) && d != 0)
            return (n / d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : cleaned.Replace(" ", "");
    }

    public static string EvidenceKey(string? value) =>
        Regex.Replace((value ?? "").ToUpperInvariant(), @"[^A-Z0-9/]+", "");
}
