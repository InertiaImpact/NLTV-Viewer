# NLTV Viewer

NLTV Viewer is a self-contained Windows tray application that reads Nielsen Local TV View XLSX reports and serves a read-only quarter-hour ratings dashboard. The desktop window owns configuration and report refresh. The web view is designed for direct local use or reverse proxying through nginx under a configurable path base.

The repository contains no production report path, station name, show name, credentials, or copied ratings data. Machine-specific settings are written to `%LocalAppData%\NLTV Viewer\settings.json`.

## What it shows

For each configured logical show, the dashboard plots the selected demographic and metric for the chosen broadcast day and compares the same segment position with:

- 7 days earlier
- 14 days earlier
- 364 days earlier (the same weekday 52 weeks earlier)

Each quarter-hour also shows the absolute and percentage change. Missing data is distinct from a measured zero. A comparison against a zero baseline has no percentage value.

Audience and measure choices are discovered from each report instead of being compiled into the program. Current reports may, for example, offer HH, P25-54, M25-54, F25-54, P18-49, and P18+ across RTG, SHR, IMP, and HUT/PUT. The web toolbar only offers combinations actually present in the selected report, remembers the producer's last choice in that browser, and safely falls back when a date has different capabilities. The desktop app can optionally set a preferred initial pair; **Auto** uses the report's preferred pair (P18+/IMP when present, otherwise the first valid pair).

Program names often change between reports. A logical show therefore has a stable display name and one or more case-insensitive wildcard aliases. Optional viewing-source and time-window filters prevent broad aliases from matching unrelated programs. The comparison engine aligns quarter-hours by position within the matched show run and displays the actual report time when a show moved.

## Run from source

Requirements:

- Windows 10 or newer
- .NET SDK version selected by `global.json`

```powershell
dotnet restore NLTVViewer.slnx
dotnet test tests\NltvViewer.Tests\NltvViewer.Tests.csproj
dotnet run --project src\NltvViewer.App\NltvViewer.App.csproj
```

On first run, configure:

1. The report folder. A UNC path is more reliable than a mapped drive for unattended startup.
2. The listening port and optional nginx path base, such as `/ratings`.
3. An optional preferred demographic and metric after the first report scan. Leave both at **Auto** for report-driven defaults.
4. One row per logical show. Separate aliases with semicolons. `*` matches any text and `?` matches one character.
5. Optional source and start/end time constraints.
6. Whether the app starts at Windows sign-in.

Use **Test folder**, then **Save & apply**. Closing the settings window leaves the server in the notification area. Use the tray menu to reopen settings, refresh reports, open the viewer, or exit.

Use **Export settings** to save the current validated configuration as a versioned JSON file for transfer to another computer. Use **Import settings** on the destination computer to load and validate that file into the form. Importing does not restart the server, save local settings, or change Windows startup; review it and select **Save & apply** to make it active. The transfer file can contain private paths and show aliases, so keep it in an access-controlled location and do not commit it.

## Local data and privacy

The app stores the following under `%LocalAppData%\NLTV Viewer`:

- `settings.json` and its previous backup
- a versioned report cache used to avoid rereading unchanged network files
- size-limited diagnostic logs

The cache contains normalized report rows and discovered field metadata needed by the viewer. Detailed multi-metric values are loaded on demand into a bounded memory cache so large report archives do not create an oversized disk cache. Runtime data is ignored by Git. Tests create small anonymous XLSX files at runtime; no real report is checked in.

The configured report tree is treated as read-only source data. The app only enumerates directories and opens workbooks with read access; it never creates, edits, renames, or deletes anything there. It rejects cache paths and settings-export destinations inside the report tree. Automated tests verify the source workbook hash, timestamp, and directory contents remain unchanged after indexing.

## Report compatibility

The parser discovers viewing sources, demographics, program-name columns, and metrics from the workbook's multi-level headers rather than relying on fixed column letters or requiring specific demographic/metric names. It scans every worksheet and viewing source, validates 15-minute intervals, and uses the report's content date before considering the filename. Valid demographic/metric pairs are retained explicitly, including reports whose fields do not form a complete Cartesian product.

Legacy Nielsen program-summary workbooks that do not contain the Time Period Standard quarter-hour schema are reported as unsupported. They are never presented as zero ratings.

The catalog polls the folder because filesystem notifications are unreliable on network shares. It ignores Excel lock files, retries on later polls when a workbook is incomplete, selects the newest source when duplicate dates exist, and keeps the last good cached view during an outage.

## nginx

Keep the application bound to `127.0.0.1` when nginx runs on the same computer. Configure the desktop path base to `/ratings`, preserve that prefix in the proxy request, and redirect the prefix without a trailing slash:

```nginx
location = /ratings {
    return 301 /ratings/;
}

location /ratings/ {
    proxy_pass http://127.0.0.1:5085;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

All assets and API requests are path-base relative. The public surface is read-only:

- `GET /ratings/`
- `GET /ratings/api/v1/days`
- `GET /ratings/api/v1/capabilities`
- `GET /ratings/api/v1/ratings?date=YYYY-MM-DD&demographic=p18-plus&metric=imp`
- `GET /ratings/api/v1/status`
- `GET /ratings/health/live`
- `GET /ratings/health/ready`

nginx should own TLS and access control. If nginx runs on another computer, configure a specific LAN bind address and firewall rule; do not expose Kestrel directly to an untrusted network.

Both `demographic` and `metric` are optional together; omitting both uses the configured/report default. Supplying only one or an invalid pair returns a structured client error with the available capabilities. The parameterless behavior remains compatible with earlier P18+/IMP clients.

## Startup model

The toggle creates an HKCU Run entry and starts the tray app after that Windows user signs in. The process and web page stop when the user signs out. Mapped drives also belong to the interactive user and may not be ready immediately, so the app retains cached data and retries.

For a web page that must remain available before sign-in or after sign-out, deploy a separate Windows Service under an account with share access and use a UNC report path. The report, comparison, and gateway layers do not depend on Windows Forms so that hosting mode can be added without replacing the data engine.

## Build a portable release

```powershell
.\scripts\publish.ps1
```

The script runs the tests and produces a self-contained `win-x64` folder and ZIP under `artifacts`. No .NET installation is required on the destination computer. Extract it to its permanent location before enabling start at sign-in; moving the executable later requires disabling and re-enabling that option.

## Development

The projects are separated by responsibility:

- `NltvViewer.Core`: domain models, wildcard matching, and comparisons
- `NltvViewer.Infrastructure`: OpenXML parsing and network/persistent catalog
- `NltvViewer.App`: Windows configuration/tray host, HTTP API, and static web UI
- `NltvViewer.Tests`: anonymous parser, catalog, and comparison fixtures

Build warnings are treated as errors. Run `dotnet test` before publishing.
