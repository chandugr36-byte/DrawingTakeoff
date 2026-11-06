using System.Text.RegularExpressions;

namespace DrawingTakeoff;

/// <summary>Compile-time generated regexes (no runtime construction / interpretation cost).</summary>
internal static partial class Rx
{
    [GeneratedRegex(@"[A-Z0-9/]+")]
    public static partial Regex Token();

    [GeneratedRegex(@"\r?\n")]
    public static partial Regex Newline();

    [GeneratedRegex(@"^\s*(?:SHEET\s*(?:NO\.?|NUMBER|#)?\s*[:\-]?\s*)?(?<n>[A-Z]{1,3}[-. ]?\d{1,3}(?:\.\d{1,2})?[A-Z]?)\s*$", RegexOptions.IgnoreCase)]
    public static partial Regex SheetNumberLine();

    [GeneratedRegex(@"\b(PLAN|PLANS|ELEVATION|ELEVATIONS|SECTION|SECTIONS|DETAIL|DETAILS|SCHEDULE|NOTES|FRAMING|FOUNDATION|ROOF|DIAGRAM)\b", RegexOptions.IgnoreCase)]
    public static partial Regex SheetTitleWord();

    // catalog entry shapes
    [GeneratedRegex(@"^(?<t>\d+/\d+)X4X8")]
    public static partial Regex SheetGoods();

    [GeneratedRegex(@"^(?<c>\d{3}[ST]\d{3}-\d{2})")]
    public static partial Regex SteelCode();

    [GeneratedRegex(@"^(?<a>\d+(?: \d+/\d+)?""?)X(?<b>\d+(?: \d+/\d+)?""?)")]
    public static partial Regex SizePair();
}
