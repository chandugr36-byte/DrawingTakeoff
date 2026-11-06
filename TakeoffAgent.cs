using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DrawingTakeoff;

public sealed class AgentSettings
{
    public string TextModel { get; set; } = "qwen2.5:3b";
    public string? VisionModel { get; set; }
    public int NumCtx { get; set; } = 4096;            // conservative KV-cache footprint for memory-limited PCs
    public int NumPredict { get; set; } = 700;         // allow several material rows in a compact JSON response
    public int ChunkChars { get; set; } = 5600;        // bounded prompt size; every page chunk is processed
    public int RenderMaxSidePx { get; set; } = 1100;   // lower peak memory for scanned drawing pages
    public int MinTextChars { get; set; } = 60;        // below this a page counts as scanned
    public int FirstTokenTimeoutSec { get; set; } = 300;
    public int IdleTimeoutSec { get; set; } = 60;
    public int PdfStepTimeoutSec { get; set; } = 45;
    public bool LayoutAwareText { get; set; }
    public bool KeepUnmatched { get; set; } = true;   // newly discovered materials must not be silently discarded
    public int LlmParallelism { get; set; } = 1;       // one model request avoids duplicate KV-cache allocations
    public int VisionParallelism { get; set; } = 1;    // one image/model request at a time
    public int ExtractParallelism { get; set; } = 1;   // never keep multiple large PDF documents open
    public bool UseCache { get; set; } = true;
    public bool WarmUp { get; set; } = false;               // fast path loads on the first real page; no extra warm-up request
    public bool FastSinglePass { get; set; } = true;       // one model request per page, no separate text + vision stages
    public int SinglePassMaxChars { get; set; } = 5600;   // compact single-page prompt sized for 4k context
}

internal sealed record PageContext(int FileIndex, string FileName, int Page, string Tag,
                                   string[] Candidates, string HintNo, string HintName,
                                   HashSet<string>? EvidenceTokens);
internal sealed record PageJob(PageContext Ctx, List<string> Chunks, string CacheKey);
internal sealed record VisionJob(int FileIndex, string FilePath, int Page);

/// <summary>
/// Memory-bounded pipeline:
///   [one PdfPig file/page producer] -> [one-page bounded channel] -> [one Ollama worker]
///   scanned pages are queued as lightweight file/page references and rendered/analysed one at a time.
/// </summary>
public sealed class TakeoffAgent
{
    private readonly OllamaClient _llm;
    private readonly CatalogService _catalog;
    private readonly CatalogIndex _index;
    private readonly AgentSettings _s;
    private readonly IProgress<double> _progress;
    private readonly IProgress<string> _status;
    private readonly ResultCache _cache;
    private readonly IProgress<IReadOnlyList<TakeoffRow>>? _liveRows;
    private readonly IProgress<PdfProcessingProgress>? _pdfProgress;
    private readonly ConcurrentQueue<TakeoffRow> _raw = new();
    private string _systemPrompt = "";

    private int _totalPages, _donePages;
    private bool _visionStageEnabled;
    private int _pagesOk, _pagesFailed, _pagesSkipped, _pagesNoCandidates, _cacheHits, _llmCalls;
    private long _seq;

    public TakeoffAgent(OllamaClient llm, CatalogService catalog, AgentSettings settings,
                        IProgress<double> progress, IProgress<string> status,
                        IProgress<IReadOnlyList<TakeoffRow>>? liveRows = null,
                        IProgress<PdfProcessingProgress>? pdfProgress = null)
    {
        _llm = llm; _catalog = catalog; _s = settings; _progress = progress; _status = status; _liveRows = liveRows; _pdfProgress = pdfProgress;
        _index = new CatalogIndex(catalog.Items);
        _cache = new ResultCache(settings.UseCache);
    }

    public List<TakeoffRow> Snapshot() => Merge(_raw.ToList());

    // ====================================================================== run
    public async Task<List<TakeoffRow>> RunAsync(IReadOnlyList<string> pdfs, CancellationToken ct)
    {
        if (_s.FastSinglePass)
            return await RunFastSinglePassAsync(pdfs, ct).ConfigureAwait(false);

        var total = Stopwatch.StartNew();
        // Memory-first execution: a single Ollama request and a single open PDF.
        // Higher concurrency can multiply model KV-cache and PdfPig working-set memory.
        const int llmWorkers = 1;
        const int extractWorkers = 1;
        Logger.Info($"=== RUN START: {pdfs.Count} PDF(s) | text '{_s.TextModel}' | vision '{_s.VisionModel ?? "none"}' | ctx {_s.NumCtx} | LLM workers {llmWorkers} | extract workers {extractWorkers} | cache {(_s.UseCache ? "on" : "off")}");
        Logger.Info($"    log file: {Logger.CurrentLogFile}");
        LogMemory("run start");
        try
        {
            // ---------- PRE-FLIGHT ----------
            _status.Report("Checking Ollama...");
            List<string> installed;
            try { installed = await _llm.ListModelsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"Cannot reach Ollama at {_llm.BaseUrl}. Start it ('ollama serve' or the Ollama tray app) and click 'Refresh models'.", ex);
                return Snapshot();
            }
            if (!OllamaClient.ModelExists(installed, _s.TextModel))
            {
                Logger.Error($"Text model '{_s.TextModel}' is NOT installed. Run in a terminal:  ollama pull {_s.TextModel}");
                return Snapshot();
            }
            if (!string.IsNullOrWhiteSpace(_s.VisionModel) && !OllamaClient.ModelExists(installed, _s.VisionModel!))
            {
                Logger.Warn($"Vision model '{_s.VisionModel}' is not installed -> scanned pages will be skipped. (ollama pull {_s.VisionModel})");
                _s.VisionModel = null;
            }

            _visionStageEnabled = !string.IsNullOrWhiteSpace(_s.VisionModel);
            _systemPrompt = BuildSystemPrompt();
            Logger.Info($"Static system prompt ~{_systemPrompt.Length / 3} tokens. Catalog ({_catalog.Items.Count} items) is a matching reference only; pages are analysed even when there are zero catalog hits.");
            Logger.Info(_visionStageEnabled
                ? "MODE: open-world text discovery + visual review of EVERY sheet (text-first, then one-page-at-a-time vision)."
                : "MODE: open-world text discovery of EVERY readable sheet; visual-only symbols/raster-only pages require a vision model.");

            // Lazy warm-up: only load the model when the first uncached page actually needs AI.
            // This avoids paying model-load cost for runs whose pages are all skipped or cached.
            var warm = new Lazy<Task>(() => _s.WarmUp ? WarmUpAsync(ct) : Task.CompletedTask,
                LazyThreadSafetyMode.ExecutionAndPublication);

            // ---------- PIPELINE ----------
            var channel = Channel.CreateBounded<PageJob>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            var visionJobs = new ConcurrentQueue<VisionJob>();

            // The single consumer performs warm-up only when an uncached text page arrives.
            var consumers = Enumerable.Range(1, llmWorkers)
                .Select(id => Task.Run(() => ConsumeAsync(id, channel.Reader, warm, ct), CancellationToken.None))
                .ToArray();

            try
            {
                // Process PDFs one at a time. This is intentionally sequential so PdfPig
                // never holds several large drawing documents in memory simultaneously.
                for (int i = 0; i < pdfs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await ExtractFileAsync(i, pdfs[i], pdfs.Count, channel.Writer, visionJobs, ct);
                }
            }
            catch
            {
                channel.Writer.TryComplete();
                try { await Task.WhenAll(consumers); } catch { /* preserve the producer/cancellation exception */ }
                throw;
            }
            channel.Writer.TryComplete();

            await Task.WhenAll(consumers);
            if (warm.IsValueCreated) await warm.Value;

            if (!visionJobs.IsEmpty)
            {
                // Do not keep the text model resident while loading a separate vision
                // model; many laptops cannot hold both in RAM/VRAM at once.
                var visionSafeToLoad = true;
                if (warm.IsValueCreated && !string.IsNullOrWhiteSpace(_s.VisionModel) &&
                    !string.Equals(_s.TextModel, _s.VisionModel, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _llm.UnloadModelAsync(_s.TextModel, ct);
                        Logger.Info($"Unloaded text model '{_s.TextModel}' before vision stage to release memory.");
                    }
                    catch (Exception ex)
                    {
                        visionSafeToLoad = false;
                        Logger.Warn("Text model could not be unloaded. Vision stage is being skipped to avoid loading two models and exhausting RAM/VRAM.", ex);
                    }
                }

                if (visionSafeToLoad) await RunVisionAsync(visionJobs, ct);
                else
                {
                    while (visionJobs.TryDequeue(out _))
                    {
                        Interlocked.Increment(ref _pagesSkipped);
                        MarkDone();
                    }
                }
            }

            return Snapshot();
        }
        finally
        {
            var mins = Math.Max(0.01, total.Elapsed.TotalMinutes);
            Logger.Info($"=== RUN END in {total.Elapsed:mm\\:ss} ({_donePages / mins:0.0} pages/min): ok {_pagesOk}, failed {_pagesFailed}, skipped {_pagesSkipped}, " +
                        $"pages with no catalog hints (still analysed) {_pagesNoCandidates}, cache hits {_cacheHits}, LLM calls {_llmCalls}, raw rows {_raw.Count}");
            _status.Report("Idle");
        }
    }

    // ====================================================================== fast single-pass path
    /// <summary>
    /// Fast path: deterministic extraction scans the entire page text locally, then the
    /// selected model receives ONE compact request per page. If a vision-capable model is
    /// selected, the same request contains both the extracted text and a low-resolution
    /// page image. This removes the old text-chunk stage followed by a second vision stage.
    /// </summary>
    private async Task<List<TakeoffRow>> RunFastSinglePassAsync(IReadOnlyList<string> pdfs, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var model = !string.IsNullOrWhiteSpace(_s.VisionModel) ? _s.VisionModel!.Trim() : _s.TextModel.Trim();
        bool useVision = IsVisionModelName(model);
        _visionStageEnabled = false; // one work unit per page in fast mode
        _donePages = 0; _totalPages = 0;
        int filesFinished = 0;
        int filesFailed = 0;
        int completedPageCount = 0;
        int completedFilesWithPageCount = 0;
        double completedPageSeconds = 0;
        double completedFilePages = 0;
        ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 0, "", 0, 0, 0, 0);
        Logger.Info($"=== FAST RUN START: {pdfs.Count} PDF(s) | model '{model}' | mode {(useVision ? "single-pass vision + extracted text" : "single-pass text")} | ctx {_s.NumCtx} | cache {(_s.UseCache ? "on" : "off")}");
        LogMemory("fast run start");

        try
        {
            _status.Report("Checking local AI model...");
            List<string> installed;
            try { installed = await _llm.ListModelsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"Cannot reach Ollama at {_llm.BaseUrl}. Start Ollama and retry.", ex);
                return Snapshot();
            }

            if (!OllamaClient.ModelExists(installed, model))
            {
                Logger.Error($"Model '{model}' is not installed. Run: ollama pull {model}");
                return Snapshot();
            }

            _systemPrompt = BuildFastSystemPrompt();
            Logger.Info("FAST MODE: one model request per page; no warm-up request, no chunk-by-chunk inference, and no second visual pass.");
            Logger.Info("The deterministic material extractor scans the full extracted page text before the model call.");

            for (int fileIndex = 0; fileIndex < pdfs.Count; fileIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var path = pdfs[fileIndex];
                var fileName = Path.GetFileName(path);
                PdfSession? session = null;
                bool fileFailed = false;
                double avgPageSecondsAtStart = completedPageCount > 0
                    ? completedPageSeconds / completedPageCount : 0;
                double avgPagesPerFileAtStart = completedFilesWithPageCount > 0
                    ? completedFilePages / completedFilesWithPageCount : 0;
                ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 1, fileName,
                    0, 0, avgPageSecondsAtStart, avgPagesPerFileAtStart,
                    false, total.Elapsed.TotalSeconds);
                try
                {
                    _status.Report($"Opening {fileName}...");
                    var openWatch = Stopwatch.StartNew();
                    session = await PdfSession.OpenAsync(path, TimeSpan.FromSeconds(_s.PdfStepTimeoutSec * 2), ct).ConfigureAwait(false);
                    Interlocked.Add(ref _totalPages, session.PageCount);
                    Logger.Info($"--- {fileName}: {session.PageCount} page(s), opened in {openWatch.Elapsed.TotalSeconds:0.0}s");
                    double avgPagesPerFileAfterOpen = completedFilesWithPageCount > 0
                        ? completedFilePages / completedFilesWithPageCount : session.PageCount;
                    ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 1, fileName,
                        session.PageCount, 0,
                        completedPageCount > 0 ? completedPageSeconds / completedPageCount : 0,
                        avgPagesPerFileAfterOpen, false, total.Elapsed.TotalSeconds);

                    for (int page = 1; page <= session.PageCount; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var pageWatch = Stopwatch.StartNew();
                        ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 1, fileName,
                            session.PageCount, page - 1,
                            completedPageCount > 0 ? completedPageSeconds / completedPageCount : 0,
                            completedFilesWithPageCount > 0
                                ? completedFilePages / completedFilesWithPageCount
                                : session.PageCount,
                            true, total.Elapsed.TotalSeconds);
                        var tag = $"[{fileName} p{page}/{session.PageCount}]";
                        try
                        {
                            _status.Report($"{fileName} — page {page}/{session.PageCount}: extracting text and running the quick material scan...");
                            var text = "";
                            if (!session.TextBroken)
                            {
                                try
                                {
                                    text = await session.GetTextAsync(page,
                                        TimeSpan.FromSeconds(_s.PdfStepTimeoutSec), _s.LayoutAwareText, ct).ConfigureAwait(false);
                                }
                                catch (TimeoutException ex)
                                {
                                    session.TextBroken = true;
                                    Logger.Warn($"{tag} text extraction timed out; continuing with image only when a vision model is selected. {ex.Message}");
                                }
                            }

                            var (sheetNo, sheetName) = SheetInfoExtractor.Detect(text);
                            var sheet = BuildSheetLabel(fileName, page, sheetNo, sheetName);
                            var rules = DrawingMaterialExtractor.Extract(text);
                            var catalogLumberHits = _catalog.FindExactCatalogTextMatches(text, CatalogService.LumberCategory);
                            var catalogHardwareHits = _catalog.FindExactCatalogTextMatches(text, CatalogService.HardwareCategory);
                            var hardwareCodeHits = _catalog.FindHardwarePartCodeMatches(text);
                            var catalogHits = catalogLumberHits.Concat(catalogHardwareHits).Concat(hardwareCodeHits).ToList();
                            if (catalogHits.Count > 0)
                                rules = MergeRuleHits(rules, catalogHits);
                            if (rules.Count > 0)
                            {
                                AddRuleBasedResults(fileIndex, fileName, page, sheet, rules);
                                PublishLiveRows(); // preliminary local-rule findings are visible while AI continues
                                _status.Report($"{fileName} — page {page}: {rules.Count} preliminary local match(es) shown; AI analysis is still running to discover more items and quantities...");
                            }
                            else
                            {
                                _status.Report($"{fileName} — page {page}: quick scan found no known terms; AI discovery is still running...");
                            }

                            if (text.Trim().Length < _s.MinTextChars && !useVision)
                            {
                                Interlocked.Increment(ref _pagesSkipped);
                                Logger.Warn($"{tag} has no extractable text and selected model '{model}' is text-only; skipping AI for this page.");
                                continue;
                            }

                            var candidateHits = _index.Select(text).Names;
                            var lumberHints = catalogLumberHits.Select(x => x.Name);
                            var hardwareHints = catalogHardwareHits.Select(x => x.Name).Concat(hardwareCodeHits.Select(x => x.Name));
                            var lumberCandidates = candidateHits.Where(x => _catalog.Classify(x) == CatalogService.LumberCategory)
                                .Concat(lumberHints).Distinct(StringComparer.Ordinal).Take(24);
                            var hardwareCandidates = candidateHits.Where(x => _catalog.Classify(x) == CatalogService.HardwareCategory)
                                .Concat(hardwareHints).Distinct(StringComparer.Ordinal).Take(30);
                            var candidates = lumberCandidates.Concat(hardwareCandidates)
                                .Distinct(StringComparer.Ordinal).ToArray();
                            var tokens = Tokens(text).ToHashSet(StringComparer.Ordinal);
                            var ctx = new PageContext(fileIndex, fileName, page, tag, candidates, sheetNo, sheetName, tokens);
                            var promptText = TextWindows.BuildSinglePass(text, _s.SinglePassMaxChars, out var originalChars, out var keptChars);
                            if (originalChars > keptChars)
                                Logger.Warn($"{tag} compacted page text from {originalChars} to {keptChars} chars for a single model call; deterministic extraction still scanned all source text.");

                            var fileInfo = new FileInfo(path);
                            var cacheKey = ResultCache.Key(model, ResultCache.PromptVersion, "fast-single-pass-v4-lumber-hardware-dedicated-inspection",
                                Path.GetFullPath(path), fileInfo.Length.ToString(CultureInfo.InvariantCulture),
                                fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), page.ToString(CultureInfo.InvariantCulture),
                                promptText, useVision ? "vision" : "text");

                            if (_cache.TryGet(cacheKey, out var cachedJson) && !string.IsNullOrWhiteSpace(cachedJson) &&
                                ApplyReplies(ctx, new[] { cachedJson }, verified: !useVision))
                            {
                                Interlocked.Increment(ref _cacheHits);
                                Interlocked.Increment(ref _pagesOk);
                                Logger.Info($"{tag} served from cache — no model call.");
                                PublishLiveRows();
                                Logger.Info($"{tag} completed from cache in {pageWatch.Elapsed.TotalSeconds:0.0}s");
                                continue;
                            }
                            else if (!string.IsNullOrWhiteSpace(cachedJson))
                            {
                                Logger.Warn($"{tag} cache reply could not be parsed; running a fresh model request.");
                            }

                            string[]? images = null;
                            if (useVision)
                            {
                                var renderWatch = Stopwatch.StartNew();
                                var png = await PdfRenderer.RenderPngAsync(path, page,
                                    TimeSpan.FromSeconds(_s.PdfStepTimeoutSec * 2), ct, _s.RenderMaxSidePx).ConfigureAwait(false);
                                var b64 = Convert.ToBase64String(png);
                                Logger.Info($"{tag} rendered {png.Length / 1024} KB ({renderWatch.Elapsed.TotalSeconds:0.0}s)");
                                png = Array.Empty<byte>();
                                images = new[] { b64 };
                            }

                            _status.Report($"{fileName} — page {page}: Ollama is reviewing the drawing" + (useVision ? " image + text..." : " text...") );
                            var user = BuildFastUserPrompt(ctx, promptText, useVision);
                            var json = await CallModelAsync(model, user, images,
                                tag + " single-pass", ct, extraTokens: useVision ? 500 : 0, maxOutputTokens: _s.NumPredict).ConfigureAwait(false);
                            bool parsed = ApplyReplies(ctx, new[] { json }, verified: !useVision);
                            if (parsed) await _cache.PutAsync(cacheKey, json).ConfigureAwait(false);
                            if (images is not null) images = null;
                            Interlocked.Increment(ref _pagesOk);
                            PublishLiveRows();
                            _status.Report($"{fileName} — page {page}: AI pass completed; findings reconciled.");
                            Logger.Info($"{tag} complete in {pageWatch.Elapsed.TotalSeconds:0.0}s ({(parsed ? "parsed" : "model reply needs review")}); rows currently {_raw.Count}.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            fileFailed = true;
                            Interlocked.Increment(ref _pagesFailed);
                            Logger.Error($"{tag} failed ({ex.GetType().Name}): {ex.Message}", ex);
                        }
                        finally
                        {
                            pageWatch.Stop();
                            completedPageSeconds += pageWatch.Elapsed.TotalSeconds;
                            completedPageCount++;
                            MarkFastDone(fileIndex, page, session.PageCount, pdfs.Count);
                            double avgPageSeconds = completedPageSeconds / Math.Max(1, completedPageCount);
                            double avgPagesPerFile = completedFilesWithPageCount > 0
                                ? completedFilePages / completedFilesWithPageCount
                                : session.PageCount;
                            ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 1, fileName,
                                session.PageCount, page, avgPageSeconds, avgPagesPerFile,
                                false, total.Elapsed.TotalSeconds);
                            LogMemory($"page complete: {fileName} p{page}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    fileFailed = true;
                    throw;
                }
                catch (Exception ex)
                {
                    fileFailed = true;
                    Logger.Error($"Could not process '{fileName}': {ex.Message}", ex);
                }
                finally
                {
                    int finishedFilePageCount = session?.PageCount ?? 0;
                    session?.Dispose();
                    session = null;
                    filesFinished++;
                    if (fileFailed) filesFailed++;
                    if (finishedFilePageCount > 0)
                    {
                        completedFilePages += finishedFilePageCount;
                        completedFilesWithPageCount++;
                    }
                    double avgPageSeconds = completedPageCount > 0
                        ? completedPageSeconds / completedPageCount : 0;
                    double avgPagesPerFile = completedFilesWithPageCount > 0
                        ? completedFilePages / completedFilesWithPageCount : 0;
                    ReportPdfProgress(pdfs.Count, filesFinished, filesFailed, 0, "",
                        0, 0, avgPageSeconds, avgPagesPerFile,
                        false, total.Elapsed.TotalSeconds);
                }
            }

            _progress.Report(1.0);
            var result = Snapshot();
            PublishLiveRows();
            return result;
        }
        finally
        {
            var mins = Math.Max(0.01, total.Elapsed.TotalMinutes);
            Logger.Info($"=== FAST RUN END in {total.Elapsed:mm\\:ss} ({_donePages / mins:0.0} pages/min): ok {_pagesOk}, failed {_pagesFailed}, skipped {_pagesSkipped}, cache hits {_cacheHits}, LLM calls {_llmCalls}, rows {_raw.Count}");
            _status.Report("Completed");
        }
    }

    private void MarkFastDone(int fileIndex, int page, int pagesInFile, int fileCount)
    {
        Interlocked.Increment(ref _donePages);
        // Stable progress: do not move backwards as more PDFs are opened and their page
        // counts become known. Each file contributes an equal fraction of the run.
        var perFile = page / (double)Math.Max(1, pagesInFile);
        _progress.Report(Math.Clamp((fileIndex + perFile) / Math.Max(1, fileCount), 0.0, 1.0));
    }

    private void PublishLiveRows()
    {
        if (_liveRows is null) return;
        try { _liveRows.Report(Snapshot()); }
        catch (Exception ex) { Logger.Warn("Live result table update failed; final export is still available.", ex); }
    }

    private void ReportPdfProgress(
        int total,
        int finished,
        int failed,
        int processing,
        string currentFile,
        int currentFilePages = 0,
        int currentFilePagesCompleted = 0,
        double averagePageSeconds = 0,
        double averagePagesPerFile = 0,
        bool currentPageInProgress = false,
        double progressReportedElapsedSeconds = 0)
    {
        if (_pdfProgress is null) return;
        int pending = Math.Max(0, total - finished - processing);
        try
        {
            _pdfProgress.Report(new PdfProcessingProgress(
                total, finished, failed, processing, pending, currentFile,
                currentFilePages, currentFilePagesCompleted,
                averagePageSeconds, averagePagesPerFile,
                currentPageInProgress, progressReportedElapsedSeconds));
        }
        catch (Exception ex) { Logger.Warn("PDF queue progress update failed.", ex); }
    }

    private static List<RuleBasedMaterial> MergeRuleHits(
        IReadOnlyList<RuleBasedMaterial> textHits, IReadOnlyList<RuleBasedMaterial> catalogHits)
    {
        var groups = textHits.Concat(catalogHits)
            .GroupBy(x => MaterialIdentity.Key(x.Name), StringComparer.Ordinal);
        return groups.Select(g => new RuleBasedMaterial(
                g.OrderByDescending(x => x.Name.Length).First().Name,
                Math.Max(1, g.Max(x => x.Occurrences)),
                string.Join(" || ", g.Select(x => x.Evidence)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8))))
            .ToList();
    }

    private static bool IsVisionModelName(string model) =>
        model.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("moondream", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("bakllava", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("vision", StringComparison.OrdinalIgnoreCase);

    private static string BuildFastSystemPrompt() => """
        You are a senior civil/structural construction estimator analysing one complete building drawing sheet.
        Identify every explicitly specified material/product, not just items in the reference catalog. Decompose assemblies into their named components (boards, studs, joists, trusses, decking, toppings, insulation, channels, sheathing, blocking, finishes, fixtures and accessories). Preserve thickness, size, grade, species, treatment, type and finish. Do not report generic dimensions or drafting labels as products.
        Count distinct callout appearances for each same item; use o=1 if it appears once or the count is uncertain. A parenthetical (2) before a layer description means two layers, not two separate drawing callouts. Do not double-count the same item when the same note is repeated in an adjacent view unless it is visibly a separate callout.
        Return q only if a quantity is explicit in a schedule/callout or if a clearly measurable visual count is possible. Never invent quantities. Use b=explicit, schedule, visual_count or not_shown; use u such as EA, LF, SF, CY, SHEET or SET. Mark visual counts as visual_count. If quantity is not shown, q=null.
        The numbered catalog suggestions are advisory only. If an exact equivalence is not certain, use c=0 and put the observed description in t. Keep each distinct specification as its own item.
        LUMBER INSPECTION (do not rely on catalog hits): Inspect every plan, section, wall/floor/roof assembly, schedule and detail for dimensional wood, studs, joists, TJI/I-joists, trusses, rafters, beams, headers, posts, blocking, LVL/LSL/PSL/glulam, rim board, plywood, OSB, CDX, structural panels, T&G decking, fascia, soffit, trim and wood slats. Read nominal and actual sizes, species, grade, lengths and PT/FRT treatments.
        HARDWARE INSPECTION (dedicated pass within this same request): Inspect every connection/detail/schedule and callout for Simpson Strong-Tie or other manufacturer connectors, joist hangers (H/LU/LUS/HUC families), hold-downs (HD/HTT/DTT/PHD families), straps/ties (LSTA/MSTA/CMST/GATR families), post bases/caps, clips, brackets, anchor bolts, expansion anchors, thru bolts, carriage bolts, lag screws, SDS/SDW/THD fasteners, nails, screws, hex nuts, washers, threaded rods, embed plates and shear plates. Treat short model codes (e.g. H3, LU26, A23, TC26, HUCQ..., LUS..., THD..., SDS...) as hardware part references when they appear as callouts. Identify the product by its code and visible description; do not infer an exact SKU from a generic symbol.
        For the image, zoom attention mentally into every connection symbol, leader note, framing intersection, beam/column/post base, ledger, rim joist, shear-wall boundary and schedule row. Hardware may be drawn as a small symbol with a part number; explicitly transcribe that code and its adjacent dimensions/fastener schedule. Do not omit hardware just because the word “hardware” is absent. For lumber, inspect every member label and assembly layer, including wood studs/joists/trusses, blocking, engineered wood, plywood/OSB and decking even when embedded in a wall/floor/roof assembly.
        For each item return k as exactly one of: lumber, hardware, other. Use lumber for dimensional lumber, engineered wood, studs, joists, trusses, rafters, beams, plywood, OSB, sheathing and decking. Use hardware for connectors, hangers, fasteners, bolts, screws, nuts, washers, anchors, straps, clips, brackets and hold-downs. Use other for gypsum board, insulation, concrete toppings, finishes, membranes, sealants and other building products that are not in the two catalogs. Never force an item into lumber or hardware just to fill a column.
        Output compact JSON only: {"s":"sheet number","n":"sheet title","i":[{"c":0,"k":"lumber","t":"observed material specification","o":1,"q":null,"u":"","b":"not_shown","d":"detail reference","e":"short evidence"}]}. Set k to one actual value per item: lumber, hardware, or other. If none, return {"s":"","n":"","i":[]}.
        """;

    private string BuildFastUserPrompt(PageContext ctx, string pageText, bool withImage)
    {
        var sb = new StringBuilder(pageText.Length + 1200);
        sb.Append("DRAWING: ").Append(ctx.FileName).Append(" | PAGE: ").Append(ctx.Page).AppendLine();
        if (ctx.HintNo.Length > 0 || ctx.HintName.Length > 0)
            sb.Append("SHEET HINT: ").Append(ctx.HintNo).Append(' ').Append(ctx.HintName).AppendLine();
        sb.AppendLine("REFERENCE CATALOG MATCH HINTS (not a whitelist):");
        var lumberHints = ctx.Candidates.Where(x => _catalog.Classify(x) == CatalogService.LumberCategory).ToArray();
        var hardwareHints = ctx.Candidates.Where(x => _catalog.Classify(x) == CatalogService.HardwareCategory).ToArray();
        if (lumberHints.Length > 0)
        {
            sb.AppendLine("LUMBER TAKEOFF LIST suggestions:");
            foreach (var item in lumberHints) sb.Append("- ").AppendLine(item);
        }
        if (hardwareHints.Length > 0)
        {
            sb.AppendLine("HARDWARE TAKEOFF LIST suggestions:");
            foreach (var item in hardwareHints) sb.Append("- ").AppendLine(item);
        }
        if (ctx.Candidates.Length == 0) sb.AppendLine("No catalog hints; discover items independently.");
        sb.AppendLine("MANDATORY CLASSIFICATION: actively inspect lumber/wood member descriptions AND structural hardware/connector callouts separately. Hardware may be shown only as a short manufacturer code; do not ignore it because the code is unfamiliar.");
        if (pageText.Length > 0)
            sb.AppendLine("EXTRACTED DRAWING TEXT (local rules scanned the complete original text):").AppendLine(pageText);
        if (withImage)
            sb.AppendLine("Inspect the attached full-sheet image as well as the extracted text. Read callouts, dimensions, schedules, partition/floor/roof assemblies, symbols and all material labels. Return individual material components, not just assembly names.");
        else
            sb.AppendLine("Identify all material components from the extracted drawing text. The source text may be compacted; favour explicit specifications and callouts.");
        return sb.ToString();
    }

    // ====================================================================== producer
    private async Task ExtractFileAsync(int fileIndex, string path, int fileCount,
        ChannelWriter<PageJob> writer, ConcurrentQueue<VisionJob> visionJobs, CancellationToken ct)
    {
        var fname = Path.GetFileName(path);
        string sizeTxt;
        try { sizeTxt = $"{new FileInfo(path).Length / 1048576.0:0.0} MB"; } catch { sizeTxt = "size unknown"; }
        Logger.Info($"--- FILE {fileIndex + 1}/{fileCount}: {fname} ({sizeTxt})");

        PdfSession? session = null;
        try
        {
            var sw = Stopwatch.StartNew();
            session = await PdfSession.OpenAsync(path, TimeSpan.FromSeconds(_s.PdfStepTimeoutSec * 2), ct);
            int pc = session.PageCount;
            Interlocked.Add(ref _totalPages, pc);
            Logger.Info($"    [{fname}] opened: {pc} page(s) in {sw.Elapsed.TotalSeconds:0.0}s");

            for (int p = 1; p <= pc; p++)
            {
                ct.ThrowIfCancellationRequested();
                try { await PreparePageAsync(session, fileIndex, fname, p, writer, visionJobs, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _pagesFailed); MarkDone();
                    Logger.Error($"    [{fname} p{p}] PREPARE FAILED ({ex.GetType().Name}): {ex.Message} -> continuing", ex);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"    FILE FAILED: cannot process '{fname}' ({ex.GetType().Name}): {ex.Message}" +
                         (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? "  (PDF looks encrypted)" : ""), ex);
        }
        finally
        {
            session?.Dispose();
            session = null;
            LogMemory($"file closed: {fname}");
        }
    }

    private async Task PreparePageAsync(PdfSession session, int fileIndex, string fname, int p,
        ChannelWriter<PageJob> writer, ConcurrentQueue<VisionJob> visionJobs, CancellationToken ct)
    {
        var tag = $"[{fname} p{p}/{session.PageCount}]";

        // When a vision model is configured, inspect every sheet image as a second pass,
        // not only fully scanned PDFs. Hybrid CAD PDFs often contain readable text plus
        // important graphical callouts/symbols that text extraction cannot interpret.
        if (_visionStageEnabled)
            visionJobs.Enqueue(new VisionJob(fileIndex, session.FilePath, p));

        // 1) text
        string text = "";
        if (!session.TextBroken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                text = await session.GetTextAsync(p, TimeSpan.FromSeconds(_s.PdfStepTimeoutSec), _s.LayoutAwareText, ct);
                Logger.Info($"  {tag} text {text.Trim().Length} chars in {sw.Elapsed.TotalSeconds:0.0}s");
            }
            catch (TimeoutException tex)
            {
                session.TextBroken = true;
                Logger.Warn($"  {tag} {tex.Message}. Text extraction disabled for the rest of this PDF (very dense CAD content?).");
            }
        }

        // 2) scanned page -> vision queue (processed later)
        if (text.Trim().Length < _s.MinTextChars)
        {
            if (_visionStageEnabled)
            {
                // Complete the text-pass work unit; the visual pass is queued separately.
                MarkDone();
                Logger.Info($"  {tag} no embedded text -> visual analysis queued");
            }
            else
            {
                Interlocked.Increment(ref _pagesSkipped); MarkDone();
                Logger.Warn($"  {tag} SKIPPED: no embedded text (scanned / outlined CAD) and no vision model selected.");
            }
            return;
        }

        // Detect sheet identity once, then run a deterministic callout pass before AI.
        // Small local models often miss abbreviations or split labels in dense CAD exports;
        // the rule pass is additive and does not constrain the open-world model discovery.
        var (hintNo, hintName) = SheetInfoExtractor.Detect(text);
        var sheetLabel = BuildSheetLabel(fname, p, hintNo, hintName);
        var ruleHits = DrawingMaterialExtractor.Extract(text);
        if (ruleHits.Count > 0)
        {
            AddRuleBasedResults(fileIndex, fname, p, sheetLabel, ruleHits);
            Logger.Info($"  {tag} deterministic material pass: {ruleHits.Count} item type(s), " +
                        $"{ruleHits.Sum(x => x.Occurrences)} callout occurrence(s)");
        }

        // 3) Catalog retrieval is advisory only. It provides possible exact equivalents,
        // but must never block analysis of new/unlisted materials or entire sheets.
        var set = _index.Select(text);
        if (set.Names.Count == 0)
        {
            Interlocked.Increment(ref _pagesNoCandidates);
            Logger.Info($"  {tag} no catalog keyword match; broad material discovery will still run");
        }

        // 4) Analyse ALL extracted page text in bounded chunks. Do not send only snippets
        // around known catalog products: that would hide new materials from the model.
        var catalogHints = set.Names.Take(24).ToArray();
        var candidateChars = catalogHints.Sum(n => n.Length + 8);
        var availablePromptTokens = Math.Max(512, _s.NumCtx - _s.NumPredict - 256);
        var contextSafeChars = availablePromptTokens * 3 - _systemPrompt.Length - candidateChars - 512;
        var requestedChunkChars = Math.Max(500, _s.ChunkChars);
        var effectiveChunkChars = Math.Max(500, Math.Min(requestedChunkChars, Math.Max(500, contextSafeChars)));
        var chunks = TextWindows.BuildAll(text, effectiveChunkChars, out var kept);
        Logger.Info($"  {tag} {set.Names.Count} catalog hint(s); sending {kept} of {text.Length} chars in {chunks.Count} chunk(s) (complete text coverage; no catalog gate)" +
                    $"; sheet hint '{hintNo}' '{hintName}'");

        // Retain only unique evidence tokens, not the potentially huge extracted page string.
        // This prevents queued AI jobs from holding the full PdfPig text for many pages.
        var evidenceTokens = Tokens(text).ToHashSet(StringComparer.Ordinal);
        var ctx = new PageContext(fileIndex, fname, p, tag, catalogHints, hintNo, hintName, evidenceTokens);
        var key = ResultCache.Key(_s.TextModel, ResultCache.PromptVersion, string.Join("|", catalogHints), hintNo, hintName,
                                  string.Join("\u0001", chunks));

        // 5) cache
        if (_cache.TryGet(key, out var blob))
        {
            try
            {
                var replies = JsonSerializer.Deserialize<string[]>(blob);
                if (replies is not null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    ApplyReplies(ctx, replies, verified: true);
                    Interlocked.Increment(ref _pagesOk); MarkDone();
                    Logger.Info($"  {tag} served from CACHE (no AI call)");
                    return;
                }
            }
            catch (JsonException) { /* corrupt cache entry -> recompute */ }
        }

        // Release the full extracted text before waiting on the bounded channel.
        text = string.Empty;
        await writer.WriteAsync(new PageJob(ctx, chunks, key), ct);
    }

    private void AddRuleBasedResults(int fileIndex, string fileName, int page, string sheet,
                                     IReadOnlyList<RuleBasedMaterial> materials)
    {
        foreach (var material in materials)
        {
            if (string.IsNullOrWhiteSpace(material.Name)) continue;
            var catalogName = _catalog.Snap(material.Name);
            var itemName = catalogName ?? $"[NOT IN CATALOG] {material.Name}";
            _raw.Enqueue(new TakeoffRow
            {
                Sheet = sheet,
                ItemName = itemName,
                Category = _catalog.Classify(itemName),
                Detail = "",
                Quantity = null,
                QuantityUnit = "",
                QuantityBasis = "Occurrence count (not a measured takeoff quantity)",
                OccurrenceCount = Math.Max(1, material.Occurrences),
                Evidence = material.Evidence,
                Method = "Rule-based text-evidenced",
                SourceFile = fileName,
                Page = page,
                FileIndex = fileIndex,
                Seq = Interlocked.Increment(ref _seq)
            });
        }
    }

    private static string BuildSheetLabel(string fileName, int page, string sheetNo, string sheetName)
    {
        if (sheetNo.Length == 0 && sheetName.Length == 0)
            return $"{Path.GetFileNameWithoutExtension(fileName)} (p{page})";
        return string.Join(" - ", new[] { sheetNo, sheetName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ====================================================================== consumers (LLM)
    private async Task ConsumeAsync(int worker, ChannelReader<PageJob> reader, Lazy<Task> warmUp, CancellationToken ct)
    {
        await foreach (var job in reader.ReadAllAsync(ct))
        {
            await warmUp.Value.ConfigureAwait(false);
            var ctx = job.Ctx;
            try
            {
                var replies = new List<string>();
                for (int i = 0; i < job.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _status.Report($"AI worker {worker}: {ctx.FileName} p{ctx.Page} chunk {i + 1}/{job.Chunks.Count}   ({_donePages}/{_totalPages} pages done)");
                    var user = BuildUserPrompt(ctx, job.Chunks[i], i + 1, job.Chunks.Count, ctx.Candidates);
                    replies.Add(await CallModelAsync(_s.TextModel, user, null, $"{ctx.Tag} w{worker} c{i + 1}/{job.Chunks.Count}", ct));
                }
                bool allParsed = ApplyReplies(ctx, replies, verified: true);
                if (allParsed) await _cache.PutAsync(job.CacheKey, JsonSerializer.Serialize(replies));
                Interlocked.Increment(ref _pagesOk);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pagesFailed);
                Logger.Error($"  {ctx.Tag} AI STEP FAILED ({ex.GetType().Name}): {ex.Message} -> continuing with next page", ex);
            }
            finally { MarkDone(); }
        }
    }

    // ====================================================================== vision stage
    private async Task RunVisionAsync(ConcurrentQueue<VisionJob> jobs, CancellationToken ct)
    {
        // Vision discovery is open-ended too. Do not pass the whole catalog as a whitelist;
        // identified products are mapped to the catalog after the model returns.
        var candidates = Array.Empty<string>();
        // Avoid sorting/copying the full scanned-page queue and process exactly one
        // raster + one vision request at a time to cap unmanaged/GPU memory.
        Logger.Info($"=== VISION STAGE: {jobs.Count} scanned page(s) with '{_s.VisionModel}' | workers 1 | cache {(_s.UseCache ? "on" : "off")}");

        while (jobs.TryDequeue(out var j))
        {
            ct.ThrowIfCancellationRequested();
            var fname = Path.GetFileName(j.FilePath);
            var tag = $"[{fname} p{j.Page} vision]";
            var ctx = new PageContext(j.FileIndex, fname, j.Page, tag, candidates, "", "", null);
            try
            {
                var cacheKey = VisionCacheKey(j, candidates);
                if (_cache.TryGet(cacheKey, out var blob))
                {
                    try
                    {
                        var cached = JsonSerializer.Deserialize<string[]>(blob);
                        if (cached is { Length: > 0 })
                        {
                            ApplyReplies(ctx, cached, verified: false);
                            Interlocked.Increment(ref _cacheHits);
                            Interlocked.Increment(ref _pagesOk);
                            Logger.Info($"  {tag} served from VISION CACHE (no render / AI call)");
                            continue;
                        }
                    }
                    catch (JsonException) { /* corrupt vision cache entry -> recompute */ }
                }

                ct.ThrowIfCancellationRequested();
                _status.Report($"Vision: {fname} p{j.Page}");
                var json = await AnalyzeVisionPageAsync(j, ctx, tag, ct);
                if (ApplyReplies(ctx, new[] { json }, verified: false))
                    await _cache.PutAsync(cacheKey, JsonSerializer.Serialize(new[] { json }));
                Interlocked.Increment(ref _pagesOk);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pagesFailed);
                Logger.Error($"  {tag} FAILED ({ex.GetType().Name}): {ex.Message}", ex);
            }
            finally
            {
                MarkDone();
                LogMemory($"vision page complete: {fname} p{j.Page}");
            }
        }
    }

    private async Task<string> AnalyzeVisionPageAsync(VisionJob job, PageContext ctx, string tag, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var png = await PdfRenderer.RenderPngAsync(job.FilePath, job.Page,
            TimeSpan.FromSeconds(_s.PdfStepTimeoutSec * 2), ct, _s.RenderMaxSidePx);
        Logger.Info($"  {tag} rendered {png.Length / 1024} KB in {sw.Elapsed.TotalSeconds:0.0}s");

        var imageBase64 = Convert.ToBase64String(png);
        // Do not retain the full raster while the HTTP JSON body is being serialized.
        png = Array.Empty<byte>();
        var user = BuildUserPrompt(ctx, "(see attached image of the drawing sheet)", 1, 1, ctx.Candidates);
        return await CallModelAsync(_s.VisionModel!, user,
            new[] { imageBase64 }, tag, ct, extraTokens: 1000, maxOutputTokens: 512);
    }

    private static void LogMemory(string stage)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var managed = GC.GetTotalMemory(false) / 1048576.0;
            var working = process.WorkingSet64 / 1048576.0;
            var privateBytes = process.PrivateMemorySize64 / 1048576.0;
            Logger.Info($"MEMORY [{stage}]: working set {working:0} MB | private {privateBytes:0} MB | managed heap {managed:0} MB");
        }
        catch { /* diagnostics must not interrupt takeoff */ }
    }

    private string VisionCacheKey(VisionJob job, string[] candidates)
    {
        try
        {
            var info = new FileInfo(job.FilePath);
            return ResultCache.Key(_s.VisionModel ?? "", ResultCache.PromptVersion, "vision-v1", Path.GetFullPath(job.FilePath),
                info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString(), job.Page.ToString(),
                string.Join("|", candidates));
        }
        catch
        {
            return ResultCache.Key(_s.VisionModel ?? "", ResultCache.PromptVersion, "vision-v1", job.FilePath, job.Page.ToString(),
                string.Join("|", candidates));
        }
    }

    // ====================================================================== model call
    private async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            Logger.Info($"Warm-up: loading '{_s.TextModel}' before AI extraction...");
            await _llm.ChatAsync(_s.TextModel, _systemPrompt, "Reply with {}", null, _s.NumCtx, 1, true,
                TimeSpan.FromSeconds(_s.FirstTokenTimeoutSec), TimeSpan.FromSeconds(_s.IdleTimeoutSec), ct);
            Logger.Info($"Warm-up done in {sw.Elapsed.TotalSeconds:0.0}s - model is resident in memory.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Logger.Warn("Warm-up failed (continuing without it)", ex); }
    }

    private async Task<string> CallModelAsync(string model, string user, string[]? images, string tag,
                                              CancellationToken ct, int extraTokens = 0, int? maxOutputTokens = null)
    {
        int outputLimit = maxOutputTokens ?? _s.NumPredict;
        int est = (_systemPrompt.Length + user.Length) / 3 + extraTokens;
        int need = est + outputLimit + 256;
        if (need > _s.NumCtx)
            Logger.Warn($"      {tag}: estimated prompt/output ({need} tokens) exceeds fixed context {_s.NumCtx}; consider lowering Chunk chars or raising Context before the run. Context will NOT change mid-run.");
        Logger.Info($"      -> {tag}: '{model}' prompt ~{est} tokens | output cap {outputLimit} | fixed ctx {_s.NumCtx}");

        Interlocked.Increment(ref _llmCalls);
        var stats = await _llm.ChatAsync(model, _systemPrompt, user, images, _s.NumCtx, outputLimit, json: true,
            TimeSpan.FromSeconds(_s.FirstTokenTimeoutSec), TimeSpan.FromSeconds(_s.IdleTimeoutSec), ct);

        double tps = stats.OutputTokens / Math.Max(0.01, stats.TotalSec - stats.LoadSec);
        Logger.Info($"      <- {tag}: {stats.OutputTokens} tokens in {stats.TotalSec:0.0}s (load {stats.LoadSec:0.0}s, prompt {stats.PromptTokens} tok, ~{tps:0.0} tok/s)");
        if (stats.PromptTokens > 0 && stats.PromptTokens >= _s.NumCtx - 64)
            Logger.Warn($"      {tag}: prompt filled the whole context window - text may be truncated! Increase Context or lower Chunk chars.");
        if (stats.Truncated)
            Logger.Warn($"      {tag}: reply hit the {_s.NumPredict}-token limit (JSON may be cut off; will try to repair).");
        if (tps < 3 && stats.OutputTokens > 20)
            Logger.Warn("      model is very slow on this PC (<3 tok/s). Try a smaller quantized model (for example qwen2.5:3b) or enable GPU in Ollama.");

        Logger.Debug($"RAW REPLY {tag}:\n{stats.Content}");
        return stats.Content;
    }

    // ====================================================================== verify + collect
    private bool ApplyReplies(PageContext ctx, IReadOnlyList<string> replies, bool verified)
    {
        bool allParsed = true;
        var results = new List<PageResult>();
        foreach (var r in replies)
        {
            var pr = Parse(r, ctx.Tag);
            if (pr is null) allParsed = false; else results.Add(pr);
        }

        var sheetNo = results.Select(x => x.SheetNumber).LastOrDefault(s => s.Length > 0) ?? ctx.HintNo;
        var sheetName = results.Select(x => x.SheetName).LastOrDefault(s => s.Length > 0) ?? ctx.HintName;
        var sheet = BuildSheetLabel(ctx.FileName, ctx.Page, sheetNo, sheetName);
        if (sheetNo.Length == 0 && sheetName.Length == 0) Logger.Warn($"  {ctx.Tag} title block not recognised, using file/page as sheet name");

        HashSet<string>? tokens = verified ? ctx.EvidenceTokens : null;
        int found = 0, kept = 0;

        foreach (var it in results.SelectMany(r => r.Items))
        {
            found++;
            string? name = null;

            // Prefer a canonical snap from the model's full item description. Candidate
            // numbers are only fallback hints and are not allowed to override conflicting text.
            if (!string.IsNullOrWhiteSpace(it.Text)) name = _catalog.Snap(it.Text);
            if (name is null && string.IsNullOrWhiteSpace(it.Text) &&
                it.Candidate >= 1 && it.Candidate <= ctx.Candidates.Length)
                name = ctx.Candidates[it.Candidate - 1];

            if (name is null && string.IsNullOrWhiteSpace(it.Text))
            { Logger.Warn("      x ignored an empty model item; no usable item name was returned"); continue; }
            if (name is null && !_s.KeepUnmatched)
            { Logger.Info($"      x dropped by user setting (not in catalog): '{it.Text}'"); continue; }
            if (verified && !EvidenceFound(it.Evidence, tokens!))
            { Logger.Info($"      x dropped, evidence not on sheet: '{name ?? it.Text}' / '{it.Evidence}'"); continue; }

            decimal? qty = it.Quantity;
            var basis = string.IsNullOrWhiteSpace(it.QuantityBasis) ? (qty.HasValue ? "explicit" : "not_shown") : it.QuantityBasis.Trim().ToLowerInvariant();
            var unit = (it.Unit ?? string.Empty).Trim().ToUpperInvariant();

            if (qty.HasValue && verified)
            {
                // Text-only extraction cannot visually count symbols. A visual count may
                // only be provided by the vision stage and remains explicitly unverified.
                if (basis == "visual_count")
                {
                    Logger.Warn($"      ! visual count is not supported by text evidence for '{name ?? it.Text}', quantity set to n/s");
                    qty = null;
                }
                else if (basis == "calculated")
                {
                    // Calculations are retained only when the model cites dimensional evidence.
                    if (string.IsNullOrWhiteSpace(it.Evidence) || !it.Evidence.Any(char.IsDigit))
                    {
                        Logger.Warn($"      ! calculated quantity has no dimensional evidence for '{name ?? it.Text}', set to n/s");
                        qty = null;
                    }
                }
                else if (!QuantityAppearsInEvidence(qty.Value, it.Evidence))
                {
                    Logger.Warn($"      ! quantity {FormatQuantity(qty.Value)} not present in evidence for '{name ?? it.Text}', set to n/s");
                    qty = null;
                }
            }

            if (qty.HasValue && unit.Length == 0)
                unit = basis == "calculated" ? "UNSPECIFIED" : "EA";

            var itemName = name ?? $"[NOT IN CATALOG] {it.Text}";
            var method = name is null
                ? (verified ? "Text-evidenced; Not-in-catalog" : "Vision-unverified; Not-in-catalog")
                : !verified ? "Multimodal-unverified" : basis == "calculated" ? "Text-calculated - verify" : "Text-evidenced";
            var basisLabel = basis switch
            {
                "explicit" => "Explicit callout",
                "schedule" => "Schedule",
                "calculated" => "Calculated from stated dimensions - verify",
                "visual_count" => "Visual count - verify",
                _ => qty.HasValue ? basis : "Quantity not stated"
            };

            _raw.Enqueue(new TakeoffRow
            {
                Sheet = sheet,
                ItemName = itemName,
                Category = _catalog.Classify(itemName, it.Category),
                Detail = it.Detail,
                Quantity = qty,
                QuantityUnit = unit,
                QuantityBasis = basisLabel,
                OccurrenceCount = Math.Clamp(it.Occurrences, 1, 1000),
                Evidence = it.Evidence,
                Method = method,
                SourceFile = ctx.FileName,
                Page = ctx.Page,
                FileIndex = ctx.FileIndex,
                Seq = Interlocked.Increment(ref _seq)
            });
            Logger.Info($"      + {itemName} | qty {(qty.HasValue ? FormatQuantity(qty.Value) + (unit.Length > 0 ? " " + unit : "") : "n/s")} | basis '{basisLabel}' | detail '{it.Detail}'");
            kept++;
        }
        Logger.Info($"  {ctx.Tag} DONE: sheet '{sheet}', model proposed {found}, kept {kept}");
        return allParsed;
    }

    private static bool QuantityAppearsInEvidence(decimal quantity, string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        var q = FormatQuantity(quantity);
        var pattern = $"(?<![A-Z0-9./-]){System.Text.RegularExpressions.Regex.Escape(q)}(?![A-Z0-9./-])";
        return System.Text.RegularExpressions.Regex.IsMatch(evidence, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private void MarkDone()
    {
        int d = Interlocked.Increment(ref _donePages);
        int stagesPerPage = _visionStageEnabled ? 2 : 1;
        _progress.Report(Math.Min(1.0, d / (double)Math.Max(1, _totalPages * stagesPerPage)));
    }

    // ====================================================================== merge
    private List<TakeoffRow> Merge(List<TakeoffRow> raw)
    {
        // First reconcile model, deterministic-rule and vision findings on each source
        // page. Rule/text/vision describe the same drawing, so their occurrence counts are
        // compared with MAX rather than added together (prevents double counting).
        var pageRows = new List<TakeoffRow>();
        var validRows = raw.Where(r => !string.IsNullOrWhiteSpace(r.ItemName) &&
                                       MaterialIdentity.Key(r.ItemName).Length > 0);

        foreach (var pageGroup in validRows.GroupBy(r =>
                     (r.FileIndex, r.Page, Key: MaterialIdentity.Key(r.ItemName))))
        {
            var entries = pageGroup.ToList();
            var ruleEntries = entries.Where(r => r.Method.StartsWith("Rule-based", StringComparison.OrdinalIgnoreCase)).ToList();
            // All non-rule rows are model findings. This deliberately includes
            // "Multimodal-unverified" results from the single-pass vision model.
            var modelEntries = entries.Where(r => !r.Method.StartsWith("Rule-based", StringComparison.OrdinalIgnoreCase)).ToList();
            var textEntries = modelEntries.Where(r => r.Method.Contains("Text", StringComparison.OrdinalIgnoreCase)).ToList();

            int ruleCount = CountOccurrences(ruleEntries);
            int modelCount = CountOccurrences(modelEntries);
            int pageOccurrences = Math.Max(ruleCount, modelCount);
            if (pageOccurrences < 1) pageOccurrences = 1;

            // Prefer evidence-checked text quantities; otherwise retain model quantities
            // (vision-derived quantities remain visibly marked unverified in the audit).
            var textQty = UniqueQuantityRows(textEntries);
            var selectedQty = textQty.Count > 0 ? textQty : UniqueQuantityRows(modelEntries);
            decimal? pageQuantity = null;
            string pageUnit = "";
            string pageBasis = "";
            var first = entries.OrderBy(r => r.Seq).First();
            string pageQuantitySummary = "";
            if (selectedQty.Count > 0)
            {
                var units = selectedQty.Select(x => x.QuantityUnit).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (units.Count <= 1)
                {
                    pageQuantity = selectedQty.Sum(x => x.Quantity ?? 0m);
                    pageUnit = units.FirstOrDefault() ?? "";
                    pageQuantitySummary = $"{first.Sheet}: {FormatQuantity(pageQuantity.Value)}{(pageUnit.Length > 0 ? " " + pageUnit : "")}";
                }
                else
                {
                    pageBasis = "Multiple units; quantities retained separately";
                    pageQuantitySummary = string.Join(", ", selectedQty.Select(x =>
                        $"{FormatQuantity(x.Quantity!.Value)}{(x.QuantityUnit.Length > 0 ? " " + x.QuantityUnit : "")}").Distinct());
                }
                if (pageBasis.Length == 0)
                    pageBasis = string.Join(" + ", selectedQty.Select(x => x.QuantityBasis)
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            }

            var details = entries.Select(r => r.Detail).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var evidence = entries.Select(r => r.Evidence).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            var itemName = entries.OrderBy(r => r.ItemName.StartsWith("[NOT IN CATALOG]", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(r => r.ItemName.Length).First().ItemName;

            pageRows.Add(new TakeoffRow
            {
                Sheet = first.Sheet,
                ItemName = itemName,
                Category = _catalog.Classify(itemName, entries.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()),
                Detail = string.Join(", ", details),
                Quantity = pageQuantity,
                QuantityUnit = pageUnit,
                QuantityBasis = pageBasis,
                QuantitySummary = pageQuantitySummary,
                OccurrenceCount = Math.Max(1, pageOccurrences),
                Evidence = string.Join(" || ", evidence),
                Method = string.Join(" + ", entries.Select(r => r.Method).Distinct()),
                SourceFile = first.SourceFile,
                SourceReferences = $"{first.Sheet} | {first.SourceFile} p{first.Page}",
                Page = first.Page,
                FileIndex = first.FileIndex,
                Seq = entries.Min(r => r.Seq)
            });
        }

        // Then consolidate the same material specification across ALL drawings/pages.
        // The no. of items defaults to 1 and is incremented by distinct page callouts.
        var merged = new List<TakeoffRow>();
        foreach (var itemGroup in pageRows.GroupBy(r => MaterialIdentity.Key(r.ItemName), StringComparer.Ordinal))
        {
            var pages = itemGroup.OrderBy(r => r.FileIndex).ThenBy(r => r.Page).ThenBy(r => r.Seq).ToList();
            var first = pages[0];
            var pageQuantities = pages.Where(r => !string.IsNullOrWhiteSpace(r.QuantitySummary))
                .Select(r => r.QuantitySummary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var numericPages = pages.Where(r => r.Quantity.HasValue).ToList();
            decimal? singleQuantity = numericPages.Count == 1 ? numericPages[0].Quantity : null;
            string singleUnit = numericPages.Count == 1 ? numericPages[0].QuantityUnit : "";

            var detailRefs = pages.SelectMany(r => r.Detail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sheetNames = pages.Select(r => r.Sheet).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sourceRefs = pages.SelectMany(r => string.IsNullOrWhiteSpace(r.SourceReferences)
                    ? new[] { $"{r.Sheet} | {r.SourceFile} p{r.Page}" }
                    : r.SourceReferences.Split(" || ", StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var allEvidence = pages.SelectMany(r => r.Evidence.Split(" || ", StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();

            var finalItemName = pages.OrderBy(r => r.ItemName.StartsWith("[NOT IN CATALOG]", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(r => r.ItemName.Length).First().ItemName;
            var finalCategoryHint = pages.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            merged.Add(new TakeoffRow
            {
                Sheet = string.Join("; ", sheetNames),
                ItemName = finalItemName,
                Category = _catalog.Classify(finalItemName, finalCategoryHint),
                Detail = string.Join(", ", detailRefs),
                Quantity = singleQuantity,
                QuantityUnit = singleUnit,
                QuantityBasis = string.Join("; ", pages.Select(r => r.QuantityBasis)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)),
                QuantitySummary = string.Join("; ", pageQuantities),
                OccurrenceCount = Math.Max(1, pages.Sum(r => Math.Max(1, r.OccurrenceCount))),
                Evidence = string.Join(" || ", allEvidence),
                Method = string.Join(" + ", pages.Select(r => r.Method).Distinct()),
                SourceFile = string.Join("; ", pages.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase)),
                SourceReferences = string.Join(" || ", sourceRefs),
                Page = first.Page,
                FileIndex = first.FileIndex,
                Seq = pages.Min(r => r.Seq)
            });
        }

        merged = merged.OrderBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        int n = 1;
        foreach (var r in merged)
        {
            r.SlNo = n++;
            // User-facing count always has a numeric default of 1. Repeated appearances
            // of the same normalized item increment this count; it is not a measured
            // procurement quantity unless the drawing also supplies an explicit quantity.
            var parts = new List<string> { Math.Max(1, r.OccurrenceCount).ToString(CultureInfo.InvariantCulture) };
            if (r.Detail.Length > 0) parts.Add($"Details: {r.Detail}");
            if (r.QuantitySummary.Length > 0) parts.Add($"Drawing quantity: {r.QuantitySummary}");
            r.DetailQty = string.Join(" | ", parts);
        }
        return merged;
    }

    private static int CountOccurrences(List<TakeoffRow> rows)
    {
        if (rows.Count == 0) return 0;
        return rows
            .GroupBy(r =>
            {
                var key = MaterialIdentity.EvidenceKey(r.Evidence);
                return key.Length == 0 ? $"SEQ:{r.Seq}" : key;
            }, StringComparer.Ordinal)
            .Sum(g => Math.Max(1, g.Max(r => r.OccurrenceCount)));
    }

    private static List<TakeoffRow> UniqueQuantityRows(List<TakeoffRow> rows) => rows
        .Where(r => r.Quantity.HasValue)
        .GroupBy(r => (r.Quantity, Unit: r.QuantityUnit.ToUpperInvariant(), Evidence: MaterialIdentity.EvidenceKey(r.Evidence)))
        .Select(g => g.First())
        .ToList();

    // ====================================================================== prompts
    private static string BuildSystemPrompt() => """
        You are an expert construction drawing material-identification and quantity-takeoff analyst
        with senior civil/structural estimator-level knowledge of wood framing, engineered wood,
        sheathing, light-gauge steel, connectors, roofing, envelope products and general building materials.
        Analyse the supplied excerpt from ONE drawing sheet as an open-world material discovery task.

        CORE OBJECTIVE
        Find EVERY distinct construction material/product that is actually specified, called out,
        scheduled or clearly represented in this sheet excerpt. Do not limit discovery to the reference
        catalog. Use your engineering vocabulary to recognise synonyms, abbreviations, dimensions,
        species, grades, treatments, product families and common drawing notation.

        REFERENCE CATALOG
        The numbered candidate list in the user message is only a set of possible purchasing/catalog
        equivalents. It is NOT a whitelist and may be empty or incomplete. Identify other materials too.
        Use c only when a listed candidate is an exact equivalent in all known critical attributes.
        Otherwise set c=0 and put the full observed item description in t. Never force a near match.
        Preserve size, thickness, width, depth, length, species (SYP/SPF/DFL/CEDAR etc.), grade (#1/#2),
        pressure treatment (PT), fire treatment (FRT), engineered product type (LVL/LSL/PSL/Glulam),
        panel type (CDX/OSB/Structural 1/T&G), finish and other specified properties.
        Do not invent missing lengths, grades, species, treatment or product properties to fit the catalog.

        IDENTIFICATION RULES
        1. Include actual specified materials: lumber, studs, joists, rafters, beams, posts, headers,
           engineered wood, sheathing/panels, decking, trim, fascia, soffit, steel members/straps,
           connectors/anchors/fasteners, roofing, underlayment, housewrap, sill sealer, adhesives,
           insulation, concrete/reinforcement, masonry, drywall and other building products when present.
        2. Include items shown in schedules, keynotes, material notes, member callouts and detail notes.
           Use surrounding context to distinguish a material from a dimension, gridline, room label,
           existing condition or generic drafting annotation. Do not report dimensions or symbols alone.
        3. Do not omit a material just because it is absent from the candidate list. Discover the
           individual components inside assembly descriptions (board, stud, joist/truss, decking,
           topping, insulation, channel, sheathing, blocking, sealant, finish and accessories).
           One item object per distinct material specification in the chunk.
        4. For each item, o is the number of separate callout/label occurrences for that same item
           in this chunk. Use o=1 by default when it appears once or when unsure; increment only
           when the same material is clearly repeated. Parenthetical layer notation such as (2)
           describes assembly layers and must remain in the evidence, not inflate the item appearance count.
        5. Keep the item's description specific enough for procurement. Include all stated dimensions,
           length, species, grade, treatment, type and finish. If some attribute is not stated, leave it
           unstated; do not guess a catalog stock length.

        QUANTITY RULES - VERY IMPORTANT
        6. q is a numeric quantity only when shown in a schedule/callout (e.g. QTY 12, (4), 12 EA),
           or when a clear, explicitly dimensioned calculation is directly supported by evidence.
           Never infer piece count from a member size or assume a standard stock length.
        7. u is the unit, e.g. EA, LF, SF, SQ FT, SY, CF, CY, LB, TON, SHEET, ROLL or SET.
           If a count is explicit but no unit is printed, use EA. If no numeric quantity is supported,
           set q=null and b="not_shown"; the application will show Qty not stated.
        8. b must be one of: "explicit", "schedule", "calculated", "visual_count", "not_shown".
           Use "calculated" only when the drawing supplies the dimensions needed for the calculation;
           evidence must quote those dimensions. Use "visual_count" only when analysing an image and
           every counted instance is clearly distinct. Never visually estimate obscured/repeated symbols.
        9. Do not invent or round quantities. Do not multiply across separate views if that could double count.
           If the same item is shown in a plan and a detail, do not count both representations twice.

        EVIDENCE AND OUTPUT
        10. e must be a short verbatim quote (maximum 100 characters) from the supplied text. For image
           analysis, describe the visible callout/schedule evidence briefly and mark the result unverified.
        11. d is a detail/callout such as 5/S-501, DETAIL 3, or a schedule identifier; empty if none.
        12. s and n are the sheet number and sheet title from the title block if visible; otherwise empty.
        13. Do not return only an assembly name when the assembly text names its components; emit
            separate material objects for each clearly specified component (for example gypsum board,
            wood stud, TJI truss, T&G plywood deck, gypcrete topping, batt insulation and resilient channel).
        14. Output compact JSON only, no markdown, no commentary, no invented fields. Use exactly:
        {"s":"","n":"","i":[{"c":0,"t":"2X6-16' SYP #2","o":2,"q":24,"u":"EA","b":"schedule","d":"5/S-501","e":"QTY 24 - 2X6-16' SYP #2"}]}
        If there are no identifiable material items in this excerpt, return {"s":"","n":"","i":[]}.
        """;

    private static string BuildUserPrompt(PageContext ctx, string chunk, int part, int parts, string[] candidates)
    {
        var sb = new StringBuilder(candidates.Length * 24 + chunk.Length + 320);
        sb.Append("SHEET FILE: ").Append(ctx.FileName).Append("  PAGE: ").Append(ctx.Page).Append('\n');
        sb.Append("SHEET HINT: ").Append(ctx.HintNo.Length + ctx.HintName.Length == 0
            ? "not detected - read it from the title block if present"
            : $"number='{ctx.HintNo}' name='{ctx.HintName}'").Append("\n\nREFERENCE CATALOG MATCH HINTS (NOT EXHAUSTIVE):\n");
        if (candidates.Length == 0) sb.Append("No exact lexical catalog hints found. Discover materials independently.\n");
        for (int i = 0; i < candidates.Length; i++) sb.Append(i + 1).Append('|').Append(candidates[i]).Append('\n');
        if (chunk.StartsWith("(see attached image", StringComparison.OrdinalIgnoreCase))
            sb.Append("\nDRAWING IMAGE: visually inspect the complete sheet, including schedules, labels, member callouts and material symbols.\n").Append(chunk);
        else
            sb.Append("\nCOMPLETE PAGE TEXT CHUNK ").Append(part).Append('/').Append(parts).Append(" (all chunks together cover the extracted page text):\n").Append(chunk);
        return sb.ToString();
    }

    // ====================================================================== helpers
    private static IEnumerable<string> Tokens(string s) =>
        Rx.Token().Matches((s ?? "").ToUpperInvariant()).Select(m => m.Value);

    private static bool EvidenceFound(string evidence, HashSet<string> pageTokens)
    {
        var t = Tokens(evidence).ToList();
        return t.Count > 0 && t.Count(pageTokens.Contains) / (double)t.Count >= 0.7;
    }

    private static PageResult? Parse(string json, string tag)
    {
        var r = TryParse(json);
        if (r is null)
        {
            var idx = json.LastIndexOf('}');
            if (idx > 0) r = TryParse(json[..(idx + 1)] + "]}");
            if (r is not null) Logger.Warn($"      {tag}: JSON was incomplete, repaired ({r.Items.Count} item(s) recovered)");
        }
        if (r is null)
            Logger.Warn($"      {tag}: model did not return valid JSON. Reply start: " +
                        (json.Length > 300 ? json[..300] + "..." : json).Replace('\n', ' '));
        return r;
    }

    private static PageResult? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var items = new List<ExtractedItem>();
            if (root.TryGetProperty("i", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    items.Add(new ExtractedItem(Int(e, "c") ?? 0, Str(e, "t"), Decimal(e, "q"), Str(e, "u"),
                        Str(e, "d"), Str(e, "e"), Str(e, "b"), Math.Clamp(Int(e, "o") ?? 1, 1, 1000), Str(e, "k")));
                }
            return new PageResult(Str(root, "s"), Str(root, "n"), items);
        }
        catch (JsonException) { return null; }
    }

    private static int? Int(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static decimal? Decimal(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number)) return number;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";
}
