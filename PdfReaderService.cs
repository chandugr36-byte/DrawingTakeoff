using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PDFtoImage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DrawingTakeoff;

internal static class Timeouts
{
    /// <summary>Runs synchronous PDF work without leaving abandoned background tasks.</summary>
    public static async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct, string what)
    {
        // PdfPig/PDFtoImage execute synchronous native/CPU work and cannot be cancelled
        // safely once started. The previous timeout abandoned the worker task, allowing
        // it to keep parsing/rendering in the background while the next file started.
        // That created unbounded concurrent work and memory growth. Await completion,
        // then report that the configured limit was exceeded; never orphan the worker.
        ct.ThrowIfCancellationRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(work, CancellationToken.None);
        T result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            if (result is IDisposable cancelledResult) cancelledResult.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        if (sw.Elapsed > timeout)
        {
            if (result is IDisposable slowResult) slowResult.Dispose();
            throw new TimeoutException($"{what} took {sw.Elapsed.TotalSeconds:0}s (configured warning limit {timeout.TotalSeconds:0}s). The operation was allowed to finish safely; no background parser task was left running.");
        }
        return result;
    }
}

/// <summary>One opened PDF (text extraction only; no rendering state).</summary>
public sealed class PdfSession : IDisposable
{
    private readonly PdfDocument _doc;
    public string FilePath { get; }
    public int PageCount => _doc.NumberOfPages;
    /// <summary>Set when text extraction timed out once; further text extraction is skipped for this file.</summary>
    public bool TextBroken { get; set; }

    private PdfSession(string path, PdfDocument doc) { FilePath = path; _doc = doc; }

    public static async Task<PdfSession> OpenAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var doc = await Timeouts.RunAsync(() => PdfDocument.Open(path,
                      new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }),
                      timeout, ct, "Opening PDF");
        return new PdfSession(path, doc);
    }

    public Task<string> GetTextAsync(int pageNumber, TimeSpan timeout, bool layoutAware, CancellationToken ct) =>
        Timeouts.RunAsync(() =>
        {
            var p = _doc.GetPage(pageNumber);
            return layoutAware ? (ContentOrderTextExtractor.GetText(p) ?? "") : (p.Text ?? "");
        }, timeout, ct, $"Text extraction page {pageNumber}");

    public void Dispose() { try { _doc.Dispose(); } catch { } }
}

/// <summary>Page rasteriser for scanned sheets (independent of PdfPig). Longest side is capped.</summary>
public static class PdfRenderer
{
    public static Task<byte[]> RenderPngAsync(string path, int pageNumber, TimeSpan timeout, CancellationToken ct, int maxSidePx = 1400) =>
        Timeouts.RunAsync(() => Render(path, pageNumber, maxSidePx), timeout, ct, $"Rendering page {pageNumber}");

    private static byte[] Render(string path, int pageNumber, int maxSidePx)
    {
        using var pdf = File.OpenRead(path);
        RenderOptions opt;
        try
        {
            var size = Conversion.GetPageSize(pdf, page: pageNumber - 1);
            pdf.Position = 0;
            var scale = maxSidePx / Math.Max(size.Width, size.Height);
            opt = new RenderOptions(Width: Math.Max(1, (int)(size.Width * scale)),
                                    Height: Math.Max(1, (int)(size.Height * scale)));
        }
        catch
        {
            pdf.Position = 0;
            opt = new RenderOptions(Dpi: 96);
        }
        using var ms = new MemoryStream();
        Conversion.SavePng(ms, pdf, page: pageNumber - 1, options: opt);
        return ms.ToArray();
    }
}
