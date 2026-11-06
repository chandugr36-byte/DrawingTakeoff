using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DrawingTakeoff;

/// <summary>Separate reference catalogs for lumber and hardware; neither is a discovery whitelist.</summary>
public sealed class CatalogService
{
    public const string LumberCategory = "Lumber";
    public const string HardwareCategory = "Hardware";
    public const string OtherCategory = "Other";

    private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lumberByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hardwareByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _numericBuckets = new(StringComparer.Ordinal);
    // Precomputed search keys avoid rebuilding thousands of normalized catalog strings for every PDF page.
    private readonly List<(string Key, string Item)> _lumberSearchKeys = new();
    private readonly List<(string Key, string Item)> _hardwareSearchKeys = new();
    private readonly Dictionary<string, string> _hardwareCodes = new(StringComparer.Ordinal);

    public List<string> Items { get; } = new();
    public List<string> LumberItems { get; } = new();
    public List<string> HardwareItems { get; } = new();

    /// <summary>Legacy loader: items are loaded as lumber/reference items.</summary>
    public void Load(string path) => Load(path, null);

    public void Load(string lumberPath, string? hardwarePath)
    {
        Items.Clear();
        LumberItems.Clear();
        HardwareItems.Clear();
        _byKey.Clear();
        _lumberByKey.Clear();
        _hardwareByKey.Clear();
        _numericBuckets.Clear();
        _lumberSearchKeys.Clear();
        _hardwareSearchKeys.Clear();
        _hardwareCodes.Clear();

        LoadOne(lumberPath, _lumberByKey, LumberItems);
        if (!string.IsNullOrWhiteSpace(hardwarePath) && File.Exists(hardwarePath))
            LoadOne(hardwarePath, _hardwareByKey, HardwareItems);
        BuildSearchIndexes();

        foreach (var item in LumberItems.Concat(HardwareItems))
        {
            var key = Key(item);
            if (_byKey.TryAdd(key, item)) Items.Add(item);
            var numericKey = Numbers(item);
            if (numericKey.Length == 0) continue;
            if (!_numericBuckets.TryGetValue(numericKey, out var bucket))
                _numericBuckets[numericKey] = bucket = new List<string>();
            if (!bucket.Contains(item, StringComparer.Ordinal)) bucket.Add(item);
        }
    }

    private static void LoadOne(string path, Dictionary<string, string> target, List<string> items)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Reference catalog file was not found.", path);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("# Source:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            // The source workbook contains a standalone 0 row; it is a placeholder, not a product.
            if (line == "0") continue;
            var key = Key(line);
            if (key.Length > 0 && target.TryAdd(key, line)) items.Add(line);
        }
    }

    /// <summary>Normalised comparison key: upper-case, only letters/digits and '/', '-'.</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToUpperInvariant().Replace('×', 'X'))
            if (char.IsLetterOrDigit(c) || c == '/' || c == '-') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Exact match first. Fuzzy matching is constrained to an identical numeric signature.</summary>
    public string? Snap(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var key = Key(candidate);
        if (_byKey.TryGetValue(key, out var exact)) return exact;
        var numericKey = Numbers(candidate);
        if (numericKey.Length == 0 || !_numericBuckets.TryGetValue(numericKey, out var candidates)) return null;

        string? best = null;
        double bestScore = 0;
        foreach (var item in candidates)
        {
            var score = Similarity(key, Key(item));
            if (score > bestScore) { bestScore = score; best = item; }
        }
        return bestScore >= 0.88 ? best : null;
    }

    /// <summary>
    /// Finds exact standalone hardware part-number tokens from the hardware catalog.
    /// The ordinary word index intentionally ignores very short tokens; this exact matcher
    /// recovers codes such as H3, LU26, A23, TC26, THD50600H, HUCQ210-2 and LUS210.
    /// This is an additional evidence pass, not a filter for AI discovery.
    /// </summary>
    internal List<RuleBasedMaterial> FindHardwarePartCodeMatches(string text)
    {
        var results = new List<RuleBasedMaterial>();
        if (string.IsNullOrWhiteSpace(text) || HardwareItems.Count == 0) return results;

        if (_hardwareCodes.Count == 0) return results;

        var found = new Dictionary<string, (string Item, int Count, List<string> Evidence)>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"(?<![A-Z0-9])(?<code>[A-Z][A-Z0-9./-]{1,31})(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var token = Key(match.Groups["code"].Value);
            if (!_hardwareCodes.TryGetValue(token, out var item)) continue;
            var key = Key(item);
            if (!found.TryGetValue(key, out var current)) current = (item, 0, new List<string>());
            current.Count++;
            int start = Math.Max(0, match.Index - 55);
            int end = Math.Min(text.Length, match.Index + match.Length + 75);
            var evidence = Regex.Replace(text[start..end], @"\s+", " ").Trim();
            if (evidence.Length > 180) evidence = evidence[..180];
            if (evidence.Length > 0 && !current.Evidence.Contains(evidence, StringComparer.OrdinalIgnoreCase) && current.Evidence.Count < 8)
                current.Evidence.Add(evidence);
            found[key] = current;
        }

        foreach (var item in found.Values)
            results.Add(new RuleBasedMaterial(item.Item, Math.Max(1, item.Count), string.Join(" || ", item.Evidence)));
        return results;
    }

    /// <summary>
    /// Finds catalog descriptions that are explicitly present in extracted PDF text.
    /// Normalization removes CAD spacing/quotation differences; minimum key length is used
    /// to avoid treating generic short words as catalog matches.
    /// </summary>
    internal List<RuleBasedMaterial> FindExactCatalogTextMatches(string text, string category, int maxMatches = 80)
    {
        var results = new List<RuleBasedMaterial>();
        if (string.IsNullOrWhiteSpace(text)) return results;
        var searchKeys = category.Equals(LumberCategory, StringComparison.OrdinalIgnoreCase)
            ? _lumberSearchKeys : category.Equals(HardwareCategory, StringComparison.OrdinalIgnoreCase)
                ? _hardwareSearchKeys : null;
        if (searchKeys is null || searchKeys.Count == 0) return results;

        var pageKey = Key(text);
        foreach (var entry in searchKeys)
        {
            // A short prefix check avoids a full substring scan for most catalog rows.
            // Full matching is still required before a result is emitted.
            if (entry.Key.Length > 8 && pageKey.IndexOf(entry.Key[..8], StringComparison.Ordinal) < 0)
                continue;
            int count = CountOccurrences(pageKey, entry.Key);
            if (count < 1) continue;
            results.Add(new RuleBasedMaterial(entry.Item, count,
                $"Normalized full catalog description matched page text: {entry.Item}"));
            if (results.Count >= maxMatches) break;
        }
        return results;
    }

    private void BuildSearchIndexes()
    {
        foreach (var item in LumberItems)
        {
            var key = Key(item);
            if (key.Length >= 8) _lumberSearchKeys.Add((key, item));
        }

        foreach (var item in HardwareItems)
        {
            var key = Key(item);
            if (key.Length >= 8) _hardwareSearchKeys.Add((key, item));

            // Keep manufacturer part codes in a dedicated exact lookup. Some catalog
            // entries include a separated size after the code (for example "PSCL 1/2").
            // Store the leading identifier too; this index is built once at catalog load.
            var codeMatch = Regex.Match(item.Trim(), @"^(?<code>[A-Z][A-Z0-9./-]{1,31})(?:\s+|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (codeMatch.Success)
            {
                var code = Key(codeMatch.Groups["code"].Value);
                bool hasDigit = code.Any(char.IsDigit);
                bool knownAlphaCode = code is "PSCL" or "PAF" or "DTC" or "TSP" or "MASAZ";
                if (code.Length >= 2 && code.Length <= 32 && (hasDigit || knownAlphaCode))
                    _hardwareCodes.TryAdd(code, item);
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        if (text.Length == 0 || value.Length == 0) return 0;
        int count = 0, offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    /// <summary>
    /// Classifies a discovered item using catalog membership, specific hardware evidence,
    /// lumber evidence and the model hint. Hardware signals are evaluated before broad
    /// wood/dimensional words so a connector described beside timber is not sent to Lumber.
    /// Lumber and hardware descriptions are resolved semantically before trusting catalog placement.
    /// </summary>
    public string Classify(string? item, string? modelCategory = null)
    {
        if (string.IsNullOrWhiteSpace(item)) return OtherCategory;

        var value = Regex.Replace(item.Trim(), @"^\[NOT IN CATALOG\]\s*", "", RegexOptions.IgnoreCase);
        var key = Key(value);
        bool inLumber = _lumberByKey.ContainsKey(key);
        bool inHardware = _hardwareByKey.ContainsKey(key);
        var hint = (modelCategory ?? string.Empty).Trim();
        bool hintHardware = hint.Equals(HardwareCategory, StringComparison.OrdinalIgnoreCase);
        bool hintLumber = hint.Equals(LumberCategory, StringComparison.OrdinalIgnoreCase);

        // The supplied workbook has hardware products accidentally placed in its Lumber
        // sheet (for example Simpson connectors, anchor bolts, straps, nails and screws).
        // Therefore semantic hardware evidence MUST take precedence over sheet membership.
        // This also keeps a connector categorized as Hardware when its note mentions wood.
        if (LooksLikeHardware(value) || ContainsKnownHardwareCode(value)) return HardwareCategory;

        // Descriptive wood-member evidence comes before catalog-sheet placement. The
        // workbook also contains genuine lumber products in the Hardware sheet (for
        // example LVL entries), so sheet membership is not a reliable category by itself.
        bool strongLumber = LooksLikeLumber(value);
        if (strongLumber) return LumberCategory;

        // Catalog membership resolves remaining unambiguous items only after both
        // semantic hardware and semantic lumber evidence have been evaluated.
        if (inHardware && !inLumber) return HardwareCategory;
        if (inLumber && !inHardware) return LumberCategory;

        // Model category is useful for less-specific descriptions such as a bracket code
        // or a generic product family, but it cannot override explicit lumber dimensions.
        if (hintHardware) return HardwareCategory;
        if (hintLumber) return LumberCategory;
        if (inHardware) return HardwareCategory;
        if (inLumber) return LumberCategory;
        return OtherCategory;
    }

    private bool ContainsKnownHardwareCode(string value)
    {
        if (_hardwareCodes.Count == 0 || string.IsNullOrWhiteSpace(value)) return false;
        foreach (Match match in Regex.Matches(value,
                     @"(?<![A-Z0-9])(?<code>[A-Z][A-Z0-9./-]{1,31})(?![A-Z0-9])",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var code = Key(match.Groups["code"].Value);
            if (_hardwareCodes.ContainsKey(code)) return true;
        }
        return false;
    }

    private static bool LooksLikeHardware(string value)
    {
        var s = value.ToUpperInvariant();
        // Phrase/token boundaries are intentional: e.g. a wood post is lumber, while a
        // post base/post cap is a connector. Avoid broad substring matches like "POST".
        const string hardwareTerms = @"\b(?:SIMPSON(?:\s+STRONG[- ]?TIE)?|STRONG[- ]?TIE|JOIST\s+HANGERS?|HANGERS?|HOLD[ -]?DOWNS?|ANCHORS?|ANCHOR\s+BOLTS?|HEADED\s+STUDS?|ANCHOR\s+STUDS?|WELDED\s+STUDS?|THRU\s+BOLTS?|THROUGH\s+BOLTS?|CARRIAGE\s+BOLTS?|LAG\s+SCREWS?|LAG\s+BOLTS?|BOLTS?|SCREWS?|NAILS?|NAIL\s+PLATES?|HEX\s+NUTS?|NUTS?|WASHERS?|THREADED\s+RODS?|STRAPS?|HURRICANE\s+TIES?|TIE[ -]?DOWNS?|STRAP\s+TIES?|CLIPS?|BRACKETS?|CONNECTORS?|FASTENERS?|POST\s+BASES?|POST\s+CAPS?|POST\s+ANCHORS?|SHEAR\s+PLATES?|EMBED(?:DED)?\s+PLATES?|METAL\s+CONNECTORS?|STRUCTURAL\s+CONNECTORS?|EXPANSION\s+ANCHORS?|BEAM\s+SEATS?|HDG|SS304)\b";
        if (Regex.IsMatch(s, hardwareTerms, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;

        // Common connector families. The catalog-driven exact code lookup below is the
        // primary check for all hardware SKUs; these are fallback families for uncatalogued SKUs.
        return Regex.IsMatch(s,
            @"\b(?:H[1-9](?:\.\d+[A-Z]?)?|A\d{1,3}[A-Z]?|TC\d{1,3}[A-Z]?|LUS\w*|LU\d+\w*|HUC\w*|CCQ\w*|CCOQ\w*|HTT\w*|DTT\w*|PHD\w*|MST\w*|MSTA\w*|LSTA\w*|CMST\w*|GATR\w*|THD\w*|SDS\w*|SDW\w*|SDWH\w*|HDUE\w*|HGA\w*|LTP\w*|STB\w*|BP\w*|ABU\w*|ABA\w*|ABW\w*|CBT\w*|CS\d{2,}|PT-?\d+|PSCL\w*|PAF|DTC|TSP|MASAZ)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeLumber(string value)
    {
        var s = value.ToUpperInvariant();
        string[] terms =
        {
            "LUMBER", "WOOD", "WOOD POST", "TIMBER POST", "STUD", "JOIST", "TRUSS", "TJI",
            "RAFTER", "BEAM", "HEADER", "TIMBER", "LVL", "LSL", "PSL", "GLULAM", "PLYWOOD",
            "OSB", "CDX", "SHEATHING", "DECKING", "WOOD DECK", "FASCIA", "SOFFIT", "SIDING",
            "BLOCKING", "T&G", "TONGUE AND GROOVE", "TONGUE & GROOVE"
        };
        if (terms.Any(t => s.Contains(t, StringComparison.Ordinal))) return true;
        // Dimensional members/sheet goods are lumber only when no connector signal matched.
        return Regex.IsMatch(s, @"\b\d+\s*[X×]\s*\d+(?:\s+\d+/\d+)?\b", RegexOptions.IgnoreCase);
    }

    private static string Numbers(string s)
    {
        var parts = new List<string>();
        foreach (Match match in Regex.Matches(s, @"\d+(?:/\d+)?")) parts.Add(match.Value);
        return string.Join("|", parts);
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0 || Math.Abs(n - m) > Math.Max(n, m) * 0.12) return 0;
        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (int j = 0; j <= m; j++) previous[j] = j;
        for (int i = 1; i <= n; i++)
        {
            current[0] = i;
            for (int j = 1; j <= m; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[m] / Math.Max(n, m);
    }
}
