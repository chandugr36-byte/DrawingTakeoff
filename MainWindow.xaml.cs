using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DrawingTakeoff;

public partial class MainWindow : Window
{
    private const string OllamaUrl = "http://localhost:11434";
    private readonly ObservableCollection<string> _pdfs = new();
    private readonly ObservableCollection<TakeoffRow> _rows = new();
    private readonly ObservableCollection<TakeoffRow> _otherRows = new();
    private readonly CatalogService _catalog = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _runWatch = new();
    private PdfProcessingProgress? _latestPdfProgress;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        PdfList.ItemsSource = _pdfs;
        ResultGrid.ItemsSource = _rows;
        OtherGrid.ItemsSource = _otherRows;
        _timer.Tick += (_, _) => UpdateTimeEstimate();

        Logger.Message += (level, message) =>
        {
            if (level < LogLevel.Warn) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ActivityText.Text = message;
                StatusText.Text = level == LogLevel.Error ? "Attention required — see source evidence / log" : StatusText.Text;
            }));
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadCatalogFiles();
        await RefreshModelsAsync();
    }

    private void LoadCatalogFiles()
    {
        try
        {
            var resources = Path.Combine(AppContext.BaseDirectory, "Resources");
            var lumberPath = Path.Combine(resources, "lumber_catalog.txt");
            var hardwarePath = Path.Combine(resources, "hardware_catalog.txt");
            _catalog.Load(lumberPath, hardwarePath);
            CatalogInfo.Text = $"Lumber: {_catalog.LumberItems.Count:N0} | Hardware: {_catalog.HardwareItems.Count:N0}";
            Logger.Info($"Loaded separate catalogs: {_catalog.LumberItems.Count} unique lumber items and {_catalog.HardwareItems.Count} unique hardware items.");
        }
        catch (Exception ex)
        {
            CatalogInfo.Text = "Reference catalogs unavailable";
            Logger.Error("Reference catalog load failed", ex);
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        RefreshModelsBtn.IsEnabled = false;
        try
        {
            StatusText.Text = "Connecting to Ollama...";
            var models = await new OllamaClient(OllamaUrl).ListModelsAsync();
            AnalysisModelBox.ItemsSource = models;
            var preferred = ChooseFastAnalysisModel(models);
            if (!string.IsNullOrWhiteSpace(preferred)) AnalysisModelBox.SelectedItem = preferred;

            if (models.Count == 0)
            {
                ModelHintText.Text = "No Ollama model installed. Pull qwen2.5vl:3b for diagram analysis or qwen2.5:3b for text-only analysis.";
                StatusText.Text = "Ollama is running, but no models are installed";
            }
            else
            {
                SetModelHint(AnalysisModelBox.SelectedItem?.ToString() ?? "");
                StatusText.Text = $"Ready — {models.Count} local model(s) found";
                Logger.Info("Ollama models: " + string.Join(", ", models));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Cannot connect to Ollama";
            ModelHintText.Text = "Start Ollama, then refresh models.";
            Logger.Error("Cannot connect to Ollama at " + OllamaUrl, ex);
        }
        finally { RefreshModelsBtn.IsEnabled = true; }
    }

    private static string ChooseFastAnalysisModel(IReadOnlyList<string> models)
    {
        // Prefer one multimodal model so a page is analysed in one request, not two model passes.
        string[] preferred = { "qwen2.5vl:3b", "qwen2.5-vl:3b", "qwen2.5:3b", "llama3.2:3b", "gemma2:2b", "moondream:1.8b" };
        foreach (var wanted in preferred)
        {
            var found = models.FirstOrDefault(m => m.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return models.FirstOrDefault() ?? "";
    }

    private static bool IsVisionModel(string model) =>
        model.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("moondream", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("bakllava", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("vision", StringComparison.OrdinalIgnoreCase);

    private void SetModelHint(string model)
    {
        ModelHintText.Text = IsVisionModel(model)
            ? "Diagram mode: image + extracted text in one AI request per page; checks lumber and connector hardware."
            : "Text-only mode: can analyse embedded PDF text but cannot inspect drawing graphics. Install/select a vision model for diagram-only callouts.";
    }

    private void AnalysisModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelHintText is not null) SetModelHint(AnalysisModelBox.SelectedItem?.ToString() ?? "");
    }

    private void AddPdfs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF drawings (*.pdf)|*.pdf", Multiselect = true };
        if (dialog.ShowDialog() == true)
            foreach (var file in dialog.FileNames) AddPdf(file);
    }

    private void PdfList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))) AddPdf(file);
    }

    private void AddPdf(string file)
    {
        if (!File.Exists(file) || _pdfs.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
        _pdfs.Add(file);
        PdfCountText.Text = $"{_pdfs.Count} PDF(s) selected";
        PdfProcessingText.Text = $"PDFs: 0 finished · 0 processing · {_pdfs.Count} pending";
        ActivityText.Text = $"{Path.GetFileName(file)} added to the analysis queue. Extraction starts only after you click Analyse drawings.";
        Logger.Info($"Added PDF to queue: {file}");
    }

    private void RemovePdf_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in PdfList.SelectedItems.Cast<string>().ToList()) _pdfs.Remove(file);
        PdfCountText.Text = $"{_pdfs.Count} PDF(s) selected";
        PdfProcessingText.Text = $"PDFs: 0 finished · 0 processing · {_pdfs.Count} pending";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfs.Count == 0)
        {
            StatusText.Text = "Add at least one PDF drawing";
            return;
        }

        var model = AnalysisModelBox.SelectedItem?.ToString() ?? "";
        if (model.Length == 0)
        {
            StatusText.Text = "Select an installed Ollama model";
            return;
        }

        if (!IsVisionModel(model))
        {
            var choice = MessageBox.Show(
                "The selected model is text-only. It can read embedded PDF text, but it cannot inspect drawing graphics, symbols or image-only connector/lumber callouts.\n\nInstall a vision model (for example qwen2.5vl:3b) for full diagram analysis. Continue with text-only analysis?",
                "Text-only model selected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
        }

        _rows.Clear();
        _otherRows.Clear();
        ResultCountText.Text = "0 lumber / hardware items · 0 other materials";
        PdfProcessingText.Text = $"PDFs: 0 finished · 0 processing · {_pdfs.Count} pending";
        _latestPdfProgress = null;
        EvidenceText.Text = "Select a row to inspect its source evidence.";
        ActivityText.Text = "Live results are incremental: the local scan publishes preliminary matches first; Ollama continues to identify uncatalogued items, interpret diagram content and review quantity evidence until each page is complete.";
        Bar.Value = 0;
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        RefreshModelsBtn.IsEnabled = false;
        AnalysisModelBox.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _runWatch.Restart();
        UpdateTimeEstimate();
        _timer.Start();

        var settings = new AgentSettings
        {
            TextModel = model,
            VisionModel = IsVisionModel(model) ? model : null,
            NumCtx = 4096,
            NumPredict = 800,
            ChunkChars = 4600,
            SinglePassMaxChars = 4600,
            RenderMaxSidePx = 1000,
            MinTextChars = 60,
            FirstTokenTimeoutSec = 300,
            IdleTimeoutSec = 60,
            PdfStepTimeoutSec = 45,
            LayoutAwareText = false,
            KeepUnmatched = true,
            LlmParallelism = 1,
            VisionParallelism = 1,
            ExtractParallelism = 1,
            UseCache = true,
            WarmUp = false,
            FastSinglePass = true
        };

        var progress = new Progress<double>(value => Bar.Value = value);
        var status = new Progress<string>(value => StatusText.Text = value);
        var liveRows = new Progress<IReadOnlyList<TakeoffRow>>(items => UpdateRows(items));
        var pdfProgress = new Progress<PdfProcessingProgress>(p =>
        {
            _latestPdfProgress = p;
            var failed = p.Failed > 0 ? $" · {p.Failed} failed" : string.Empty;
            PdfProcessingText.Text = $"PDFs: {p.Finished}/{p.Total} finished · {p.Processing} processing · {p.Pending} pending{failed}";
            if (!string.IsNullOrWhiteSpace(p.CurrentFile))
                ActivityText.Text = $"Current PDF: {p.CurrentFile}";
            UpdateTimeEstimate();
        });
        var agent = new TakeoffAgent(new OllamaClient(OllamaUrl), _catalog, settings, progress, status, liveRows, pdfProgress);
        var files = _pdfs.ToList();
        List<TakeoffRow> finalRows;

        try
        {
            finalRows = await Task.Run(() => agent.RunAsync(files, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            finalRows = agent.Snapshot();
            StatusText.Text = "Cancelled — partial results retained";
            Logger.Warn("User cancelled the takeoff run.");
        }
        catch (Exception ex)
        {
            finalRows = agent.Snapshot();
            StatusText.Text = "Analysis stopped — partial results retained";
            Logger.Error("Takeoff run failed", ex);
        }
        finally
        {
            _timer.Stop();
            _runWatch.Stop();
            UpdateTimeEstimate();
            RunBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;
            RefreshModelsBtn.IsEnabled = true;
            AnalysisModelBox.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }

        UpdateRows(finalRows);
        if (!StatusText.Text.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase) &&
            !StatusText.Text.StartsWith("Analysis stopped", StringComparison.OrdinalIgnoreCase))
            StatusText.Text = finalRows.Count == 0
                ? "Completed — no material rows found; check the log folder"
                : $"Completed — {finalRows.Count} consolidated material item(s)";
        ExcelBtn.IsEnabled = CsvBtn.IsEnabled = finalRows.Count > 0;
        var elapsedText = _runWatch.Elapsed.ToString(@"mm\:ss");
        ActivityText.Text = $"Last run: {elapsedText}. Local matches were displayed as preliminary findings; the AI stage continued to discover, classify and reconcile additional materials before completion.";
    }

    private void UpdateTimeEstimate()
    {
        if (ElapsedText is null) return;

        var elapsed = _runWatch.Elapsed;
        ElapsedText.Text = $"Elapsed: {FormatDuration(elapsed)}";

        var progress = _latestPdfProgress;
        if (progress is null || progress.Total <= 0)
        {
            EstimatedTotalText.Text = "Estimated total: --";
            RemainingText.Text = "Remaining: --";
            return;
        }

        if (progress.Finished >= progress.Total)
        {
            EstimatedTotalText.Text = $"Estimated total: {FormatDuration(elapsed)}";
            RemainingText.Text = "Remaining: 00:00";
            return;
        }

        // ETA is based on measured page durations and observed average pages per PDF.
        // The first page establishes a baseline; before that we deliberately show
        // "calibrating" rather than displaying a fabricated time estimate.
        if (progress.AveragePageSeconds <= 0 || progress.AveragePagesPerFile <= 0)
        {
            EstimatedTotalText.Text = "Estimated total: calibrating";
            RemainingText.Text = progress.Processing > 0
                ? "Remaining: learning first page"
                : "Remaining: --";
            return;
        }

        double remainingPagesInCurrentFile = 0;
        double activePageRemainingSeconds = 0;
        if (progress.Processing > 0)
        {
            remainingPagesInCurrentFile = progress.CurrentFilePages > 0
                ? Math.Max(0, progress.CurrentFilePages - progress.CurrentFilePagesCompleted)
                : progress.AveragePagesPerFile;

            if (progress.CurrentPageInProgress && remainingPagesInCurrentFile > 0)
            {
                // Let the ETA count down while Ollama is working on the current page,
                // instead of leaving a frozen estimate until the page finishes.
                double currentPageElapsed = Math.Max(0,
                    elapsed.TotalSeconds - progress.ProgressReportedElapsedSeconds);
                activePageRemainingSeconds = Math.Max(0,
                    progress.AveragePageSeconds - currentPageElapsed);
                remainingPagesInCurrentFile = Math.Max(0, remainingPagesInCurrentFile - 1);
            }
        }

        double estimatedPagesInPendingFiles = progress.Pending * progress.AveragePagesPerFile;
        double remainingSeconds = Math.Max(0, activePageRemainingSeconds +
            progress.AveragePageSeconds * (remainingPagesInCurrentFile + estimatedPagesInPendingFiles));

        if (progress.Finished >= progress.Total) remainingSeconds = 0;

        RemainingText.Text = $"Remaining: {FormatDuration(TimeSpan.FromSeconds(remainingSeconds))}";
        EstimatedTotalText.Text = $"Estimated total: {FormatDuration(TimeSpan.FromSeconds(elapsed.TotalSeconds + remainingSeconds))}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalDays >= 1)
            return ((int)value.TotalDays) + "d " + value.ToString(@"hh\:mm\:ss");
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private void UpdateRows(IEnumerable<TakeoffRow> rows)
    {
        var copy = rows.OrderBy(r => r.Category == CatalogService.LumberCategory ? 0 :
                                     r.Category == CatalogService.HardwareCategory ? 1 : 2)
                       .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        var lumberHardware = copy.Where(r => r.Category == CatalogService.LumberCategory ||
                                             r.Category == CatalogService.HardwareCategory).ToList();
        var other = copy.Where(r => r.Category != CatalogService.LumberCategory &&
                                    r.Category != CatalogService.HardwareCategory).ToList();

        _rows.Clear();
        for (int i = 0; i < lumberHardware.Count; i++)
        {
            lumberHardware[i].DisplaySlNo = i + 1;
            _rows.Add(lumberHardware[i]);
        }

        _otherRows.Clear();
        for (int i = 0; i < other.Count; i++)
        {
            other[i].OtherSlNo = i + 1;
            _otherRows.Add(other[i]);
        }

        ResultCountText.Text = $"{_rows.Count} lumber / hardware item(s) · {_otherRows.Count} other material(s)";
        ExcelBtn.IsEnabled = CsvBtn.IsEnabled = copy.Count > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Cancelling after the current PDF/model operation...";
        _cts?.Cancel();
        CancelBtn.IsEnabled = false;
    }

    private void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "DrawingTakeoff.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ExportService.ToExcel(dialog.FileName, _rows.Concat(_otherRows));
            StatusText.Text = "Excel takeoff exported";
        }
        catch (Exception ex) { Logger.Error("Excel export failed", ex); StatusText.Text = "Excel export failed — check the log"; }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "DrawingTakeoff.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ExportService.ToCsv(dialog.FileName, _rows.Concat(_otherRows));
            StatusText.Text = "CSV takeoff exported";
        }
        catch (Exception ex) { Logger.Error("CSV export failed", ex); StatusText.Text = "CSV export failed — check the log"; }
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is TakeoffRow row)
        {
            EvidenceText.Text = $"{row.Category}: {row.ItemName} | {row.SourceReferences} | {row.Method} | {row.QuantityBasis}\nEvidence: {row.Evidence}";
        }
    }
}
