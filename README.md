# DrawingTakeoff – Fast single-pass WPF edition

A Windows WPF desktop application for construction drawing material identification and quantity takeoff using local Ollama models. The updated reference workbook provides two separate catalogs: `Lumber takeoff list` (3,117 unique normalized entries) and `Hardware takeoff list` (2,759 unique normalized entries). These are matching/classification references, not a whitelist. Duplicate normalized descriptions are removed, and a standalone `0` placeholder is ignored. Open-world material discovery and the deterministic terminology extractor remain enabled.

## What changed in this edition

### Faster per-page workflow

- **One AI request per PDF page.** The previous workflow could send many text chunks to a text model and then send the same sheet again to a vision model. Fast mode replaces both stages with a single request.
- When a vision-capable model is selected, the request includes the **drawing image and extracted PDF text together**. This is the recommended mode for diagram sheets.
- When a text-only model is selected, the request contains one compact text excerpt. The local deterministic material rules still scan the complete extracted page text before the model call.
- Removed the additional warm-up request. The first real page request loads the model.
- Repeated runs use a disk cache keyed by model, file metadata, page, prompt version and compact text. Unchanged pages can be returned without another model request.
- Long PDF text is compacted and relevant callout/schedule lines are prioritised for the single model prompt. The full original text remains available to the deterministic extractor.

### Live results instead of waiting until the end

- Rule-based material findings are immediately published to the WPF results grid as soon as a page is extracted.
- AI findings are published to the same grid as soon as that page's model response is parsed.
- The grid is consolidated as the run proceeds; the app does not keep a list of page chunks or token prompts for later display.
- The current page text, image and prompt are released after processing. Only extracted result rows/evidence and the disk cache remain.
- Cancel preserves partial results already shown in the grid.

### Simplified WPF UI

The UI now focuses on the daily workflow:
1. Add or drag-and-drop PDF drawings.
2. Select one installed AI model (vision models are preferred for diagram analysis).
3. Analyse drawings.
4. Watch material rows appear in the result table while processing.
5. Export Excel or CSV and select a row to inspect source evidence.

Advanced context, chunk, timeout, parallelism, cache and log controls have been removed from the main screen. The app retains safe fixed defaults and writes diagnostics to `%LocalAppData%\DrawingTakeoff\logs`.

## Models

For diagrams, install a vision-capable model:

```powershell
ollama pull qwen2.5vl:3b
```

For PDF pages where embedded text is sufficient, a text-only alternative is:

```powershell
ollama pull qwen2.5:3b
```

The model dropdown prefers `qwen2.5vl:3b` when installed, because it can inspect an image and the extracted text in one request. Select a vision model for diagrams containing symbols, linework or labels that are not exposed as embedded PDF text. If only a text model is selected, the application warns that graphics without extractable text cannot be visually inspected and asks whether to continue in text-only mode.

## Performance expectations

Fast mode reduces the AI workload from potentially many chunk requests plus a separate visual request to **one model request per page**. This is the largest safe software-side reduction without abandoning full-page material discovery.

A one-minute page target is a goal, not a guarantee. Actual runtime still depends on CPU/GPU acceleration, available RAM/VRAM, model quantisation, page image complexity, PDF extraction time, and how many material rows the model must return. A CPU-only machine may still take longer than one minute per page. For a fair benchmark, test the same drawing twice: the first run measures extraction + model inference; the second unchanged run measures cache performance. Per-page elapsed time, prompt token count, output tokens and inference throughput are written to the log file.

## Built-in analysis behaviour

- The deterministic extractor scans the full embedded PDF text for the architectural/structural material terms covered by the rule set.
- The model is prompted to identify additional materials and to decompose assemblies into their named components.
- The updated workbook's Lumber and Hardware sheets are loaded as separate reference catalogs (3,117 unique normalized lumber descriptions and 2,759 unique normalized hardware descriptions). Items not found in either catalog are still retained and classified as Other when they are not clearly lumber or hardware.
- Each new material gets a default appearance count of 1. Repeated occurrences of the same normalized material are consolidated and counted once per distinct evidence occurrence, with same-page model/rule reconciliation designed to prevent double counting.
- Parenthetical layer notation such as `(2) layers of 5/8 in. F.R. GYP. BD.` describes layers; it does not automatically mean two separate callout appearances.
- Explicit quantities and units are kept separately from occurrence counts. A visual count is marked for review; quantities are not invented when the drawing does not support them.

## Output

**Lumber & Hardware**: Sl num, Sheet / Drawing, Number of lumber items, Lumber item, Number of hardware items, Hardware item. Each material occupies one row; only its matching category pair is populated. The displayed number is the consolidated drawing-callout/appearance count (minimum 1), not an automatically measured purchase quantity.

**Other materials**: non-lumber/non-hardware materials (for example gypsum board, insulation, gypcrete, sealants and finishes) are preserved in a separate worksheet and UI tab so the six-column requested table does not silently lose them.

**Audit**: source file/page, category, occurrence count, quantity, unit, quantity basis, detail, source references, analysis method and evidence text.

Adding a PDF only places it in the selected-file queue; extraction begins when Analyse drawings is clicked. During analysis, preliminary rows appear as soon as the local scan finishes reading a page. Those are not final results: the status line then changes to show Ollama reviewing the same page for uncatalogued items, diagram interpretation and quantity evidence. AI findings are merged into the live list. Processing is complete only when the run status changes to Completed.

The `Detail/no of items` count is an occurrence/reference count, not automatically a procurement quantity. Use explicit schedule quantities or dimension-backed calculations for procurement, and have a qualified estimator/engineer verify all rows before ordering or construction.


## Latest architect updates (Lumber / Hardware / queue / grid)

- The result grid uses wrapped cells and wrapped headers, minimum widths, automatic row height, and horizontal scrolling; long catalog descriptions remain readable rather than being clipped.
- The PDF queue indicator reports total files, finished files, current processing file, pending files, and failures. “Finished” includes a completed attempt; failures are displayed separately.
- Hardware detection now includes exact part-code lookup against the hardware catalog (including short codes such as H3, LU26, A23 and TC26), full normalized catalog-description text matching, and terminology rules for hangers, hold-downs, straps, bolts, screws, anchors, washers, nuts, clips and brackets.
- The model prompt explicitly asks for a separate visual hardware inspection at connectors, framing intersections, post/beam bases, ledgers, rim joists, shear-wall boundaries and schedules, while also scanning all lumber/wood members and assembly components.
- Lumber and hardware catalog keys are precomputed once when catalogs load rather than regenerated on each page, reducing per-page catalog matching overhead.
- Cache version was incremented so results from the previous prompt are not reused.

## Build and run

Requires Windows and the .NET 8 SDK / Windows Desktop targeting pack.

```powershell
dotnet restore .\DrawingTakeoff.csproj
dotnet build .\DrawingTakeoff.csproj --configuration Release
dotnet run --project .\DrawingTakeoff.csproj --configuration Release
```

Alternatively run `BUILD-CHECK.ps1` from the project directory.

## Diagnostics

Logs: `%LocalAppData%\DrawingTakeoff\logs`
Cache: `%LocalAppData%\DrawingTakeoff\cache`

Use the log to compare `page complete in ...s`, Ollama prompt/output tokens and tokens/sec. If a single page still exceeds one minute, the log will show whether time is spent opening/extracting/rendering the PDF or inside model inference.


## Architect-level updates in this build

- The results DataGrids use wrapped headers and cells, automatic row height, minimum column widths, and horizontal scrolling so long sheet names and material descriptions remain readable.
- PDF-level progress shows finished, current/processing, pending, and failed files separately. A file is marked finished after its attempt ends; failures are called out rather than hidden.
- Hardware discovery has a dedicated exact part-number pass for short catalog identifiers (for example H3, LU26, A23, TC26, THD/HUC/LUS/SDS families) and text rules for hangers, hold-downs, straps, anchors, bolts, screws, nuts, washers, clips, brackets, post bases/caps and plates.
- The AI prompt has explicit independent lumber and hardware inspection instructions. Catalog entries remain suggestions, not a whitelist.
- The prompt/cache version was incremented so cached JSON from the earlier hardware prompt is not reused.
- The fast pass remains one Ollama inference request per page. Default image edge is 1000px and the response cap is 800 tokens to reduce inference work; complex sheets may still take longer or need a higher cap to avoid truncation.

### Notes

A text-only Ollama model cannot inspect vector-only or image-only drawing details. Select an installed vision-language model for visual connector and lumber callouts. Detected callout/appearance counts are not the same as procurement quantities. Review evidence and quantity basis before issuing a takeoff.

- The full normalized page text is also checked against complete catalog descriptions for direct lumber/hardware matches; short hardware part numbers have a separate token-exact check. These rule matches are reconciled with AI results by material identity, not blindly added to them.


## Latest classification and time-estimate update

- Hardware classification now checks explicit product/connector terminology and recognized hardware part codes **before** trusting the source catalog sheet. This corrects misfiled hardware rows in the reference workbook's Lumber sheet (including Simpson connectors, anchor bolts, flat straps, nails, screws and headed studs).
- Catalog-only classification is used after hardware semantics. Lumber dimensions and wood-member descriptions such as `2X4-16' SYP #2`, TJI joists/trusses, LVL, plywood and OSB remain Lumber when no hardware signal exists.
- Lumber and Hardware reference lists contain some duplicated normalized descriptions. Ambiguous overlaps are resolved from material terms and the model's category hint rather than blindly selecting the first catalog.
- Both result grids explicitly enable horizontal and vertical scrollbars. Header text wraps; cell text wraps and is center-aligned horizontally and vertically; columns remain user-resizable.
- The progress panel now shows elapsed time, estimated total time and remaining time. The ETA is based on measured page processing duration and observed average PDF page count. It recalculates every second while a model request is running. Before the first page completes, the display says `calibrating` because there is not yet a measured baseline. Estimates can move as additional drawings establish a better average; treat them as approximate, not a guaranteed finish time.
