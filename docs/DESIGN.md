# ReportStudio — design document

A standalone .NET reporting suite in the spirit of Crystal Reports, JasperReports and Eclipse BIRT:
a **designer** where report authors build band-based reports, a **report definition file** (`.rsr`) that is
the unit of distribution, and a **viewer** that end users open to run, view, export and print a report.
It is independent of the Revolution HRMS project.

## 1. Goals and scope (version 1)

| Area | In scope for v1 |
|---|---|
| Designer | WinForms drag-and-drop band canvas, property grid, field list, undo/redo, preview tab |
| Distribution | `.rsr` JSON file + stand-alone Viewer (no designer needed) |
| Data | SQL Server, MySQL/MariaDB, PostgreSQL via a SQL query with `@parameters`; built-in sample data |
| Layout | Report header/footer, page header/footer, detail, unlimited nested groups with header/footer |
| Expressions | Fields, parameters, arithmetic, comparison, `IIf`, text/date functions, aggregates (`Sum`, `Avg`, `Count`, `Min`, `Max`, `CountDistinct`), page numbers |
| Export | PDF (vector), Excel (.xlsx), Word (.docx), images (PNG, JPEG, BMP, TIFF), print |

Out of scope for v1 (see roadmap): charts, cross-tabs, sub-reports, barcodes, web viewer, scheduled delivery.

## 2. Architecture

```
ReportStudio.Designer (exe)     ReportStudio.Viewer (exe)
        \                              /
         ReportStudio.UI  (ViewerControl, ParameterDialog, PrintHelper)
                  |
         ReportStudio.Export  (GDI page renderer, PDF, Excel, Word, image)
                  |
         ReportStudio.Core  (model, expressions, data, layout engine)   <- no UI dependencies
```

* **Core** is plain `net8.0` with no Windows dependency, so the engine can later run in a web service or a
  CLI/scheduler unchanged. It is the layer covered by unit tests.
* **Export** consumes the engine's *rendered page model*, never the definition, so every output format
  shows the same content.
* **UI** holds everything shared by the designer's *Preview* tab and the stand-alone Viewer, so what you
  preview is what end users get.

### Pipeline

```
.rsr (ReportDefinition) + parameter values
        │
   DataLoader  ── SQL with real DB parameters ──►  rows (name → value)
        │
   ReportEngine.Run  ── sort by group keys ── group ── evaluate expressions ── paginate (2 passes)
        │
   ReportResult  =  pages of RenderedItem (absolute positions, resolved text)
        │
   ├── GdiPageRenderer  → screen preview, print, PNG/JPEG/BMP/TIFF
   ├── PdfExporter      → vector PDF via PDFsharp
   └── Grid → ExcelExporter / WordExporter   (editable tables, numbers stay numbers)
```

Pagination runs twice: pass one counts pages so pass two can print "Page 3 of 7".

## 3. Report model (`.rsr`)

One JSON document (`ReportDefinition`): page settings, data source, parameters, groups, sort expressions,
five fixed bands plus a header/footer pair per group. Geometry is in pixels at 96 DPI (A4 = 794 × 1123),
positions relative to the band and the page margins.

```jsonc
{
  "formatVersion": 1,
  "name": "Sales by Country",
  "page": { "width": 794, "height": 1123, "marginLeft": 40, ... },
  "dataSource": { "provider": "SqlServer", "connectionString": "...", "query": "SELECT ... WHERE Year = @Year" },
  "parameters": [ { "name": "Year", "type": "Number", "prompt": "Financial year", "defaultValue": "2026" } ],
  "groups": [ { "expression": "Country", "startNewPage": false, "header": { ... }, "footer": { ... } } ],
  "detail": { "height": 20, "items": [ { "kind": "Text", "x": 0, "y": 1, "width": 70, "height": 18, "text": "{InvoiceNo}", "format": "0" } ] }
}
```

Elements are one `ReportItem` type with a `kind` (Text, Line, Rectangle, Image), so the property grid edits
every element the same way and the file stays small and diff-friendly. `formatVersion` allows migrations.

## 4. Expression language

Text elements mix literal text with `{expressions}`: `Total {Country}: {Sum(Amount)}`.
Fields are `Name` or `[Name With Spaces]`, parameters are `@Name`, `{{` / `}}` are literal braces.
A hand-written recursive-descent parser builds a cached AST (no `eval`, no dynamic compilation), so expressions
are safe to run from files received from other people. Numbers are `decimal` throughout to avoid floating-point
drift in money totals. Aggregates evaluate over the *scope of the band*: the group for group bands, the whole
report for the report footer.

Errors never abort a run: a bad expression prints `#ERR` and is listed once in the viewer's status bar.

## 5. Designer

* Bands stack vertically; drag a band's bottom edge to resize it.
* Draw elements with the Insert tools, or double-click a field in the field list.
* Move/resize with 8 handles, snap-to-grid, arrow-key nudging, multi-select (Ctrl+click), align and same-size.
* Properties (font, colours, borders, format, *Visible when*, can-grow) in the property grid.
* Undo/redo is snapshot-based (the definition is small JSON), so every edit, including property grid and
  settings changes, is undoable with no per-command code.
* Settings dialog: data source (with *Test connection & load fields*), parameters, groups/sort, page size.

## 6. Data access and security

* Queries use real `DbParameter`s, bound only for `@names` that appear in the SQL. Parameter values are never
  concatenated into SQL.
* Drivers: `Microsoft.Data.SqlClient`, `MySqlConnector`, `Npgsql`.
* **Connection strings are stored in the `.rsr` in plain text in v1.** For distribution, use a read-only database
  account, or (roadmap) resolve connections by name from a per-machine config or secret store.
* A distributed report runs SQL written by its author. Treat `.rsr` files from untrusted sources like macros; a
  warn-before-run prompt is a roadmap item.

## 7. Export details

| Format | Approach | Notes |
|---|---|---|
| PDF | PDFsharp vector drawing, own word-wrap | Selectable text; page size = report page |
| Excel | OpenXML; band lines become rows, left edges become columns | Numbers stored as numbers; page footers dropped; page header kept once |
| Word | OpenXML; borderless table keeping column layout | Editable, not pixel-identical |
| Images | GDI+ at 150 dpi (configurable) | One file per page (`_page1`, ...) |
| Print | Pages rasterised at 200 dpi | Same renderer as the preview |

## 8. Build and run

Requirements: Windows, .NET 8 SDK, internet access for NuGet on first build.

```
dotnet build ReportStudio.sln
dotnet test  tests/ReportStudio.Tests          # engine + expression tests (also run on Linux/macOS)
dotnet run   --project src/ReportStudio.Designer
dotnet run   --project src/ReportStudio.Viewer -- path\to\report.rsr
```

The Designer opens with the built-in *Sales by Country* sample (no database needed). Press **F5** to preview, then
use **Export** to try PDF, Excel, Word and image output.

## 9. Status and known limitations

* **The code has not been compiled or run.** It was written in an environment without a .NET SDK, and an
  independent read-through found no definite compile errors but flagged a few API-signature risks (PDFsharp,
  property-grid colour editor). Expect a short round of compile fixes on first build; run the unit tests first.
* The designer canvas is 100 % zoom only, has no rulers, and moves elements between bands via cut/paste.
* *Can grow* uses GDI+ measurement; PDF and Word line breaks can differ slightly from the preview.
* Excel/Word export is structural (rows and columns), so overlapping or free-floating elements are approximated,
  and Excel cells do not carry the report's number format.
* Images are embedded as Base64 in the `.rsr`, so files with large pictures get big.

## 10. Roadmap

1. Compile and stabilise; add golden-image tests for the renderer.
2. Named connections with encrypted credentials; warn-before-run for untrusted files.
3. Charts (bar/line/pie), barcodes/QR, running totals, conditional-formatting UI.
4. Sub-reports and cross-tabs; per-group page numbering.
5. Zoom, rulers and guides; dragging elements between bands; report wizard.
6. Headless runner (`reportstudio run report.rsr --param Year=2026 --pdf out.pdf`) and scheduled e-mail delivery.
7. Web viewer (ASP.NET Core) reusing Core with a non-GDI renderer.
