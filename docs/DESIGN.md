# ReportStudio — design document

A standalone .NET reporting suite in the spirit of Crystal Reports, JasperReports and Eclipse BIRT:
a **designer** where report authors build band-based reports, a **report definition file** (`.rsr`) that is
the unit of distribution, and a **viewer** that end users open to run, view, export and print a report.
It is independent of the Revolution HRMS project, which uses it as a library (section 10).

## 1. Scope

| Area | Status |
|---|---|
| Designer | WinForms band canvas, property grid, field list, undo/redo, preview tab, rulers, zoom, drag between bands |
| Distribution | `.rsr` JSON file + stand-alone Viewer (no designer needed) |
| Data | Host-supplied rows (default for embedding); SQL Server, MySQL/MariaDB, PostgreSQL through the optional `ReportStudio.Data` project |
| Layout | Report/page header and footer, detail, nested groups, per-group page numbering |
| Elements | Text, line, box, image, barcode (Code 128, Code 39), QR code, sub-report, cross-tab |
| Expressions | Fields, parameters, arithmetic, comparison, `IIf`, text/date functions, aggregates, `AmountInWords`, page numbers |
| Formats | Indian grouping (lakh/crore), `dd-MM-yyyy`, rupee symbol, away-from-zero money rounding |
| Fonts | Ubuntu by default, embedded in PDF |
| Export | PDF (vector), Excel (.xlsx), Word (.docx), images (PNG, JPEG, BMP, TIFF), print |
| Automation | Headless `ReportRunner` returning PDF / Excel bytes |

Not planned: **charts** (dropped by the client), web viewer, scheduled delivery.

## 2. Architecture

```
ReportStudio.Designer (exe)     ReportStudio.Viewer (exe)
        \                              /
         ReportStudio.UI  (ViewerControl, ParameterDialog, PrintHelper, UntrustedFileGuard)
                  |
         ReportStudio.Export  (GDI renderer, PDF, Excel, Word, image, FontCatalog, ReportRunner)
                  |
         ReportStudio.Core  (model, expressions, barcodes/QR, formatting, layout engine)  <- no UI, no database drivers

ReportStudio.Data   (optional)  SQL Server / MySQL / PostgreSQL loader, plugged in with DatabaseLoader.Register()
```

* **Core** is plain `net10.0` with no Windows or database dependency, so the engine can run in a service or a
  scheduler unchanged. It holds almost all unit tests.
* **Data** is separate so an application that only ever supplies its own rows (the HRMS) ships no database drivers.
* **Export, UI, Designer, Viewer** target `net10.0-windows` (WinForms, GDI+).
* **Export** consumes the engine's *rendered page model*, never the definition, so every output shows the same content.
* **UI** holds everything shared by the designer's *Preview* tab and the Viewer.

Tests: `ReportStudio.Tests` (engine, runs anywhere) and `ReportStudio.Export.Tests` (golden-image tests, Windows).

### Pipeline

```
.rsr (ReportDefinition) + parameter values + rows
        │                                   (rows come from the host, or from DataLoader when a database is registered)
   ReportEngine.Run  ── sort ── group ── evaluate ── paginate (2 passes, per numbering section)
        │
   ReportResult  =  pages of RenderedItem (absolute positions, resolved text)
        │
   ├── GdiPageRenderer  → screen preview, print, PNG/JPEG/BMP/TIFF
   ├── PdfExporter      → vector PDF via PDFsharp, fonts embedded
   └── GridBuilder → ExcelExporter / WordExporter   (tables, numbers stay numbers)
```

Pagination runs twice: pass one counts the pages of every numbering section so pass two can print "Page 3 of 7".

## 3. Report model (`.rsr`)

One JSON document (`ReportDefinition`): page settings, data source, parameters, groups, sort expressions, five fixed
bands plus a header/footer pair per group. Geometry is in pixels at 96 DPI (A4 = 794 × 1123), positions relative to
the band and the page margins. Enums are written as strings, names are case-insensitive.

```jsonc
{
  "formatVersion": 1,
  "name": "Payslip",
  "page": { "width": 794, "height": 1123, "marginLeft": 40, ... },
  "dataSource": {
    "provider": "Host",
    "fields": [ { "name": "EmpCode", "type": "Text" }, { "name": "Net", "type": "Number" } ]
  },
  "parameters": [ { "name": "Month", "type": "Text", "defaultValue": "" } ],
  "groups": [ { "expression": "EmpCode", "startNewPage": true, "restartPageNumbers": true, "header": { ... }, "footer": { ... } } ],
  "detail": { "height": 20, "items": [ { "kind": "Text", "x": 0, "y": 1, "width": 70, "height": 18, "text": "{Net}", "format": "N2" } ] }
}
```

Every element is one `ReportItem` with a `kind`, so the property grid edits all of them the same way and the file stays
small and diff-friendly. `formatVersion` allows migrations.

**Field schema.** `dataSource.fields` lists each field's name and type (Text, Number, Date, Boolean). The designer's
field list reads it, and `JsonRows` uses it to type JSON data, so a report can be designed with no database at all.

## 4. Expression language

Text elements mix literal text with `{expressions}`: `Total {Country}: {Sum(Amount)}`. Fields are `Name` or
`[Name With Spaces]`, parameters are `@Name`, `{{` / `}}` are literal braces. A hand-written recursive-descent parser
builds a cached AST (no `eval`, no dynamic compilation), so expressions are safe to run from files received from other
people. Numbers are `decimal` throughout to avoid floating-point drift in money. Aggregates evaluate over the *scope of
the band*: the group for group bands, the whole report for the report footer.

`AmountInWords(x)` writes rupees in the Indian system ("Rupees One Lakh Twenty Three Thousand Four Hundred Fifty Six and Fifty Paise Only").
A date with no format prints as a date only (`dd-MM-yyyy` in the Indian culture).

Errors never abort a run: a bad expression prints `#ERR` and is listed once in the viewer's status bar.

## 5. Elements

| Kind | Notes |
|---|---|
| Text, Line, Box, Image | As before. Image from Base64 in the file or a path resolved against the report's folder |
| Barcode | Code 128 (set C for even digit strings, else set B) and Code 39, drawn as bars. The readable value is a separate centred Text item, so Excel/Word carry it |
| QR code | Own encoder: byte mode, UTF-8, versions 1–40, error levels L/M/Q/H, mask chosen by penalty. A value that cannot be encoded prints nothing and warns |
| Sub-report | Another report drawn inside a band, see below |
| Cross-tab | Rows × columns summary, see below |

Barcodes and QR codes print the value of `Text`, which may be an expression (`{EmpCode}`, `upi://pay?pa={Upi}&am={Net}`).

### Sub-reports

A sub-report item names another report and says where its rows come from.

* **Report:** a name the host registered (`ReportOptions.SubReports`), or a `.rsr` file **inside the parent report's
  folder**. A path that leaves that folder, or is not `.rsr`, is refused with a warning.
* **Rows:** a field of the parent row that holds a list of rows, **or** a host data set (`ReportOptions.DataSets`)
  filtered by `LinkChild` = `LinkParent` (for example `EmpCode`).
* **Parameters:** `Name={expression}; Name2={expression}` evaluated on the parent row.
* The child always runs as a Host report (its own query is never run). Its page header and footer are not used.
* The item's band grows to fit the child. A child longer than one page shows its first page and warns.
* Nesting is limited to 3 levels.

### Cross-tabs

A cross-tab summarises the rows in the scope of its band (so in a report footer it covers the whole report, in a group
footer one group). Set the row expression, the column expression, the value and the function (Sum, Count, Average, Min,
Max). Distinct values are sorted; an optional Total column and Total row are added. It is expanded into ordinary Text
cells, so every export carries it, and the band grows to hold it.

## 6. Designer

* Bands stack vertically; drag a band's bottom edge to resize it.
* Draw elements with the Insert tools, or double-click a field in the field list.
* Move/resize with 8 handles, snap-to-grid, arrow-key nudging, multi-select (Ctrl+click), align and same-size.
* **Drag between bands:** items follow the mouse into whichever band it is over, keeping their place on the page.
* **Rulers:** top ruler along the page width with the margins shaded; left ruler restarting at 0 in every band; a
  red line follows the mouse. Units cycle cm / in / px with the toolbar button.
* **Zoom:** 25 %–400 % with the toolbar or Ctrl + mouse wheel. The canvas works in report pixels and scales at the edges.
* Properties (font, colours, borders, format, *Visible when*, can-grow, element-specific settings) in the property grid.
* Undo/redo is snapshot-based, so every edit, including property grid and settings changes, is undoable with no per-command code.
* Settings dialog: data source and field schema, parameters, groups (start new page, restart page numbers) and sort, page size.

## 7. Data access and security

* **Host mode (default for embedding).** The host application passes the rows. A Host report stores no query or
  connection string, and any found in a file is stripped when it is loaded or saved (`Normalize`). Running a Host report
  without rows is an error, never sample data.
* **Database mode** needs `ReportStudio.Data`. Queries use real `DbParameter`s bound only for `@names` in the SQL; values
  are never concatenated. Drivers: `Microsoft.Data.SqlClient`, `MySqlConnector`, `Npgsql`.
* **Untrusted files.** The Viewer asks before running a report from a file (`UntrustedFileGuard`); `ViewerControl.HostOnly`
  makes it refuse any report that has its own query. Sub-report files are confined to the parent's folder.
* Connection strings in database mode are stored in plain text. Use a read-only account, or Host mode.

## 8. Pagination and page numbers

A group can **start a new page** and/or **restart page numbers**. Restarting begins a new numbering *section*: `{Page}`
and `{PageCount}` count inside the section, so a 200-employee payslip run prints "Page 1 of 1" for every employee, and an
employee with a long statement prints "Page 2 of 3". Each page records its section.

## 9. Output

| Format | Approach | Notes |
|---|---|---|
| PDF | PDFsharp vector drawing, own word-wrap | Selectable text; fonts embedded; page size = report page; barcode/QR as vector shapes |
| Excel | OpenXML; band lines become rows, left edges become columns | Numbers stay numbers with Indian number formats; dates `dd-mm-yyyy`; barcode/QR bars are not carried (their caption text is) |
| Word | OpenXML; borderless table keeping column layout | Editable, not pixel-identical |
| HTML | One self-contained `.html`: a sheet per page, absolutely positioned elements, images as data URIs, barcode/QR as inline SVG, no scripts | Text stays selectable; fonts are not embedded (falls back to Segoe UI / Arial); prints one report page per sheet |
| CSV | Same table as Excel/Word; UTF-8 with BOM, CRLF | Headings + detail by default; numbers plain (`1234567.50`), dates ISO, `007`-style codes stay text; text starting `=` `@` or `+`/`-` gets a leading `'` |
| Images | GDI+ at 150 dpi (configurable) | One file per page |
| Print | Pages rasterised at 200 dpi | Same renderer as the preview |

**Fonts.** `FontCatalog` loads `Fonts/*.ttf` named `Family-Style.ttf` (Ubuntu Regular/Bold/Italic/BoldItalic, shipped with
the app, see `fonts/README.txt`). They are used by GDI+ (private font collection) and embedded in PDFs. Ubuntu is the
default for new items and the fallback for an unknown font.

**Indian formats.** `IndianFormat.Culture` groups digits 3,2,2 (`12,34,567.00`), prints dates `dd-MM-yyyy` and the ₹ symbol.
`RoundMoney` rounds half away from zero. It is the default culture of the Viewer and `ReportRunner`.

**PDF protection.** `PdfOptions` sets a password to open (`UserPassword`) and switches off printing, copying or editing.
Restrictions only hold when an owner password nobody else knows is set; if none is given a random one is generated and
discarded. PDFsharp 6 encrypts with AES 128-bit by default; passwords should be plain Latin-1 characters.

**Headless.** `ReportRunner.ToPdf` / `ToExcel` take a definition, parameters and rows and return bytes, with no window.
They force Host mode, so a file cannot make a headless run touch a database.

## 10. Using it from Revolution HRMS

* The HRMS references the built assemblies at a **pinned version**; `Revolution.Reports` wraps them.
* Services return rows, never a window or message box. The report layout only names fields.
* Reports call stored procedures (`rpt.usp_*`) in the HRMS; the report file contains no SQL.
* Changes arrive as numbered requests, one at a time.

## 11. Build, test, CI

Requirements: .NET 10 SDK (pinned in `global.json`).

```
dotnet build ReportStudio.sln -c Release -warnaserror
dotnet test  --solution ReportStudio.sln -c Release   # xunit v3 on Microsoft Testing Platform (set in global.json)
dotnet run   --project src/ReportStudio.Designer
dotnet run   --project src/ReportStudio.Viewer -- path\to\report.rsr
```

GitHub Actions (`.github/workflows/ci.yml`) builds with warnings as errors, runs all tests, uploads test results, golden
files and the published Designer and Viewer.

**Samples** (`samples/`): a payslip and a Form 16-style report with fixed data, regenerated by `tools/make_samples.py`.
**Golden files:** layout dumps (`Golden/*.txt`) and rendered images (`Golden/*.png`). A missing golden file is created
by the first run (the test passes); download the `golden-files` artifact from CI and commit it. Set `UPDATE_GOLDEN=1` to
refresh after an intended change. The image tests need the Ubuntu fonts in `fonts/`.

## 12. Status and known limitations

* **The code has been written without a compiler.** The first CI run will show compile errors to fix (PDFsharp calls and
  the colour-editor type name are the likeliest). The QR encoder was checked against a Python reference decoded with OpenCV;
  the barcode tables were checked structurally.
* The Ubuntu font files must be added to `fonts/` by hand (licence included); until then image tests fail on purpose.
* Excel/Word export is structural (rows and columns), so overlapping or free-floating elements are approximated.
  Negative amounts of a lakh or more use Western grouping in Excel.
* *Can grow* uses GDI+ measurement; PDF and Word line breaks can differ slightly from the preview.
* A sub-report must fit on one page. A cross-tab cannot split across pages.
* The designer rulers, zoom and drag-between-bands have no automated tests (window code); check them by hand.
* Images are embedded as Base64 in the `.rsr`, so files with large pictures get big.

## 13. Roadmap

Requirements 1–11 are done. The rest are numbered and ordered so that **an item only depends on items before it**;
each one is small enough to be done, tested and delivered alone. Gauges, sparklines and maps are not planned (they belong
with the dropped charts).

**First, whenever convenient:** compile, fix the first CI errors, commit the golden files, add the Ubuntu fonts.
Named connections with encrypted credentials (database mode) stay on hold until the HRMS needs them.

**Stage 1 — Output quick wins** (touch only the exporters; independent of everything else)
12. PDF password and permissions (payslip PDFs) — **done** (`PdfOptions`: user/owner password, allow print / copy / modify; `ReportRunner.ToPdf(..., pdf: options)`)
13. CSV export — **done** (`CsvExporter`, `CsvOptions`: separator, data only or every line, plain or as-printed numbers; UTF-8 with BOM; formula-injection guard)
14. HTML export — **done** (`HtmlExporter`: one self-contained file, a sheet per report page, elements positioned exactly, pictures embedded, barcodes/QR as inline SVG, prints one page per sheet)
15. Indic-script check in PDF (Hindi/Tamil sample; embed a Noto font; decide how to handle shaping)

**Stage 2 — Engine rules** (Core only; each builds on the one before)
16. Running totals and shared variables
17. Conditional formatting (file format + engine)
18. Record filter (selection expression)
19. Sort groups by their total, Top-N
20. Group by ranges (date by month, number by bucket)
21. Keep-together, widow control, repeat group header on a continued group
22. Can-shrink and suppress-if-blank

**Stage 3 — Designer support for stage 2** (needs 16–22)
23. Expression editor dialog with function list
24. Conditional formatting editor (needs 17 and 23)
25. Alignment guides, grouping, locking, z-order, format painter (independent)
26. Data preview inside the designer

**Stage 4 — Links and interaction**
27. Hyperlinks and bookmarks
28. Table of contents and PDF bookmarks (needs 27)
29. Search in the viewer
30. Drill-down and drill-through (needs 27)
31. Parameter upgrades: dropdown from data, multi-value, dependent parameters, review defaults before running

**Stage 5 — Elements** (each independent)
32. More barcodes: EAN-13, UPC, ITF, Codabar
33. Data Matrix, PDF417, Aztec
34. Ellipse, rounded box, gradient, watermark, page background, rotated text
35. SVG images and images from a data field
36. Rich / HTML text

**Stage 6 — Layout and data**
37. Multi-column pages and address labels
38. Mixed page sizes and orientations in one report
39. Several data sets in one report (lookups, joins)
40. Direct JSON, XML, CSV, Excel, REST and OData sources
41. Data caching

**Stage 7 — Output II**
42. Excel formulas and one sheet per group
43. PDF/A, digital signature, tagged PDF
44. RTF, ODT/ODS, PowerPoint, XPS

**Stage 8 — Designer II**
45. Report wizard and templates
46. Styles and themes
47. Ribbon, dark mode, localised designer UI

**Stage 9 — Platform**
48. Command-line runner (`reportstudio run report.rsr --param Year=2026 --pdf out.pdf`)
49. Web viewer (ASP.NET Core) reusing Core with a non-GDI renderer
50. Report server: scheduling, e-mail delivery, subscriptions
51. Roles and report versioning
52. AI-assisted design (optional)
