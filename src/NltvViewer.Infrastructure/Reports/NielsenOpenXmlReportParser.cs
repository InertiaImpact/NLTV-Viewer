using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NltvViewer.Core.Filtering;
using NltvViewer.Core.Reports;

namespace NltvViewer.Infrastructure.Reports;

public sealed partial class NielsenOpenXmlReportParser : IVersionedReportParser
{
    private const string UnsupportedSchemaCode = "unsupported_nielsen_schema";

    public string CacheVersion => "nielsen-time-period-standard-v1";

    public ValueTask<ReportParseResult> ParseAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return ValueTask.FromResult(ReportParseResult.Failure(
                    ReportParseStatus.Unavailable,
                    "report_file_not_found",
                    $"The report file '{path}' does not exist."));
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var document = SpreadsheetDocument.Open(stream, false);
            return ValueTask.FromResult(ParseDocument(
                document,
                path,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return ValueTask.FromResult(ReportParseResult.Failure(
                ReportParseStatus.Unavailable,
                "report_access_denied",
                $"Cannot access '{path}'. Check the desktop app/server account's file-share permissions. {exception.Message}"));
        }
        catch (IOException exception)
        {
            return ValueTask.FromResult(ReportParseResult.Failure(
                ReportParseStatus.Unavailable,
                "report_io_error",
                $"Cannot read '{path}'. The network location may be unavailable or the report may still be copying. {exception.Message}"));
        }
        catch (Exception exception) when (
            exception is OpenXmlPackageException or InvalidDataException or FormatException)
        {
            return ValueTask.FromResult(ReportParseResult.Failure(
                ReportParseStatus.InvalidFile,
                "invalid_xlsx",
                $"'{path}' is not a valid readable XLSX report. {exception.Message}"));
        }
    }

    private static ReportParseResult ParseDocument(
        SpreadsheetDocument document,
        string path,
        DateTimeOffset lastModifiedUtc,
        CancellationToken cancellationToken)
    {
        var workbookPart = document.WorkbookPart;
        var sheets = workbookPart?.Workbook.Sheets?.Elements<Sheet>().ToArray();
        if (workbookPart is null || sheets is null || sheets.Length == 0)
        {
            return ReportParseResult.Failure(
                ReportParseStatus.InvalidFile,
                "missing_workbook",
                "The XLSX package does not contain a workbook with worksheets.");
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray() ?? [];

        var reports = new List<RatingReport>();
        var warnings = new List<string>();

        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { Length: > 0 } relationshipId ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                warnings.Add($"Worksheet '{sheet.Name?.Value ?? "(unnamed)"}' could not be opened.");
                continue;
            }

            var worksheetName = sheet.Name?.Value ?? "(unnamed)";
            var rows = worksheetPart.Worksheet.GetFirstChild<SheetData>()?
                .Elements<Row>()
                .Select(row => ReadRow(row, sharedStrings))
                .Where(row => row.Cells.Count > 0)
                .ToArray() ?? [];

            var parsed = ParseWorksheet(rows, path, worksheetName, lastModifiedUtc);
            if (parsed.Report is not null)
            {
                reports.Add(parsed.Report);
            }
            else if (parsed.LookedLikeCurrentSchema || sheets.Length == 1)
            {
                warnings.Add($"Worksheet '{worksheetName}': {parsed.Message}");
            }
        }

        if (reports.Count == 0)
        {
            return ReportParseResult.Failure(
                ReportParseStatus.UnsupportedFormat,
                UnsupportedSchemaCode,
                warnings.Count == 0
                    ? "No worksheet contains the Nielsen Time Period Standard 15-minute schema."
                    : string.Join(" ", warnings));
        }

        return ReportParseResult.Success(reports, warnings);
    }

    private static WorksheetParseResult ParseWorksheet(
        IReadOnlyList<RowValues> rows,
        string path,
        string worksheetName,
        DateTimeOffset lastModifiedUtc)
    {
        var metricIndex = FindMetricHeaderIndex(rows);
        if (metricIndex < 0)
        {
            return new(null, false, "The 15-minute metric header was not found.");
        }

        var metricRow = rows[metricIndex];
        var demoIndex = FindPreviousRow(rows, metricIndex, row =>
            row.Cells.Values.Any(value => HeaderEquals(value, "Demo")));
        var sourceIndex = demoIndex < 0
            ? -1
            : FindPreviousRow(rows, demoIndex, row =>
                row.Cells.Values.Any(value => HeaderEquals(value, "Viewing Source")));

        if (demoIndex < 0 || sourceIndex < 0)
        {
            return new(null, true, "Viewing Source and demographic header levels were not found.");
        }

        var timeColumn = metricRow.Cells
            .FirstOrDefault(pair => HeaderEquals(pair.Value, "Time / Metrics"))
            .Key;
        if (timeColumn <= 0)
        {
            return new(null, true, "The Time / Metrics column was not found.");
        }

        var bindings = FindBindings(rows[sourceIndex], rows[demoIndex], metricRow, timeColumn);
        if (bindings.Count == 0)
        {
            return new(null, true, "No Viewing Source contains demographic program and metric columns.");
        }

        var demographics = bindings
            .Select(binding => binding.Demographic)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var metrics = bindings
            .SelectMany(binding => binding.Metrics)
            .Select(binding => binding.Definition)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selections = bindings
            .SelectMany(binding => binding.Metrics.Select(metric =>
                new RatingSelection(binding.Demographic.Id, metric.Definition.Id)))
            .DistinctBy(
                selection => $"{selection.DemographicId}\u001f{selection.MetricId}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preferred = selections.FirstOrDefault(selection =>
            string.Equals(selection.DemographicId, "p18-plus", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selection.MetricId, "imp", StringComparison.OrdinalIgnoreCase)) ??
            selections.First();
        var capabilities = new RatingCapabilities(
            demographics,
            metrics,
            selections,
            preferred.DemographicId,
            preferred.MetricId);
        var selectionIndexes = selections
            .Select((selection, index) => new
            {
                Key = SelectionKey(selection.DemographicId, selection.MetricId),
                Index = index
            })
            .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);

        if (!TryFindReportDate(rows, metricIndex, worksheetName, path, out var reportDate, out var dateMessage))
        {
            return new(null, true, dateMessage);
        }

        var segments = new List<RatingSegment>();
        var broadcastOrder = 0;
        for (var index = metricIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!row.Cells.TryGetValue(timeColumn, out var timeLabel) ||
                !TryParseInterval(timeLabel, out var startMinute, out var endMinute))
            {
                continue;
            }

            foreach (var sourceBindings in bindings.GroupBy(
                         binding => binding.Source,
                         StringComparer.OrdinalIgnoreCase))
            {
                var program = sourceBindings
                    .Select(binding => row.Cells.GetValueOrDefault(binding.ProgramColumn))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(program))
                {
                    continue;
                }

                var values = new decimal?[selections.Length];
                foreach (var binding in sourceBindings)
                {
                    foreach (var metric in binding.Metrics)
                    {
                        if (row.Cells.TryGetValue(metric.Column, out var rawValue) &&
                            decimal.TryParse(
                                rawValue,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var parsedValue))
                        {
                            values[selectionIndexes[SelectionKey(
                                binding.Demographic.Id,
                                metric.Definition.Id)]] = parsedValue;
                        }
                    }
                }

                var impressionIndex = selectionIndexes.GetValueOrDefault(
                    SelectionKey("p18-plus", "imp"),
                    -1);
                var impressions = impressionIndex >= 0 ? values[impressionIndex] : null;

                segments.Add(new RatingSegment(
                    sourceBindings.Key,
                    GlobMatcher.Normalize(program),
                    GlobMatcher.Normalize(timeLabel),
                    startMinute,
                    endMinute,
                    broadcastOrder,
                    impressions,
                    values));
            }

            broadcastOrder++;
        }

        if (broadcastOrder == 0)
        {
            return new(null, true, "No valid 15-minute data rows were found.");
        }

        if (segments.Count == 0)
        {
            return new(null, true, "The worksheet contains 15-minute rows but no demographic program data.");
        }

        return new(
            new RatingReport(
                reportDate,
                path,
                worksheetName,
                lastModifiedUtc,
                segments,
                capabilities),
            true,
            null);
    }

    private static int FindMetricHeaderIndex(IReadOnlyList<RowValues> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var cells = rows[index].Cells;
            var timeColumn = cells.FirstOrDefault(pair => HeaderEquals(pair.Value, "Time / Metrics")).Key;
            var programColumns = cells
                .Where(pair => pair.Key > timeColumn && HeaderEquals(pair.Value, "Dom Pgm Name"))
                .Select(pair => pair.Key)
                .ToArray();
            if (timeColumn > 0 &&
                programColumns.Length > 0 &&
                cells.Any(pair => pair.Key > programColumns[0] && !HeaderEquals(pair.Value, "Dom Pgm Name")))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindPreviousRow(
        IReadOnlyList<RowValues> rows,
        int beforeIndex,
        Func<RowValues, bool> predicate)
    {
        for (var index = beforeIndex - 1; index >= 0; index--)
        {
            if (predicate(rows[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static List<DemographicBinding> FindBindings(
        RowValues sourceRow,
        RowValues demoRow,
        RowValues metricRow,
        int timeColumn)
    {
        var demoStarts = demoRow.Cells
            .Where(pair => pair.Key > timeColumn && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
        var sourceStarts = sourceRow.Cells
            .Where(pair => pair.Key > timeColumn && !HeaderEquals(pair.Value, "Viewing Source"))
            .OrderBy(pair => pair.Key)
            .ToArray();
        var bindings = new List<DemographicBinding>();

        for (var index = 0; index < demoStarts.Length; index++)
        {
            var start = demoStarts[index];
            if (!demoRow.Cells.TryGetValue(start, out var demo) || string.IsNullOrWhiteSpace(demo))
            {
                continue;
            }

            var end = index + 1 < demoStarts.Length ? demoStarts[index + 1] - 1 : int.MaxValue;
            var metrics = metricRow.Cells.Where(pair => pair.Key >= start && pair.Key <= end).ToArray();
            var programColumn = metrics.FirstOrDefault(pair => HeaderEquals(pair.Value, "Dom Pgm Name")).Key;
            var metricBindings = metrics
                .Where(pair => pair.Key != programColumn && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => new MetricBinding(
                    RatingFieldDefinition.CreateMetric(pair.Value),
                    pair.Key))
                .ToArray();
            var source = sourceStarts.LastOrDefault(pair => pair.Key <= start).Value;

            if (programColumn > 0 && metricBindings.Length > 0 && !string.IsNullOrWhiteSpace(source))
            {
                bindings.Add(new DemographicBinding(
                    GlobMatcher.Normalize(source),
                    RatingFieldDefinition.CreateDemographic(demo),
                    programColumn,
                    metricBindings));
            }
        }

        return bindings;
    }

    private static RowValues ReadRow(Row row, IReadOnlyList<string> sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = GetColumnIndex(cell.CellReference?.Value);
            if (column <= 0)
            {
                continue;
            }

            var value = ReadCell(cell, sharedStrings);
            if (!string.IsNullOrWhiteSpace(value))
            {
                cells[column] = value;
            }
        }

        return new RowValues(row.RowIndex?.Value ?? 0, cells);
    }

    private static string? ReadCell(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        return cell.CellValue?.Text ?? cell.InnerText;
    }

    private static int GetColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return 0;
        }

        var result = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            result = checked(result * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }

        return result;
    }

    private static bool HeaderEquals(string? actual, string expected) =>
        string.Equals(
            GlobMatcher.Normalize(actual ?? string.Empty),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static string SelectionKey(string demographicId, string metricId) =>
        $"{demographicId}\u001f{metricId}";

    private static bool TryFindReportDate(
        IReadOnlyList<RowValues> rows,
        int metricIndex,
        string worksheetName,
        string path,
        out DateOnly reportDate,
        out string message)
    {
        foreach (var row in rows.Take(metricIndex))
        {
            var text = string.Join(" ", row.Cells.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            var match = DateRangeRegex().Match(text);
            if (!match.Success ||
                !TryParseDate(match.Groups[1].Value, out var start) ||
                !TryParseDate(match.Groups[2].Value, out var end))
            {
                continue;
            }

            if (start != end)
            {
                reportDate = default;
                message = $"The report covers {start:yyyy-MM-dd} through {end:yyyy-MM-dd}; daily 15-minute data is required.";
                return false;
            }

            reportDate = start;
            message = string.Empty;
            return true;
        }

        if (TryParseDateHint(worksheetName, path, out reportDate))
        {
            message = string.Empty;
            return true;
        }

        message = "A single report date could not be determined from the worksheet or filename.";
        return false;
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            ["M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static bool TryParseDateHint(string worksheetName, string path, out DateOnly date)
    {
        foreach (var candidate in new[] { worksheetName, Path.GetFileNameWithoutExtension(path) })
        {
            var match = DateHintRegex().Match(candidate);
            if (!match.Success)
            {
                continue;
            }

            var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            year = year < 100 ? year + 2000 : year;
            if (DateOnly.TryParseExact(
                $"{match.Groups[1].Value}/{match.Groups[2].Value}/{year}",
                "M/d/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
            {
                return true;
            }
        }

        date = default;
        return false;
    }

    private static bool TryParseInterval(string value, out int startMinute, out int endMinute)
    {
        var match = TimeRangeRegex().Match(value);
        if (!match.Success ||
            !TryParseClock(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, out startMinute) ||
            !TryParseClock(match.Groups[4].Value, match.Groups[5].Value, match.Groups[6].Value, out endMinute))
        {
            startMinute = 0;
            endMinute = 0;
            return false;
        }

        return (endMinute - startMinute + 1440) % 1440 == 15;
    }

    private static bool TryParseClock(
        string hourText,
        string minuteText,
        string meridiem,
        out int minuteOfDay)
    {
        if (!int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out var minute) ||
            hour is < 1 or > 12 || minute is < 0 or > 59)
        {
            minuteOfDay = 0;
            return false;
        }

        hour %= 12;
        if (meridiem.Equals("pm", StringComparison.OrdinalIgnoreCase))
        {
            hour += 12;
        }

        minuteOfDay = hour * 60 + minute;
        return true;
    }

    [GeneratedRegex(@"\bDates\s*:\s*(\d{1,2}/\d{1,2}/\d{2,4})\s*-\s*(\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRangeRegex();

    [GeneratedRegex(@"^\s*(\d{1,2})[.-](\d{1,2})[.-](\d{2,4})(?:\D|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DateHintRegex();

    [GeneratedRegex(@"^\s*(\d{1,2}):(\d{2})\s*(am|pm)\s*-\s*(\d{1,2}):(\d{2})\s*(am|pm)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeRangeRegex();

    private sealed record RowValues(uint RowNumber, IReadOnlyDictionary<int, string> Cells);

    private sealed record DemographicBinding(
        string Source,
        RatingDemographicDefinition Demographic,
        int ProgramColumn,
        IReadOnlyList<MetricBinding> Metrics);

    private sealed record MetricBinding(RatingMetricDefinition Definition, int Column);

    private sealed record WorksheetParseResult(
        RatingReport? Report,
        bool LookedLikeCurrentSchema,
        string? Message);
}
