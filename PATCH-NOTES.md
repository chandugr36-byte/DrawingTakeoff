# DrawingTakeoff4 – Lumber / Hardware Classification & ETA Update

Base project: `DrawingTakeoff4_Lumber_Hardware_ArchitectUpdate.zip` (the previous Lumber Hardware Compile Fix lineage).

## 1. Hardware vs. lumber classification

- Classification is now semantic-first. Explicit connector / fastener terminology and exact catalog hardware part numbers take priority over which worksheet the item happened to be stored in.
- Clear lumber descriptions (dimensional lumber, wood members, engineered wood, plywood, OSB, TJI, LVL, etc.) are evaluated before catalog-sheet membership. This also handles lumber products that are accidentally present in the Hardware reference sheet.
- Catalog membership is a fallback, not the deciding factor when the description itself identifies the material type.
- The AI's category is retained as a hint for otherwise ambiguous, uncatalogued descriptions; it cannot override explicit hardware or lumber evidence.

## 2. Result table layout

- Lumber/Hardware and Other result tables have both horizontal and vertical scrollbars set to `Auto`.
- Column headers wrap and are center-aligned horizontally and vertically.
- Cell text wraps, is horizontally centered, and is vertically centered; rows grow to show wrapped descriptions.
- Columns remain resizable and reorderable.

## 3. Time estimates

- The run header displays elapsed time, estimated total time, and remaining time.
- The estimate is recalculated every second while a page is being processed.
- It is based on measured page processing duration and observed average page count per completed PDF.
- The first page is used to establish a baseline. Before there is enough timing data, the UI displays `calibrating` instead of inventing a number.
- Estimates are approximate and can move as more PDFs complete, especially when drawings have very different page counts or image complexity.

## Validation notes

- `App.xaml`, `MainWindow.xaml`, and `DrawingTakeoff.csproj` XML parsing passed.
- WPF named event-handler and ETA control references were checked.
- Representative classification checks against the supplied catalogs passed, including Simpson connectors, anchor bolts, straps, headed studs, joist hangers, screws, dimensional lumber, TJI, LVL, and plywood.
- ZIP integrity will be verified before delivery.
- A full Windows/.NET build could not be run in the current environment because `dotnet`, `msbuild`, and `csc` are not installed. Run `BUILD-CHECK.ps1` on Windows to perform the actual restore/build.
