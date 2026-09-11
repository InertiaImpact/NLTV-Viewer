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

    [Fact]
    public async Task ParseAsyncDiscoversDynamicCapabilitiesAndCompactValues()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("dynamic-report.xlsx");
        SyntheticNielsenWorkbook.CreateDynamic(path, new DateOnly(2026, 9, 4));

        var result = await _parser.ParseAsync(path);

        var report = Assert.Single(result.Reports);
        var capabilities = Assert.IsType<RatingCapabilities>(report.Capabilities);
        Assert.Equal(["hh", "p18-plus", "p25-54"], capabilities.Demographics.Select(item => item.Id));
        Assert.Equal(["rtg", "shr", "imp", "hut-put"], capabilities.Metrics.Select(item => item.Id));
        Assert.Equal(9, capabilities.AvailableSelections.Count);
        Assert.DoesNotContain(
            capabilities.AvailableSelections,
            selection => selection == new RatingSelection("p25-54", "rtg"));
        Assert.Contains(
            capabilities.AvailableSelections,
            selection => selection == new RatingSelection("p25-54", "imp"));
        Assert.Equal("p18-plus", capabilities.DefaultDemographicId);
        Assert.Equal("imp", capabilities.DefaultMetricId);

        var rating = Assert.Single(capabilities.Metrics, metric => metric.Id == "rtg");
        Assert.Equal("percent", rating.Unit);
        Assert.Equal(1, rating.Decimals);
        var share = Assert.Single(capabilities.Metrics, metric => metric.Id == "shr");
        Assert.Equal(1, share.Decimals);
        var hutPut = Assert.Single(capabilities.Metrics, metric => metric.Id == "hut-put");
        Assert.Equal(1, hutPut.Decimals);
        var impressions = Assert.Single(capabilities.Metrics, metric => metric.Id == "imp");
        Assert.Equal("impressions", impressions.Unit);
        Assert.Equal(0, impressions.Decimals);

        Assert.Equal(2, report.Segments.Count);
        var primary = Assert.Single(report.Segments, segment => segment.ViewingSource == "ALPHA 1.1");
        Assert.Equal(1.25m, report.GetValue(primary, "hh", "rtg"));
        Assert.Equal(2000m, report.GetValue(primary, "p18-plus", "imp"));
        Assert.Equal(2000m, primary.P18PlusImpressions);
        var alternate = Assert.Single(report.Segments, segment => segment.ViewingSource == "ALPHA 1.2");
        Assert.Equal(0m, report.GetValue(alternate, "hh", "rtg"));
        Assert.Equal(4000m, report.GetValue(alternate, "p25-54", "imp"));
        Assert.Null(report.GetValue(alternate, "p18-plus", "imp"));
        Assert.Throws<ArgumentException>(() => capabilities.Resolve(new RatingSelection("p25-54", "rtg")));
    }

    [Fact]
    public async Task ParseAsyncDoesNotRequirePreferredDemographicOrMetric()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("different-fields.xlsx");
        SyntheticNielsenWorkbook.CreateWithoutPreferredFields(path, new DateOnly(2026, 9, 4));

        var result = await _parser.ParseAsync(path);

        Assert.Equal(ReportParseStatus.Success, result.Status);
        var report = Assert.Single(result.Reports);
        Assert.Equal("p12-17", report.EffectiveCapabilities.DefaultDemographicId);
        Assert.Equal("index", report.EffectiveCapabilities.DefaultMetricId);
        var segment = Assert.Single(report.Segments);
        Assert.Equal(7.5m, report.GetValue(segment, "p12-17", "index"));
        Assert.Null(segment.P18PlusImpressions);
    }
}
