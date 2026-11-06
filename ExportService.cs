using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace DrawingTakeoff;

public static class ExportService
{
    public static void ToExcel(string path, IEnumerable<TakeoffRow> rows)
    {
        var list = rows.ToList();
        var primary = list.Where(r => r.Category == CatalogService.LumberCategory ||
                                      r.Category == CatalogService.HardwareCategory)
            .OrderBy(r => r.Category == CatalogService.LumberCategory ? 0 : 1)
            .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        var other = list.Where(r => r.Category != CatalogService.LumberCategory &&
                                    r.Category != CatalogService.HardwareCategory)
            .OrderBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList();

        using var wb = new XLWorkbook();

        // Main sheet: exact requested six-column layout. Each material occupies one row;
        // the unused category pair is blank so lumber and hardware never get conflated.
        var ws = wb.AddWorksheet("Lumber & Hardware");
        string[] h = { "Sl num", "Sheet / Drawing", "Number of lumber items", "Lumber item", "Number of hardware items", "Hardware item" };
        for (int c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];
        for (int i = 0; i < primary.Count; i++)
        {
            var r = primary[i];
            int row = i + 2;
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = r.Sheet;
            if (r.Category == CatalogService.LumberCategory)
            {
                ws.Cell(row, 3).Value = Math.Max(1, r.OccurrenceCount);
                ws.Cell(row, 4).Value = r.ItemName;
            }
            else
            {
                ws.Cell(row, 5).Value = Math.Max(1, r.OccurrenceCount);
                ws.Cell(row, 6).Value = r.ItemName;
            }
        }
        FormatSheet(ws, "A1:F" + Math.Max(1, primary.Count + 1), "A1:F1");
        ws.Column(2).Width = 38;
        ws.Column(4).Width = 44;
        ws.Column(6).Width = 44;
        ws.Column(3).Width = 20;
        ws.Column(5).Width = 21;
        AdjustWrappedRows(ws);

        // Keep all materials which are neither lumber nor hardware; do not silently drop
        // gypsum board, insulation, gypcrete, finishes, sealants, etc.
        var ow = wb.AddWorksheet("Other materials");
        string[] oh = { "Sl num", "Sheet / Drawing", "Number of items", "Other identified material", "Drawing quantity", "Quantity basis", "Source evidence" };
        for (int c = 0; c < oh.Length; c++) ow.Cell(1, c + 1).Value = oh[c];
        for (int i = 0; i < other.Count; i++)
        {
            var r = other[i];
            int row = i + 2;
            ow.Cell(row, 1).Value = i + 1;
            ow.Cell(row, 2).Value = r.Sheet;
            ow.Cell(row, 3).Value = Math.Max(1, r.OccurrenceCount);
            ow.Cell(row, 4).Value = r.ItemName;
            ow.Cell(row, 5).Value = r.QuantitySummary;
            ow.Cell(row, 6).Value = r.QuantityBasis;
            ow.Cell(row, 7).Value = r.Evidence;
        }
        FormatSheet(ow, "A1:G" + Math.Max(1, other.Count + 1), "A1:G1");
        ow.Column(2).Width = 36;
        ow.Column(4).Width = 42;
        ow.Column(5).Width = 34;
        ow.Column(6).Width = 32;
        ow.Column(7).Width = 70;
        AdjustWrappedRows(ow);

        // Audit retains the engineering evidence trail for every extracted material.
        var au = wb.AddWorksheet("Audit");
        string[] ah = { "Sl num", "Category", "Item name", "Source file(s)", "Sheet / Drawing", "Page (first)", "No. of appearances", "Drawing quantity", "Unit", "Quantity summary", "Quantity basis", "Detail / callout refs", "Source references", "Catalog / method", "Evidence" };
        for (int c = 0; c < ah.Length; c++) au.Cell(1, c + 1).Value = ah[c];
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            int row = i + 2;
            au.Cell(row, 1).Value = i + 1;
            au.Cell(row, 2).Value = r.Category;
            au.Cell(row, 3).Value = r.ItemName;
            au.Cell(row, 4).Value = r.SourceFile;
            au.Cell(row, 5).Value = r.Sheet;
            au.Cell(row, 6).Value = r.Page;
            au.Cell(row, 7).Value = Math.Max(1, r.OccurrenceCount);
            au.Cell(row, 8).Value = r.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "";
            au.Cell(row, 9).Value = r.QuantityUnit;
            au.Cell(row, 10).Value = r.QuantitySummary;
            au.Cell(row, 11).Value = r.QuantityBasis;
            au.Cell(row, 12).Value = r.Detail;
            au.Cell(row, 13).Value = r.SourceReferences;
            au.Cell(row, 14).Value = r.Method;
            au.Cell(row, 15).Value = r.Evidence;
        }
        FormatSheet(au, "A1:O" + Math.Max(1, list.Count + 1), "A1:O1");
        au.Column(3).Width = 42;
        au.Column(4).Width = 34;
        au.Column(5).Width = 36;
        au.Column(10).Width = 38;
        au.Column(11).Width = 34;
        au.Column(12).Width = 32;
        au.Column(13).Width = 50;
        au.Column(14).Width = 32;
        au.Column(15).Width = 70;
        AdjustWrappedRows(au);

        wb.SaveAs(path);
    }

    private static void AdjustWrappedRows(IXLWorksheet ws)
    {
        foreach (var row in ws.RowsUsed().Skip(1))
            row.AdjustToContents();
    }

    private static void FormatSheet(IXLWorksheet ws, string range, string headerRange)
    {
        var used = ws.Range(range);
        var header = ws.Range(headerRange);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#17324D");
        header.Style.Alignment.WrapText = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 32;
        used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        used.Style.Alignment.WrapText = true;
        used.SetAutoFilter();
        ws.SheetView.FreezeRows(1);
    }

    public static void ToCsv(string path, IEnumerable<TakeoffRow> rows)
    {
        static string Q(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        var list = rows.ToList();
        var sb = new StringBuilder("Sl num,Category,Sheet / Drawing,Number of lumber items,Lumber item,Number of hardware items,Hardware item,Number of other items,Other identified material,Drawing quantity,Unit,Quantity basis,Source references,Evidence\r\n");
        int n = 1;
        foreach (var r in list.OrderBy(r => r.Category == CatalogService.LumberCategory ? 0 :
                                               r.Category == CatalogService.HardwareCategory ? 1 : 2)
                              .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            var lumberCount = r.Category == CatalogService.LumberCategory ? Math.Max(1, r.OccurrenceCount).ToString(CultureInfo.InvariantCulture) : "";
            var lumberItem = r.Category == CatalogService.LumberCategory ? r.ItemName : "";
            var hardwareCount = r.Category == CatalogService.HardwareCategory ? Math.Max(1, r.OccurrenceCount).ToString(CultureInfo.InvariantCulture) : "";
            var hardwareItem = r.Category == CatalogService.HardwareCategory ? r.ItemName : "";
            var otherCount = r.Category == CatalogService.OtherCategory ? Math.Max(1, r.OccurrenceCount).ToString(CultureInfo.InvariantCulture) : "";
            var otherItem = r.Category == CatalogService.OtherCategory ? r.ItemName : "";
            sb.Append(n++).Append(',').Append(Q(r.Category)).Append(',').Append(Q(r.Sheet)).Append(',')
                .Append(lumberCount).Append(',').Append(Q(lumberItem)).Append(',')
                .Append(hardwareCount).Append(',').Append(Q(hardwareItem)).Append(',')
                .Append(otherCount).Append(',').Append(Q(otherItem)).Append(',')
                .Append(Q(r.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "")).Append(',')
                .Append(Q(r.QuantityUnit)).Append(',').Append(Q(r.QuantityBasis)).Append(',')
                .Append(Q(r.SourceReferences)).Append(',').Append(Q(r.Evidence)).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
