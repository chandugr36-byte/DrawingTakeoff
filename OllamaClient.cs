using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DrawingTakeoff;

public sealed record OllamaStats(string Content, int PromptTokens, int OutputTokens,
                                 double TotalSec, double LoadSec, bool Truncated);

/// <summary>
/// Ollama REST client using STREAMING so we can (a) log progress, (b) detect stalls and
/// (c) enforce time-outs. Logs through <see cref="Logger"/>.
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public OllamaClient(string baseUrl)
    {
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var resp = await _http.GetAsync("api/tags", cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("models").EnumerateArray()
                      .Select(m => m.GetProperty("name").GetString() ?? "")
                      .Where(n => n.Length > 0).OrderBy(n => n).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama at {BaseUrl} did not answer /api/tags within 8 s.");
        }
    }

    public static bool ModelExists(IEnumerable<string> installed, string name) =>
        installed.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || m.Equals(name + ":latest", StringComparison.OrdinalIgnoreCase)
                        || (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
                            && m.Equals(name[..^7], StringComparison.OrdinalIgnoreCase)));

    public async Task UnloadModelAsync(string model, CancellationToken ct = default)
    {
        // Ollama supports keep_alive: 0 to unload an idle model and release its RAM/VRAM.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model, keep_alive = 0 });
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _http.PostAsync("api/generate", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama unload request failed ({(int)response.StatusCode}): {error}");
        }
    }

    public async Task<OllamaStats> ChatAsync(string model, string system, string user, string[]? imagesBase64,
        int numCtx, int numPredict, bool json, TimeSpan firstTokenTimeout, TimeSpan idleTimeout, CancellationToken ct)
    {
        object userMsg = imagesBase64 is null
            ? new { role = "user", content = user }
            : new { role = "user", content = user, images = imagesBase64 };

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["stream"] = true,
            ["keep_alive"] = "5m",
            ["messages"] = new object[] { new { role = "system", content = system }, userMsg },
            // num_predict caps runaway generation (JSON mode can loop on whitespace forever)
            ["options"] = new { temperature = 0, num_ctx = numCtx, num_predict = numPredict }
        };
        if (json) body["format"] = "json";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(firstTokenTimeout);

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        int tokens = 0;
        bool gotFirst = false;
        int promptTokens = 0; double totalSec = 0, loadSec = 0; bool truncated = false;

        // Heartbeat: proves the app is alive while Ollama loads the model / reads the prompt.
        using var hbStop = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(5000, hbStop.Token);
                    if (tokens == 0)
                        Logger.Info($"      ... {sw.Elapsed.TotalSeconds:0}s waiting for first token (model loading / reading prompt; limit {firstTokenTimeout.TotalSeconds:0}s)");
                    else
                        Logger.Info($"      ... {sw.Elapsed.TotalSeconds:0}s, {tokens} tokens generated so far");
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(body);
            using var requestContent = new ByteArrayContent(requestBytes);
            requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = requestContent };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                throw new HttpRequestException($"Ollama HTTP {(int)resp.StatusCode}: {err}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var er))
                    throw new InvalidOperationException("Ollama error: " + er.GetString());

                if (root.TryGetProperty("message", out var msg))
                {
                    var piece = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    var think = msg.TryGetProperty("thinking", out var t) ? t.GetString() ?? "" : "";
                    if (piece.Length > 0 || think.Length > 0)
                    {
                        sb.Append(piece); tokens++;
                        if (!gotFirst) { gotFirst = true; Logger.Info($"      first token after {sw.Elapsed.TotalSeconds:0.0}s"); }
                        cts.CancelAfter(idleTimeout);          // reset stall timer
                    }
                }

                if (root.TryGetProperty("done", out var d) && d.GetBoolean())
                {
                    if (root.TryGetProperty("prompt_eval_count", out var pe)) promptTokens = pe.GetInt32();
                    if (root.TryGetProperty("eval_count", out var ec)) tokens = ec.GetInt32();
                    if (root.TryGetProperty("total_duration", out var td)) totalSec = td.GetInt64() / 1e9;
                    if (root.TryGetProperty("load_duration", out var ld)) loadSec = ld.GetInt64() / 1e9;
                    if (root.TryGetProperty("done_reason", out var dr)) truncated = dr.GetString() == "length";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var what = gotFirst ? $"stalled (no tokens for {idleTimeout.TotalSeconds:0}s)"
                                : $"no first token within {firstTokenTimeout.TotalSeconds:0}s";
            throw new TimeoutException($"Ollama model '{model}' {what}. Model may be too big/slow for this PC, or the prompt too long.");
        }
        finally
        {
            hbStop.Cancel();
            try { await heartbeat; } catch { }
        }

        if (totalSec == 0) totalSec = sw.Elapsed.TotalSeconds;
        return new OllamaStats(sb.ToString(), promptTokens, tokens, totalSec, loadSec, truncated);
    }
}
