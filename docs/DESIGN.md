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
| Expressions | Fields, parameters, arithmetic, comparison, `IIf`, text/date functions, aggregates, range functions (`MonthStart`, `FinancialYear`, `Bucket`, `Range`), `AmountInWords`, page numbers, report variables |
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
   ├── PdfExporter      → vector PDF from our own writer (Export/Pdf), fonts embedded as subsets
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
* Settings dialog: data source and field schema, parameters, groups (start new page, restart page numbers) and sort, page size, variables and the record filter.
* Expression editor: function list with descriptions, fields/parameters/variables, live validity check.
* Arrange menu: z-order, group/ungroup, lock, copy/paste format; alignment guides while dragging.
* Conditional formatting editor: rules per element, opened from the property grid (*Conditional formats*).

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
| PDF | Own PDF writer (Export/Pdf): vector drawing, own word-wrap, TrueType subsetting | Selectable text; fonts embedded; page size = report page; barcode/QR as vector shapes |
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
discarded. The writer encrypts with AES 128-bit (standard security handler V4/R4); passwords should be plain Latin-1 characters.

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

* **The code has been written without a compiler.** The first CI run will show compile errors to fix (the PDF writer and
  the colour-editor type name are the likeliest). The QR encoder was checked against a Python reference decoded with OpenCV;
  the barcode tables were checked structurally.
* **PDF library.** PDFsharp 6.5 is AGPL/commercial with a runtime licence check (the CI failed with `PdfSharpLicenseException`),
  so ReportStudio writes PDFs itself (`Export/Pdf`: object model, TrueType subsetting, Type0 fonts with ToUnicode, AES-128).
  Tests read the files back with PdfPig (Apache-2.0, tests only). Limits: no kerning, a character missing from a font prints
  as that font's missing-glyph box (no per-character fallback), PostScript-outline (OTTO) and .ttc fonts are not supported.
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
15. Indic-script check in PDF — **done** (PDFsharp does no shaping, so Hindi/Tamil/other Indic text is laid out by GDI+ and written to the PDF as filled outlines: `ComplexScript` detects the script, `PdfComplexText` draws it. Font: the script's Noto Sans .ttf from the `Fonts` folder, else an installed Windows font such as Nirmala UI, else the old text path. Trade-off: that text is not selectable or searchable in the PDF; Latin text stays real text. Test writes `Golden/indic-sample.pdf` for a visual check)

**Stage 2 — Engine rules** (Core only; each builds on the one before)
16. Running totals and shared variables — **done** (`ReportDefinition.Variables`, each a `VariableDefinition`: Name, Function Sum/Count/Min/Max/Average/Assign, Expression, optional Condition and Initial, Reset Never/Group(level)/Page. Shown as `{Name}` in any band; a detail row counts before it prints, so a running total includes its own line; a page footer never includes a row that moved to the next page; a data field of the same name wins. `Assign` with `Balance + Credit - Debit` gives balances/shared values. Edited in the .rsr file for now; the designer dialog comes with items 23-26)
17. Conditional formatting (file format + engine) — **done** (`ReportItem.Rules`, each a `FormatRule`: Condition plus optional ForeColor, BackColor, BorderColor and Bold/Italic/Underline true/false. Every true rule applies in order, so a later one wins; empty settings are left alone; conditions can use fields, variables and parameters; a bad condition is a warning and the item keeps its design. Only the printed copy changes. Edited in the .rsr file for now; the designer dialog comes with items 23-26)
18. Record filter (selection expression) — **done** (`ReportDefinition.Filter`, e.g. `Status == 'Active' && Amount > @Min`; applied before sorting, grouping and totals, so everything counts only the kept rows and a group with no kept rows does not print; works in sub-reports too; the caller's rows are not changed; a row the filter cannot be worked out for is left out and one warning is given. Edited in the .rsr file for now; the report settings dialog gets a box with items 23-26)
19. Sort groups by their total, Top-N — **done** (per group: `SortExpression` such as `Sum(Amount)`, `SortDescending`, `TopN`. Done on the rows before layout, so every total, including the report footer, counts only the kept groups; ties keep the group-key order; works for inner groups inside each outer group; a bad sort expression is a warning and the key order stays. Edited in the .rsr file for now; the group dialog gets boxes with items 23-26)
20. Group by ranges (date by month, number by bucket) — **done** (done as expression functions, so they work in any expression: group, sort, filter, rule, cross-tab. Dates: `MonthStart`, `QuarterStart`, `YearStart`, `WeekStart` (Monday), `FinancialYear` (Indian April-March, "2025-26"). Numbers: `Bucket(x, size)` = lower end of equal-sized ranges; `Range(x, l1, l2, ...)` = lower limit of your own ranges (null below the first); `RangeLabel(x, l1, l2, ...)` = "Below 1,000" / "1,000 to below 5,000" / "5,000 and above" for headings. Group by `Range(...)`, show `RangeLabel(...)`, so groups sort in range order)
21. Keep-together, widow control, repeat group header on a continued group — **done** (per group: `KeepTogether` (a group that would be split starts on a new page, unless it is taller than a page), `KeepHeaderWithRows` N (header never alone at the bottom: it needs N detail rows after it), `KeepFooterWithRows` N (footer never alone at the top: the last N rows travel with it; for the group next to the detail rows), `RepeatHeader` (header printed again on each continuation page; `{Continued}` is true on the repeats, e.g. `{Name + IIf(Continued, ' (cont)', '')}`). All default off, so existing reports lay out as before. Edited in the .rsr file for now; the group dialog gets tick boxes with items 23-26)
22. Can-shrink and suppress-if-blank — **done** (elements: `SuppressIfBlank` = a blank element is not printed (no box, fill or border) and its space stays; `CanShrink` = a blank element also gives up its height, so elements below it that overlap it sideways move up (blank lines stacked above add up; blank lines side by side count once). Blank = no text, or no picture; spaces count as blank; an element with content keeps its designed height. Bands: `SuppressIfBlank` = a band in which nothing prints takes no room, `CanShrink` = the band is only as tall as what prints after the moves. All off by default. Page footers keep their designed position. Engine rules (16-22) are complete)

**Stage 3 — Designer support for stage 2** (needs 16–22)
23. Expression editor dialog with function list — **done** (`ExpressionEditorDialog`: text box that says at once whether the expression is valid, the function list by category with signature, description and example (`ExpressionFunctions`, checked against the engine by a test), and the report's fields, parameters and variables; double-click inserts. Opened from the property grid for *Visible when*, and from the settings dialog for the record filter and variable expressions. The settings dialog has a new *Variables && filter* tab, with checks (`DefinitionChecks`) before anything is stored)
24. Conditional formatting editor (needs 17 and 23) — **done** (*Conditional formats* in the property grid opens `RulesEditorDialog`: list of rules with Add, Delete, Up and Down; each rule has a condition (with the expression editor), text, fill and border colours with a picker and preview, and Bold, Italic and Underline as No change/Yes/No. Rules apply from the top, so a lower rule wins. Checked on OK by `DefinitionChecks.CheckRules`: condition present and valid, colours well formed, the rule changes something. One element at a time)
25. Alignment guides, grouping, locking, z-order, format painter (independent) — **done** (logic in `ItemArrange`, tested without a window: *guides* — dragged elements snap to the edges and centres of other elements and to the page and band edges within 4 px, magenta guide lines show while dragging, hold Alt to drag freely; *z-order* — bring to front/send to back/forward/backward, the band's item order is the stacking order; *grouping* — `ReportItem.ItemGroup`, clicking one member selects the group, groups move together, a pasted group is a new group; *locking* — `ReportItem.Locked`, a locked element cannot be moved, resized, nudged or deleted in the designer and shows a padlock; *format painter* — copies font, colours, border, alignment and number format, not position, text or rules. Arrange menu, Ctrl+G, Ctrl+Shift+G, Ctrl+L, Ctrl+Shift+C/V)
26. Data preview inside the designer — **done** (toolbar button / Report menu "Live data": the design canvas shows real values from the first data row instead of `{Field}` text, with the same template engine as a run, number formats applied, totals over up to 200 loaded rows, barcode/QR values evaluated. "Next row" steps through the rows. Respects the untrusted-file query confirmation and asks for parameters; a Host report has no data here. Changing settings or loading another report returns to placeholders; `DesignPreview` in Core, tested)

**Stage 4 — Links and interaction**
27. Hyperlinks and bookmarks — **done** (`ReportItem.Link` and `ReportItem.Bookmark`, both text with `{expressions}`. Link: `https://…`, `mailto:`, `tel:` or `#Name` (jump to a bookmark in the same report); anything else (file:, javascript:, network paths) is refused with a warning, spaces/accents are percent-encoded (`LinkTarget`). Bookmark: names a place; the first use of a name wins (`ReportResult.FindBookmark`). PDF: clickable link areas (URI or jump to the bookmark's page and height); HTML: anchors; viewer: hand cursor and click, with a question before an outside address is opened. Word and Excel links are not done yet)
28. Table of contents and PDF bookmarks (needs 27) — **done** (`ReportItem.Outline` = title with `{expressions}` and `OutlineLevel` (1 = main heading, 2 under the last 1, ...). Every outline place appears in the PDF reader's bookmark panel (nested tree, opens on the panel) and can be listed by a new element, the **Contents** tool (`ItemKind.Contents`: lines per level with indent, the page number as `{Page}` prints it, and a link to the place; settings ContentsLevels, ContentsRowHeight, ContentsIndent). The first layout pass finds where places land and the second prints them; the box keeps its designed height in both passes, so listing never moves what it lists, and lines that do not fit are left out with a warning. Places without their own bookmark get an automatic one (`_o0`, `_o1`...). `ReportResult.FindOutline()` gives the list to other code)
29. Search in the viewer — **done** (a Find box in the viewer toolbar, Ctrl+F to reach it; Enter / F3 = next match, Shift+Enter / Shift+F3 = previous, Esc clears; "Aa" = match case. Matches are the text elements of the report, in reading order, over all pages; line breaks and repeated spaces count as one space, so a phrase that wrapped is still found. All matches on the page are marked yellow and the current one orange, with a count "3 of 12"; the viewer moves to the page and scrolls to it. `ReportSearch.Find` in Core)
30. Drill-down and drill-through (needs 27) — **done** (drill-down: a group can be `Collapsible` (tick box in Report settings, Grouping tab) and `StartCollapsed`; in the viewer a click on its header opens or closes that group instance, the report runs again and stays on the same page. A closed instance prints header and footer only; totals in its footer and in the report still count its rows, but running variables skip the hidden rows. `{DrillMark}` in the header shows "-" open / "+" closed. `ReportOptions.ToggledGroups` holds the clicked instances (keys include the outer groups' values). Drill-through: `ReportItem.DrillReport` (a .rsr file in the same folder, `{expressions}` allowed) and `DrillParameters` (`Month={@PayMonth}; Code={EmpCode}`): a click opens that report in a new window with those values; files outside the folder or not .rsr are refused (`DrillTarget`), a file with its own query asks first, and a host can take over through the viewer's `DrillThrough` event)
31. Parameter upgrades: dropdown from data, multi-value, dependent parameters, review defaults before running — **done** (per parameter, in Report settings, Parameters tab: `Choices` (fixed drop-down: `Active;Inactive` or `A=Active;I=Inactive`), `ChoicesQuery` (a query on the report's connection: first column = value, second = shown text; may use other parameters as `@Name`), `DependsOn` (the list is read again when those parameters change in the window), `MultiValue` (tick several; the value is a list: `In(Dept, @Depts)` in expressions, `WHERE Dept IN (@Depts)` in SQL, which is expanded into real bound parameters; shown on the report as "Sales, HR"). Defaults can be expressions: `=MonthStart(Today())`, `=FinancialYear(Today())`; the parameter window shows the worked-out value for review before running. A list query counts as a query from the file, so a file that has one asks before it runs, and the question lists it. New function `In(value, option, ...)`. A report on the Sample or Host data source has no database and uses the fixed list only)

**Stage 5 — Elements** (each independent)
32. More barcodes: EAN-13, UPC, ITF, Codabar — **done** (`BarcodeType` now also Ean13, Ean8, UpcA, Itf, Codabar; chosen in the property grid like Code 128 / Code 39. EAN/UPC: give the digits without the check digit and it is added, or with it and it is checked (a wrong one prints nothing and warns); the text under the bars shows the full number; a quiet zone of 11 modules. ITF: digits only, an odd count gets a leading 0, so ITF-14 is 14 digits. Codabar: 0-9 - $ : / . + with start/stop letters A-D (A is added when missing). All renderers (screen, PDF, HTML, images) share one encoder. UPC-E is not done)
33. Data Matrix, PDF417, Aztec — **done** (one new element, the **2D code** tool: `ItemKind.Barcode2D` with `Barcode2DType`; `Text` takes `{expressions}` like the other codes. **Data Matrix** is done: ECC 200, symbols 10 x 10 to 48 x 48 (up to 174 codewords: about 173 characters or 348 digits; digit pairs take one codeword), square, with a quiet zone of 2 modules; text above 255 is written as UTF-8. The smallest symbol that fits is chosen. A value that is empty or too long prints nothing and warns ("2D code 'name': ..."). All renderers (screen, PDF, HTML, images) draw the same dark runs from `Symbol2DLayout`. Reed-Solomon words and the module placement were checked against the standard's own example and for every symbol size, and symbols were read back by an independent reader (ZXing). **PDF417** is done: 3 to 90 rows, 2 to 30 data columns, up to 928 codewords (about 1100 characters or 2700 digits), rows 3 modules tall; text, byte and numeric compaction are chosen automatically as the value changes (two characters per codeword in text, six bytes in five codewords, 44 digits at a time in numeric); text beyond ISO 8859-1 is written as UTF-8 with an ECI header; the error correction level follows the standard's recommendation for the size (levels 2 to 5); the number of columns is chosen so the symbol is about three times wider than tall. Choose it in the property grid (2D code > Barcode2DType) and make the box wider than tall. The bar-pattern table is the standard's (checked, and symbols read back by ZXing). **Aztec** is done: square, compact symbols 15 to 27 modules and full symbols 19 to 151 modules (reference lines every 16 modules, 33 % error correction, 4 to 12 bit codewords by size); a shortest-path search picks the upper, lower, digit, mixed and punctuation sub-modes, the punctuation pairs and the binary shifts; text beyond ISO 8859-1 is written as UTF-8 with an ECI header; about 3000 letters, 3700 digits or 1800 other characters fit. The module layout was checked bit for bit against the ZXing encoder and symbols of every size were read back by it.)
34. Ellipse, rounded box, gradient, watermark, page background, rotated text — **done**, in parts: (a) ellipse and rounded box — **done** (`ItemKind.Ellipse`; `CornerRadius` on a box, cut back to half the shorter side by `ShapeGeometry.CornerRadius`; drawn with GDI+ paths in the preview and image export, with Bezier curves in the PDF (`PdfPage.FillShape` / `StrokeShape`), with CSS `border-radius` in HTML; designer tool "Ellipse"; Excel and Word treat both like a plain box). (b) gradient fill — **done** (`GradientColor` + `GradientDirection` on a box, ellipse or text box; needs a back colour; GDI+ `LinearGradientBrush` in the preview and image export, a PDF axial shading defined once per colour pair on the unit square and clipped to the shape, CSS `linear-gradient` in HTML; Excel and Word keep the solid back colour). (c) watermark — **done** (text watermark: `ReportDefinition.Watermark` = text with {expressions}, colour, opacity, angle counter-clockwise, size in pt, bold; set on the new "Page look" tab of the report settings; resolved per page by the engine into `RenderedPage.Watermark`, so `{Page}` and `{@Parameters}` work; drawn first, behind the items, turned about the page centre and shrunk by `WatermarkLayout.FitFontSize` to fit nine tenths of the room along its line; GDI+ in the preview, image export and printing, PDF with `PdfPage.PushRotation` + an `ExtGState` opacity registered once per percent, HTML with a flex-centred, CSS-rotated, `pointer-events:none` span whose width is only estimated; Excel and Word do not show it; a picture watermark comes with requirement 35). (d) page background — **done** (`ReportDefinition.Background` = colour, optional gradient colour and direction, on the "Page look" tab; a gradient colour without a colour is ignored; the engine puts it on every page as `RenderedPage.Background`; painted first of all, before the watermark and the items; GDI+ fills one pixel beyond the page so a fractional scale leaves no white hairline, PDF fills a rectangle or the shared axial shading clipped to the page, HTML paints it on the page box with print-colour hints; Excel and Word do not show it). (e) rotated text — **done** (`ReportItem.TextRotation`, degrees counter-clockwise about the middle of the box; only the text turns, the fill and border stay upright; near a quarter turn the text is laid out in a box with width and height swapped (`TextTurn.LayoutSize`) so a tall narrow box gives a vertical column heading; a turned text does not grow with Can grow; GDI+ `RotateTransform`, PDF `PushRotation` about the box middle (complex scripts included), HTML a separate CSS-rotated text box; Excel and Word keep the text upright).
35. SVG images and images from a data field — **in progress**, in parts: (a) images from a data field — **done** (an Image element whose Image path is a single `{Field}` takes the picture from the row: raw bytes of a database image column, base64 text, or a `data:image/...;base64,` address; `ImageData` accepts only bytes that start like a PNG, JPEG, GIF, BMP or TIFF and at most 20 MB; an empty field prints no picture and no warning, bytes that are not a picture give a warning; a field holding a file name still loads the file). (b) SVG pictures — **done** (Image elements accept plain SVG drawings from a file, a field holding the markup, base64 or a `data:image/svg+xml` address, or the designer's picture picker; `ImageData.IsSafeSvg` refuses any SVG with scripts, event handlers, entities or DOCTYPE, `foreignObject`, `@import`, or any link or `url()` that is not inside the drawing (`#name`) or a picture held in the file (`data:image/`) — such a file or field is left out with a warning, so a report file can never make the printing machine read other files or call web addresses; drawn with the Svg library (3.4.7) into a PNG fitted to the box with its proportions kept, once per use: preview, image export and printing at the box size, PDF at twice the box size; HTML embeds the SVG itself in an `img` tag, which never runs scripts; Excel and Word are not changed by this part). To do: the picture watermark.
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
