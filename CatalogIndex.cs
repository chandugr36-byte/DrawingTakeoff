using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DrawingTakeoff;

public static class TextUtil
{
    /// <summary>Upper-case, keep only letters/digits and '/', map '×' to 'X'. Single allocation via string.Create.</summary>
    public static string Compress(string s)
    {
        int n = 0;
        foreach (var c in s) if (char.IsAsciiLetterOrDigit(c) || c == '/' || c == '×') n++;
        return string.Create(n, s, static (span, src) =>
        {
            int k = 0;
            foreach (var c in src)
            {
                if (c == '×') span[k++] = 'X';
                else if (char.IsAsciiLetterOrDigit(c) || c == '/') span[k++] = char.ToUpperInvariant(c);
            }
        });
    }
}

public sealed record CandidateSet(List<string> Names, HashSet<string> Probes);

/// <summary>
/// Builds advisory catalog hints from size/code/keyword matches. A miss is not a reason
/// to skip the page: the AI performs open-world material discovery from the full page text.
/// </summary>
public sealed class CatalogIndex
{
    private sealed class Entry
    {
        public string Name = "";
        public bool IsSheet;
        public string? Thickness;   // sheet goods: "5/8"
        public string? Code;        // steel studs: "362S20043"
        public string? SizeKey;     // lumber / LVL / LSL / PSL / glulam: "2X10", "13/4X117/8"
        public string[] Words = Array.Empty<string>(); // everything else: 6-letter word stems
    }

    private static readonly string[] SheetWords = { "PLY", "OSB", "CDX", "SHEATH", "SUBFLOOR", "STRUCT" };

    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["SHINGL"] = new[] { "ASPHALT" },
        ["UNDERL"] = new[] { "FELT" },
        ["HOUSEW"] = new[] { "WEATHERBARRIER", "TYVEK", "WRAP" },
        ["SEALER"] = new[] { "SILLSEAL", "GASKET" },
        ["ADHESI"] = new[] { "GLUE" },
    };

    private readonly List<Entry> _entries = new();

    public CatalogIndex(IEnumerable<string> items)
    {
        foreach (var name in items)
        {
            var e = new Entry { Name = name };
            var m = Rx.SheetGoods().Match(name);
            if (m.Success) { e.IsSheet = true; e.Thickness = TextUtil.Compress(m.Groups["t"].Value); }
            else if ((m = Rx.SteelCode().Match(name)).Success) e.Code = TextUtil.Compress(m.Groups["c"].Value);
            else if ((m = Rx.SizePair().Match(name)).Success)
                e.SizeKey = TextUtil.Compress(m.Groups["a"].Value + "X" + m.Groups["b"].Value);
            else e.Words = WordKeys(name);
            _entries.Add(e);
        }
    }

    private static string[] WordKeys(string name)
    {
        var keys = new HashSet<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 5)
            {
                var w = sb.ToString().ToUpperInvariant();
                var k = w.Length > 6 ? w[..6] : w;
                keys.Add(k);
                if (Aliases.TryGetValue(k, out var extra)) foreach (var x in extra) keys.Add(x);
            }
            sb.Clear();
        }
        foreach (var c in name) { if (char.IsLetter(c)) sb.Append(c); else Flush(); }
        Flush();
        return keys.ToArray();
    }

    public CandidateSet Select(string pageText)
    {
        var c = TextUtil.Compress(pageText);
        bool sheetWord = SheetWords.Any(w => c.Contains(w, StringComparison.Ordinal));
        var names = new List<string>();
        var probes = new HashSet<string>();

        foreach (var e in _entries)
        {
            bool hit = false;
            if (e.IsSheet)
            {
                if (sheetWord && c.Contains(e.Thickness!, StringComparison.Ordinal)) { hit = true; probes.Add(e.Thickness!); }
            }
            else if (e.Code is not null)
            {
                if (c.Contains(e.Code, StringComparison.Ordinal)) { hit = true; probes.Add(e.Code); }
            }
            else if (e.SizeKey is not null)
            {
                if (c.Contains(e.SizeKey, StringComparison.Ordinal)) { hit = true; probes.Add(e.SizeKey); }
            }
            else
            {
                foreach (var w in e.Words)
                    if (c.Contains(w, StringComparison.Ordinal)) { hit = true; probes.Add(w); }
            }
            if (hit) names.Add(e.Name);
        }
        return new CandidateSet(names, probes);
    }
}

/// <summary>Builds bounded prompt chunks. BuildAll preserves the complete page text.</summary>
public static class TextWindows
{
    private static readonly string[] FastMaterialTerms =
    {
        "MATERIAL", "SCHEDULE", "SPECIFICATION", "TYPICAL", "PARTITION", "ASSEMBLY", "FRAMING",
        "GYP", "DRYWALL", "STUD", "JOIST", "TRUSS", "RAFTER", "BEAM", "POST", "HEADER",
        "PLYWOOD", "OSB", "SHEATH", "INSULATION", "BATT", "RESILIENT", "CHANNEL", "GYPCRETE",
        "CONCRETE", "CMU", "MASONRY", "BLOCKING", "SEALANT", "ROOF", "SHINGLE", "WRAP",
        "LUMBER", "LVL", "LSL", "PSL", "GLULAM", "DECK", "SOFFIT", "FASCIA", "FIRE", "RATED",
        "HARDWARE", "SIMPSON", "STRONG-TIE", "HANGER", "HOLDOWN", "ANCHOR", "BOLT", "SCREW",
        "NAIL", "NUT", "WASHER", "STRAP", "TIE", "CLIP", "BRACKET", "CONNECTOR", "THD", "SDS",
        "SDW", "HUC", "LUS", "LSTA", "MSTA", "GATR", "HTT", "DTT", "PHD", "H3", "LU26", "A23", "TC26"
    };
    /// <summary>
    /// Split the complete extracted page text into bounded chunks. This is deliberately
    /// independent of catalog hits: catalog retrieval may suggest equivalents, but it
    /// must never decide whether a drawing page is analysed.
    /// </summary>
    /// <summary>
    /// Creates one compact page excerpt for fast single-pass inference. It normalises
    /// excessive CAD whitespace and prioritises material/schedule/callout lines when a
    /// page is too large for one model context. The full original text is still scanned
    /// by deterministic extraction before this method is called.
    /// </summary>
    public static string BuildSinglePass(string text, int maxChars, out int originalChars, out int keptChars)
    {
        text ??= string.Empty;
        originalChars = text.Length;
        maxChars = Math.Max(1200, maxChars);

        var lines = Rx.Newline().Split(text)
            .Select(line => Regex.Replace(line.Trim(), @"\s{2,}", " "))
            .Where(line => line.Length > 0)
            .ToList();
        var compact = string.Join("\n", lines);
        originalChars = compact.Length;
        if (compact.Length <= maxChars)
        {
            keptChars = compact.Length;
            return compact;
        }

        static int Score(string line)
        {
            var upper = line.ToUpperInvariant();
            int score = 0;
            if (FastMaterialTerms.Any(t => upper.Contains(t, StringComparison.Ordinal))) score += 5;
            if (Regex.IsMatch(upper, @"\b\d+\s*[X×/]\s*\d+\b|\b\d+\s*(?:IN|INCH(?:ES)?)\b")) score += 3;
            if (upper.Any(char.IsDigit) && upper.Any(char.IsLetter)) score += 1;
            if (line.Length <= 180) score += 1;
            return score;
        }

        // Preserve title/header and title-block tail, then add the strongest callout lines.
        var selected = new HashSet<int>();
        int selectedChars = 0;
        void AddSelected(int index)
        {
            if (selected.Add(index)) selectedChars += lines[index].Length + 1;
        }
        for (int i = 0; i < Math.Min(10, lines.Count); i++) AddSelected(i);
        for (int i = Math.Max(0, lines.Count - 18); i < lines.Count; i++) AddSelected(i);
        var ranked = lines.Select((line, index) => (line, index, score: Score(line)))
            .OrderByDescending(x => x.score).ThenBy(x => x.index);
        foreach (var item in ranked)
        {
            if (item.score <= 0) break;
            AddSelected(item.index);
            if (selectedChars >= maxChars) break;
        }

        // Keep the selected lines in original order. If a few long lines push us over the
        // budget, trim only the final selected line; do not allocate another full-page copy.
        var sb = new StringBuilder(maxChars + 32);
        foreach (int i in selected.OrderBy(i => i))
        {
            var line = lines[i];
            int remaining = maxChars - sb.Length;
            if (remaining <= 1) break;
            if (line.Length + 1 > remaining)
            {
                sb.AppendLine(line[..Math.Max(0, remaining - 1)]);
                break;
            }
            sb.AppendLine(line);
        }
        keptChars = Math.Min(compact.Length, sb.Length);
        return sb.ToString();
    }

    /// <summary>Split complete text into bounded chunks for the legacy multi-pass path.</summary>
    public static List<string> BuildAll(string text, int maxChars, out int keptChars)
    {
        var segments = SplitSegments(text ?? string.Empty, 300);
        var chunks = new List<string>();
        var sb = new StringBuilder(Math.Max(512, maxChars));
        string? previousSegment = null;
        keptChars = 0;

        foreach (var segment in segments)
        {
            if (sb.Length > 0 && sb.Length + segment.Length + 1 > maxChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
                // Carry the previous line into the next chunk so a schedule heading,
                // member callout or quantity column label is less likely to be separated
                // from the value it describes. The small overlap is reserved for in the
                // context-size calculation and is deduplicated during result merging.
                if (!string.IsNullOrWhiteSpace(previousSegment)) sb.AppendLine(previousSegment);
            }

            sb.AppendLine(segment);
            previousSegment = segment;
            keptChars += segment.Length;
        }

        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    public static List<string> Build(string text, HashSet<string> probes, int maxChars, int maxChunks,
                                     out int keptChars, out bool truncated)
    {
        var segs = SplitSegments(text, 300);
        var keep = new bool[segs.Count];

        for (int i = 0; i < segs.Count; i++)
        {
            var c = TextUtil.Compress(segs[i]);
            bool hit = false;
            foreach (var p in probes) { if (c.Contains(p, StringComparison.Ordinal)) { hit = true; break; } }
            if (!hit) continue;
            for (int j = Math.Max(0, i - 1); j <= Math.Min(segs.Count - 1, i + 1); j++) keep[j] = true;
        }

        // always keep the tail: the title block is usually last in content order
        int tail = 0;
        for (int i = segs.Count - 1; i >= 0 && tail < 500; i--) { keep[i] = true; tail += segs[i].Length; }

        var chunks = new List<string>();
        var sb = new StringBuilder();
        keptChars = 0; truncated = false;
        for (int i = 0; i < segs.Count; i++)
        {
            if (!keep[i]) continue;
            var s = segs[i];
            if (sb.Length > 0 && sb.Length + s.Length + 1 > maxChars)
            {
                chunks.Add(sb.ToString()); sb.Clear();
                if (chunks.Count >= maxChunks) { truncated = true; break; }
            }
            sb.AppendLine(s);
            keptChars += s.Length;
        }
        if (!truncated && sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    private static List<string> SplitSegments(string text, int max)
    {
        var list = new List<string>();
        foreach (var raw in Rx.Newline().Split(text))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            while (l.Length > max)
            {
                int cut = l.LastIndexOf(' ', max - 1);
                if (cut < max / 2) cut = max;
                list.Add(l[..cut].Trim());
                l = l[cut..].TrimStart();
            }
            if (l.Length > 0) list.Add(l);
        }
        return list;
    }
}

/// <summary>Deterministic (no AI) sheet number / title detection from the title block at the end of the page text.</summary>
public static class SheetInfoExtractor
{
    public static (string Number, string Name) Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        var tail = text.Length > 3000 ? text[^3000..] : text;
        var lines = Rx.Newline().Split(tail);
        string number = "", name = "";
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.Length > 60) continue;
            if (number.Length == 0)
            {
                var m = Rx.SheetNumberLine().Match(l);
                if (m.Success && m.Groups["n"].Value.Any(char.IsDigit))
                    number = m.Groups["n"].Value.ToUpperInvariant().Replace(' ', '-');
            }
            if (name.Length == 0 && l.Length >= 6 && l == l.ToUpperInvariant() && l.Any(char.IsLetter)
                && Rx.SheetTitleWord().IsMatch(l))
                name = l;
            if (number.Length > 0 && name.Length > 0) break;
        }
        return (number, name);
    }
}
