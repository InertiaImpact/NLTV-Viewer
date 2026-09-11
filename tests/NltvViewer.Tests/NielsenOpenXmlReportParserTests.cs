using NltvViewer.Core.Reports;
using NltvViewer.Infrastructure.Reports;

namespace NltvViewer.Tests;

public sealed class NielsenOpenXmlReportParserTests
{
    private readonly NielsenOpenXmlReportParser _parser = new();

    [Fact]
    public async Task ParseAsyncDiscoversEverySourceAndPreservesMissingAndZero()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("anonymous-report.xlsx");
        SyntheticNielsenWorkbook.CreateCurrent(
            path,
            new DateOnly(2026, 9, 4),
            [
                new("6:00pm - 6:15pm", "ALPHA NWS-AT 6", 5585m, "ALPHA NEWS-AT 9", 0m),
                new("6:15pm - 6:30pm", "ALPHA NWS-AT 6", null, "ALPHA NEWS-AT 9", 12508m)
            ],
            includeUnsupportedSheet: true);

        var result = await _parser.ParseAsync(path);

        Assert.Equal(ReportParseStatus.Success, result.Status);
        var report = Assert.Single(result.Reports);
        Assert.Equal(new DateOnly(2026, 9, 4), report.ReportDate);
        Assert.Equal("9.4.26 Daily", report.WorksheetName);
        Assert.Equal(4, report.Segments.Count);

        var first = Assert.Single(report.Segments, segment =>
            segment.ViewingSource == "ALPHA 1.1" && segment.StartMinute == 18 * 60);
        Assert.Equal(5585m, first.P18PlusImpressions);
        Assert.Equal(0, first.BroadcastOrder);

        var missing = Assert.Single(report.Segments, segment =>
            segment.ViewingSource == "ALPHA 1.1" && segment.StartMinute == 18 * 60 + 15);
        Assert.Null(missing.P18PlusImpressions);

        var zero = Assert.Single(report.Segments, segment =>
            segment.ViewingSource == "ALPHA 1.2" && segment.StartMinute == 18 * 60);
        Assert.Equal(0m, zero.P18PlusImpressions);
    }

    [Fact]
    public async Task ParseAsyncRejectsMultiDayAverageAsUnsupported()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("anonymous-range.xlsx");
        SyntheticNielsenWorkbook.CreateCurrent(
            path,
            new DateOnly(2026, 9, 1),
            [new("6:00pm - 6:15pm", "ALPHA NEWS", 100m)],
            rangeEnd: new DateOnly(2026, 9, 5));

        var result = await _parser.ParseAsync(path);

        Assert.Equal(ReportParseStatus.UnsupportedFormat, result.Status);
        Assert.Equal("unsupported_nielsen_schema", result.ErrorCode);
        Assert.Contains("daily 15-minute data is required", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsyncClassifiesUnsupportedAndCorruptFiles()
    {
        using var directory = new TemporaryDirectory();
        var unsupportedPath = directory.File("summary.xlsx");
        SyntheticNielsenWorkbook.CreateUnsupported(unsupportedPath);
        var corruptPath = directory.File("corrupt.xlsx");
        await File.WriteAllTextAsync(corruptPath, "This is deliberately not an XLSX package.");

        var unsupported = await _parser.ParseAsync(unsupportedPath);
        var corrupt = await _parser.ParseAsync(corruptPath);

        Assert.Equal(ReportParseStatus.UnsupportedFormat, unsupported.Status);
        Assert.Equal(ReportParseStatus.InvalidFile, corrupt.Status);
        Assert.Equal("invalid_xlsx", corrupt.ErrorCode);
    }

    [Fact]
    public async Task ParseAsyncClassifiesMissingFileAsUnavailable()
    {
        using var directory = new TemporaryDirectory();

        var result = await _parser.ParseAsync(directory.File("missing.xlsx"));

        Assert.Equal(ReportParseStatus.Unavailable, result.Status);
        Assert.Equal("report_file_not_found", result.ErrorCode);
    }
}
