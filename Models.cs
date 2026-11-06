using System;
using System.Collections.Generic;

namespace DrawingTakeoff;

/// <summary>One consolidated takeoff line with provenance for every source occurrence.</summary>
public sealed class TakeoffRow
{
    public int SlNo { get; set; }
    public string Sheet { get; set; } = "";
    public string DetailQty { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Category { get; set; } = CatalogService.OtherCategory;
    public int DisplaySlNo { get; set; }
    public int OtherSlNo { get; set; }

    // Display fields for the requested six-column lumber/hardware takeoff table.
    public string LumberCountDisplay => Category == CatalogService.LumberCategory
        ? Math.Max(1, OccurrenceCount).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string LumberItemDisplay => Category == CatalogService.LumberCategory ? ItemName : "";
    public string HardwareCountDisplay => Category == CatalogService.HardwareCategory
        ? Math.Max(1, OccurrenceCount).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string HardwareItemDisplay => Category == CatalogService.HardwareCategory ? ItemName : "";
    public string OtherCountDisplay => Category == CatalogService.OtherCategory
        ? Math.Max(1, OccurrenceCount).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    // Consolidated quantity/count and audit fields.
    public string Detail { get; set; } = "";
    public decimal? Quantity { get; set; }
    public string QuantityUnit { get; set; } = "";
    public string QuantitySummary { get; set; } = "";
    public int OccurrenceCount { get; set; } = 1;
    public string QuantityBasis { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Method { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string SourceReferences { get; set; } = "";
    public int Page { get; set; }
    public int FileIndex { get; set; }
    public long Seq { get; set; }
}

/// <summary>Open-world model output for one chunk; candidate is only an optional catalog hint index.</summary>
public sealed record ExtractedItem(
    int Candidate,
    string Text,
    decimal? Quantity,
    string Unit,
    string Detail,
    string Evidence,
    string QuantityBasis,
    int Occurrences = 1,
    string Category = "");

public sealed record PageResult(string SheetNumber, string SheetName, List<ExtractedItem> Items);

/// <summary>File-level progress for the WPF queue indicator. Finished includes successful and failed files.</summary>
public sealed record PdfProcessingProgress(
    int Total,
    int Finished,
    int Failed,
    int Processing,
    int Pending,
    string CurrentFile,
    int CurrentFilePages = 0,
    int CurrentFilePagesCompleted = 0,
    double AveragePageSeconds = 0,
    double AveragePagesPerFile = 0,
    bool CurrentPageInProgress = false,
    double ProgressReportedElapsedSeconds = 0);

