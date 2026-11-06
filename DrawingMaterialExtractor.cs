using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DrawingTakeoff;

/// <summary>
/// A deterministic material discovery hit. Occurrences counts separate callout/label
/// appearances in one sheet. It is an item-occurrence count, NOT a measured procurement quantity.
/// </summary>
internal sealed record RuleBasedMaterial(string Name, int Occurrences, string Evidence);

/// <summary>
/// Domain-specific recovery pass for architectural/structural drawing terminology.
/// This supplements open-world LLM discovery so common abbreviations, split CAD text and
/// assembly components are not lost when a small local model omits them.
/// </summary>
internal static class DrawingMaterialExtractor
{
    private sealed record Rule(Regex Pattern, Func<Match, string> Name);

    private static Regex Rx(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // IMPORTANT: keep abbreviations bounded. MaterialIdentity.Key consolidates equivalent
    // descriptions (e.g. 2x4 WD STUD and 3-1/2 in. wood stud) but preserves critical specs.
    private static readonly Rule[] Rules =
    {
        // Gypsum board: supports FIRE RESISTANT, F.R., moisture-resistant, Type X/Type C,
        // split labels and layer forms such as (2) LAYER OF 5/8 in. (layer count is not callout count).
        new(Rx("""(?:(?:\(\s*\d+\s*\)\s*(?:LAYERS?\s+(?:OF\s+)?)?)|(?:\d+(?:ST|ND|RD|TH)?\s+LAYERS?\s+(?:OF\s+)?))?(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?(?<qual>(?:(?:F\s*\.?\s*R\s*\.?|FIRE[\s-]*RESISTANT|MOISTURE[\s-]*RESISTANT|TYPE\s*[XC])\s*[.]?\s*)*)GYP(?:SUM)?\.?\s*BD\.?"""),
            m => BoardName(m.Groups["thk"].Value, m.Groups["qual"].Value)),
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?GYPSUM\s+BOARD\b"""),
            m => BoardName(m.Groups["thk"].Value, "")),
        new(Rx("""\bDOUBLE\s+GYP\b"""), _ => "Gypsum board"),

        // Floor framing and deck assembly components.
        new(Rx("""(?<depth>\d+(?:\s+\d+/\d+|\.\d+)?)\s*[\x22”]?\s*TJI\s+FLOOR\s+TRUSSES?"""),
            m => $"{Clean(m.Groups["depth"].Value)}\" TJI floor truss"),
        new(Rx("""(?<size>\d+\s*[xX]\s*\d+(?:\s+\d+/\d+)?)\s*(?:WD\.?\s*|WOOD\s*)JOISTS?"""),
            m => $"{CompactSize(m.Groups["size"].Value)} wood joist"),
        new(Rx("""(?<size>\d+\s*[xX]\s*\d+(?:\s+\d+/\d+)?)\s*(?:WD\.?\s*|WOOD\s*)RAFTERS?"""),
            m => $"{CompactSize(m.Groups["size"].Value)} wood rafter"),

        // Studs: nominal sizes, actual sizes and unspecified wood studs.
        new(Rx("""(?<size>\d+\s*[xX]\s*\d+(?:\s+\d+/\d+)?)\s*(?:WD\.?\s*|WOOD\s*)STUDS?"""),
            m => $"{CompactSize(m.Groups["size"].Value)} wood stud"),
        new(Rx("""(?<![\d/])(?<size>\d+\s+\d+/\d+\s*[\x22”]?|\d+(?:\.\d+)?\s*[\x22”])\s*(?:WD\.?\s*|WOOD\s*)STUDS?"""),
            m => $"{Clean(m.Groups["size"].Value).TrimEnd('\"', '”')}\" wood stud"),
        new(Rx("""\b(?:ADDITIONAL\s+)?WOOD\s+STUDS?\s+AS\s+REQUIRED\b|\bADDITIONAL\s+STUDS?\s+AS\s+REQUIRED\b"""),
            _ => "Wood stud (size not specified)"),

        // Sheathing and decking. Keep thickness when shown; distinguish T&G from plain panels.
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]?\s*)?(?:T\s*&\s*G|TONGUE\s+AND\s+GROOVE)\s+PLYWOOD(?:\s+DECK)?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}tongue-and-groove plywood decking"),
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?PLYWOOD\s+SHEATHING(?:\s+BOARD)?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}plywood sheathing"),
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?OSB\s+SHEATHING(?:\s+BOARD)?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}OSB sheathing"),
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?OSB\s+DECK\b"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}OSB decking"),
        new(Rx("""\bEXTERIOR\s+SHEATHING\b"""), _ => "Exterior sheathing (type per structural drawings)"),

        // Toppings and thermal/acoustic insulation.
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]?\s*)?GYPCRETE(?:\s+TOPPING)?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}gypcrete topping"),
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?SOUND\s+BATT(?:\s+INSULATION)?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}sound batt insulation"),
        new(Rx("""\bR\s*[-]?\s*(?<r>\d+)\s+BATT\s+INSULATION\b"""),
            m => $"R-{m.Groups["r"].Value} batt insulation"),
        new(Rx("""\bRIGID\s+INSULATION\b"""), _ => "Rigid insulation"),

        // Resilient channels, including text that is split into columns/lines in CAD PDFs.
        new(Rx("""(?:(?<thk>\d+(?:\s+\d+/\d+)?|\d+/\d+)\s*[\x22”]\s*)?(?<rigid>RIGID\s+)?RESILIENT\s+CHANNELS?"""),
            m => $"{(m.Groups["thk"].Success ? Clean(m.Groups["thk"].Value) + "\" " : "")}{(m.Groups["rigid"].Success ? "rigid " : "")}resilient channel"),

        // Structural hardware and connection products. Keep these separate from lumber;
        // exact catalog part-number codes are added by CatalogService.FindHardwarePartCodeMatches.
        new(Rx(@"\bJOIST\s+HANGERS?\b|\bHANGERS?\s+FOR\s+JOISTS?\b"), _ => "Joist hanger"),
        new(Rx(@"\bHOLD\s*[- ]?DOWNS?\b"), _ => "Hold-down connector"),
        new(Rx(@"\bANCHOR\s+BOLTS?\b|\bL\s*[- ]?ANCHOR\s+BOLTS?\b"), _ => "Anchor bolt"),
        new(Rx(@"\bTHRU\s+BOLTS?\b|\bTHROUGH\s+BOLTS?\b"), _ => "Through bolt"),
        new(Rx(@"\bCARRIAGE\s+BOLTS?\b"), _ => "Carriage bolt"),
        new(Rx(@"\bLAG\s+SCREWS?\b|\bLAG\s+BOLTS?\b"), _ => "Lag screw/bolt"),
        new(Rx(@"\bHEX\s+NUTS?\b"), _ => "Hex nut"),
        new(Rx(@"\b(?:ROUND|RND\.?|FLAT)\s+WASHERS?\b|\bWASHERS?\b"), _ => "Washer"),
        new(Rx(@"\bTHREADED\s+RODS?\b"), _ => "Threaded rod"),
        new(Rx(@"\b(?:METAL|STEEL)\s+STRAPS?\b|\bSTRAP\s+TIES?\b"), _ => "Metal strap/tie"),
        new(Rx(@"\bHURRICANE\s+TIES?\b"), _ => "Hurricane tie"),
        new(Rx(@"\bPOST\s+BASES?\b|\bPOST\s+ANCHORS?\b"), _ => "Post base/anchor"),
        new(Rx(@"\bPOST\s+CAPS?\b"), _ => "Post cap"),
        new(Rx(@"\bSHEAR\s+PLATES?\b"), _ => "Shear plate"),
        new(Rx(@"\bEMBED(?:DED)?\s+PLATES?\b"), _ => "Embed plate"),
        new(Rx(@"\b(?:METAL|STEEL)\s+CLIPS?\b|\bCLIPS?\b(?=\s*(?:AT|@|TO|WITH))"), _ => "Metal clip"),
        new(Rx(@"\b(?:METAL|STEEL)\s+BRACKETS?\b|\bBRACKETS?\b"), _ => "Metal bracket"),
        new(Rx(@"\b(?:SIMPSON\s+)?STRONG\s*[- ]?TIE\s+CONNECTORS?\b"), _ => "Structural connector"),
        new(Rx(@"\bFASTENERS?\b"), _ => "Fastener"),
        new(Rx(@"\bNAILS?\b"), _ => "Nail"),
        new(Rx(@"\bSCREWS?\b"), _ => "Screw"),

        // Other building envelope, framing and wall assembly materials.
        new(Rx("""\b(?:BUILDING\s+WRAP|HOUSE\s*WRAP)\b"""), _ => "Building wrap"),
        new(Rx("""\bWOOD\s+BLOCKING\b|\bBLOCKING\s+BETWEEN\s+JOISTS\b|\bCONTINUOUS\s+2\s*[xX]\s*BLOCKING\b|\b2\s*[xX]\s*BLOCKING\b"""), _ => "Wood blocking"),
        new(Rx("""\bFIRE\s+BLOCKING\b"""), _ => "Fire blocking"),
        new(Rx("""\bACOUSTICAL\s+SEALANT\b"""), _ => "Acoustical sealant"),
        new(Rx("""\b(?:STEEL|METAL)\s+HANDRAILS?\b|\bSTEEL\s+HANDRAIL\b"""), _ => "Steel handrail"),
        new(Rx("""\bHOLLOW\s+METAL\s+DOOR\s+FRAMES?\b|\bHM\s+DOOR\s+FRAMES?\b"""), _ => "Hollow metal door frame"),
        new(Rx("""\b(?:WALL\s+)?MIRRORS?\b"""), _ => "Mirror"),
        new(Rx("""\bCMU\s+BLOCK(?:\s+WALL)?\b|\bCONCRETE\s+MASONRY\s+UNIT(?:\s+BLOCK)?\b"""), _ => "Concrete masonry unit (CMU) block"),
        new(Rx("""\bSHOWER\s+SURROUND\b"""), _ => "Shower surround"),
        new(Rx("""\bSHOWER\s*/\s*TUB\s+UNIT\b|\bSHOWER\s+TUB\s+UNIT\b"""), _ => "Shower/tub unit"),
        new(Rx("""\bWALL\s+BASE\b"""), _ => "Wall base"),
        new(Rx("""\bRUBBER\s+STAIR\s+TREADS?\b"""), _ => "Rubber stair tread"),
        new(Rx("""\bWOOD\s+STAIR\s+TREADS?\b"""), _ => "Wood stair tread"),

        // Room-finish schedule materials. Abbreviations map to their written descriptions.
        // Every non-overlapping code appearance is retained as an occurrence; the result is
        // a reference count, not a procurement area or physical unit quantity.
        new(Rx("""\bACOUSTICAL\s+CEILING\s+TILE\b|\bACT\b"""), _ => "Acoustical ceiling tile (ACT)"),
        new(Rx("""\bGYPSUM\s+WALL\s+TEXTURE\b|\bGWT\b"""), _ => "Gypsum wall texture (GWT)"),
        new(Rx("""\bVINYL\s+COMPOSITION\s+TILE\b|\bVCT\b"""), _ => "Vinyl composition tile (VCT)"),
        new(Rx("""\bLAMINATE\s+VINYL\s+TILE\b|\bLVT(?:\s*[-–]\s*\d+)?\b"""), _ => "Laminate vinyl tile (LVT)"),
        new(Rx("""\bLAMINATE\s+VINYL\s+PLANK\b|\bLVP(?:\s*[-–]\s*\d+)?\b"""), _ => "Laminate vinyl plank (LVP)"),
        new(Rx("""\bPLASTIC\s+LAMINATE\b|\bPLAM\b"""), _ => "Plastic laminate (PLAM)"),
        new(Rx("""\bSEALED\s+CONCRETE\b|\bSLD\.?\s*CONC\.?\b"""), _ => "Sealed concrete"),
        new(Rx("""\bCERAMIC\s+TILE\b|\bCT\b"""), _ => "Ceramic tile (CT)"),
        new(Rx("""\bFIBERGLASS\s+REINFORCED\s+PANEL\b|\bFRP(?:\s*[-–]\s*\d+)?\b"""), _ => "Fiberglass reinforced panel (FRP)"),
        new(Rx("""\bLEXAN\s+SUSPEN[DS]ED\s+CEILING\b|\bLSC\b"""), _ => "Lexan suspended ceiling (LSC)"),
        new(Rx("""\bPAINTED\s+CONCRETE\b|\bPTD\.?\s*CONC\.?\b"""), _ => "Painted concrete"),
        new(Rx("""\bPNT\b|\bPAINT(?:ED)?\b"""), _ => "Paint"),
        new(Rx("""\bCARPET\b"""), _ => "Carpet"),
        new(Rx("""\bRUBBER\b(?!\s+STAIR\s+TREAD)"""), _ => "Rubber flooring"),
        new(Rx("""\bVINYL\s+SOFFIT\b|\bHARDI\s+SOFFIT\b|\bHARDIE\s+SOFFIT\b"""), _ => "Soffit panel"),
        new(Rx("""\bGB\b"""), _ => "Gypsum board (GB)"),
        new(Rx("""\bWB(?:\s*[-–]\s*\d+)?\b"""), _ => "Wall base"),

        // Explicitly stated concrete/masonry/finish terms that are not only abbreviations.
        new(Rx("""(?<!PAINTED )(?<!SEALED )\bCONCRETE\b(?!\s+MASONRY)|(?<!SLD\. )(?<!SLD\.)\bCONC\.?\b"""), _ => "Concrete"),
    };

    public static List<RuleBasedMaterial> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<RuleBasedMaterial>();

        var hits = new List<(string Name, string Evidence, int Start, int Length)>();
        foreach (var rule in Rules)
        {
            foreach (Match match in rule.Pattern.Matches(text))
            {
                var name = rule.Name(match).Trim();
                if (name.Length == 0) continue;
                hits.Add((name, LineEvidence(text, match.Index, match.Length), match.Index, match.Length));
            }
        }

        // For one normalized material, collapse overlapping regex aliases that describe
        // the same text span. Do NOT group by line: two labels for the same material can
        // legitimately appear side-by-side on one drawing row and must increment the count.
        var distinctHits = hits
            .Where(h => MaterialIdentity.Key(h.Name).Length > 0)
            .GroupBy(h => MaterialIdentity.Key(h.Name), StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var accepted = new List<(string Name, string Evidence, int Start, int Length)>();
                foreach (var hit in group.OrderBy(h => h.Start).ThenByDescending(h => h.Length))
                {
                    bool overlaps = accepted.Any(existing =>
                        hit.Start < existing.Start + existing.Length &&
                        hit.Start + hit.Length > existing.Start);
                    if (!overlaps) accepted.Add(hit);
                }
                return accepted;
            })
            .ToList();

        return distinctHits
            .GroupBy(h => MaterialIdentity.Key(h.Name), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .Select(g => new RuleBasedMaterial(
                g.OrderByDescending(x => x.Name.Length).First().Name,
                Math.Max(1, g.Count()),
                string.Join(" || ", g.Select(x => x.Evidence).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(12))))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BoardName(string thickness, string qualifier)
    {
        var name = thickness.Length > 0 ? Clean(thickness) + "\" " : "";
        var q = qualifier.ToUpperInvariant();
        if (q.Contains("MOISTURE", StringComparison.Ordinal)) name += "moisture-resistant ";
        else if (q.Contains("TYPE C", StringComparison.Ordinal)) name += "Type C ";
        else if (q.Contains("TYPE X", StringComparison.Ordinal)) name += "Type X ";
        else if (q.Contains("F.R", StringComparison.Ordinal) || q.Contains("FIRE", StringComparison.Ordinal)) name += "fire-resistant ";
        return name + "gypsum board";
    }

    private static string CompactSize(string value) =>
        Regex.Replace(value, @"\s*([xX])\s*", "X").Replace(" ", "").ToUpperInvariant();

    private static string Clean(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string LineEvidence(string text, int index, int length)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, index));
        start = start < 0 ? 0 : start + 1;
        int end = text.IndexOf('\n', Math.Min(text.Length, index + length));
        if (end < 0) end = text.Length;
        var line = text.Substring(start, Math.Max(0, end - start)).Trim();
        if (line.Length > 220)
        {
            int relative = Math.Max(0, index - start);
            int left = Math.Max(0, relative - 90);
            int take = Math.Min(220, line.Length - left);
            line = (left > 0 ? "..." : "") + line.Substring(left, take) + (left + take < line.Length ? "..." : "");
        }
        return line;
    }
}
