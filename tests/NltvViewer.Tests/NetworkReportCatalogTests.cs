using NltvViewer.Core.Reports;
using NltvViewer.Infrastructure.Reports;
using System.Security.Cryptography;

namespace NltvViewer.Tests;

public sealed class NetworkReportCatalogTests
{
    [Fact]
    public async Task RefreshAsyncIndexesSupportedReportsAndIgnoresExcelLockFiles()
    {
        using var directory = new TemporaryDirectory();
        var reportPath = directory.File("anonymous.xlsx");
        SyntheticNielsenWorkbook.CreateCurrent(
            reportPath,
            new DateOnly(2026, 9, 4),
            [new("6:00pm - 6:15pm", "ALPHA NEWS", 100m)]);
        await File.WriteAllTextAsync(directory.File("~$anonymous.xlsx"), "lock");
        using var catalog = new NetworkReportCatalog(directory.Path, new NielsenOpenXmlReportParser());

        var refresh = await catalog.RefreshAsync();
        var lookup = await catalog.GetAsync(new DateOnly(2026, 9, 4));

        Assert.Equal(CatalogStatus.Available, refresh.Status);
        Assert.Equal(1, refresh.FilesExamined);
        Assert.Equal(1, refresh.ReportsIndexed);
        Assert.Equal([new DateOnly(2026, 9, 4)], catalog.AvailableDates);
        Assert.True(lookup.Found);
    }

    [Fact]
    public async Task RefreshAsyncReusesMemoryAndPersistentCacheByFileStamp()
    {
        using var directory = new TemporaryDirectory();
        var reportRoot = directory.File("reports");
        Directory.CreateDirectory(reportRoot);
        var reportPath = Path.Combine(reportRoot, "synthetic.xlsx");
        var cachePath = directory.File("cache.json");
        await File.WriteAllTextAsync(reportPath, "stable-stamp");
        var firstParser = new CountingParser();

        using (var firstCatalog = new NetworkReportCatalog(reportRoot, firstParser, cachePath))
        {
            await firstCatalog.RefreshAsync();
            await firstCatalog.RefreshAsync();
            Assert.Equal(1, firstParser.Count);
        }

        var secondParser = new CountingParser();
        using var secondCatalog = new NetworkReportCatalog(reportRoot, secondParser, cachePath);
        Assert.Contains(CountingParser.ReportDate, secondCatalog.AvailableDates);
        await secondCatalog.RefreshAsync();

        Assert.Equal(0, secondParser.Count);
        Assert.True((await secondCatalog.GetAsync(CountingParser.ReportDate)).Found);
    }

    [Fact]
    public async Task RefreshAsyncPreservesLastGoodSnapshotWhenLocationDisappears()
    {
        var parent = new TemporaryDirectory();
        try
        {
            var reportRoot = parent.File("reports");
            Directory.CreateDirectory(reportRoot);
            var reportPath = Path.Combine(reportRoot, "synthetic.xlsx");
            SyntheticNielsenWorkbook.CreateCurrent(
                reportPath,
                new DateOnly(2026, 9, 4),
                [new("6:00pm - 6:15pm", "ALPHA NEWS", 100m)]);
            using var catalog = new NetworkReportCatalog(reportRoot, new NielsenOpenXmlReportParser());
            await catalog.RefreshAsync();
            File.Delete(reportPath);
            Directory.Delete(reportRoot);

            var failedRefresh = await catalog.RefreshAsync();

            Assert.Equal(CatalogStatus.LocationNotFound, failedRefresh.Status);
            Assert.Contains(new DateOnly(2026, 9, 4), catalog.AvailableDates);
            Assert.True((await catalog.GetAsync(new DateOnly(2026, 9, 4))).Found);
        }
        finally
        {
            parent.Dispose();
        }
    }

    [Fact]
    public async Task RefreshAsyncReturnsExplicitStatusForMissingLocation()
    {
        using var directory = new TemporaryDirectory();
        using var catalog = new NetworkReportCatalog(
            directory.File("does-not-exist"),
            new NielsenOpenXmlReportParser());

        var result = await catalog.RefreshAsync();

        Assert.Equal(CatalogStatus.LocationNotFound, result.Status);
        Assert.Equal("report_location_not_found", result.ErrorCode);
        Assert.Contains("file-share permissions", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsyncDistinguishesEmptyFolderFromUnsupportedWorkbooks()
    {
        using var directory = new TemporaryDirectory();
        var emptyRoot = directory.File("empty");
        var unsupportedRoot = directory.File("unsupported");
        Directory.CreateDirectory(emptyRoot);
        Directory.CreateDirectory(unsupportedRoot);
        SyntheticNielsenWorkbook.CreateUnsupported(Path.Combine(unsupportedRoot, "summary.xlsx"));
        using var emptyCatalog = new NetworkReportCatalog(emptyRoot, new NielsenOpenXmlReportParser());
        using var unsupportedCatalog = new NetworkReportCatalog(
            unsupportedRoot,
            new NielsenOpenXmlReportParser());

        var empty = await emptyCatalog.RefreshAsync();
        var unsupported = await unsupportedCatalog.RefreshAsync();

        Assert.Equal(CatalogStatus.NoReportFiles, empty.Status);
        Assert.Equal("no_report_files", empty.ErrorCode);
        Assert.Equal(CatalogStatus.NoSupportedReports, unsupported.Status);
        Assert.Equal("no_supported_reports", unsupported.ErrorCode);
        Assert.NotEmpty(unsupported.Warnings ?? []);
        Assert.Contains("none use the supported", unsupported.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsyncRetainsLastGoodReportWhenChangedFileTemporarilyFails()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.File("report.xlsx");
        await File.WriteAllTextAsync(source, "first-version");
        var parser = new TransientParser();
        using var catalog = new NetworkReportCatalog(directory.Path, parser);
        await catalog.RefreshAsync();
        await File.AppendAllTextAsync(source, "-changed");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        parser.Fail = true;

        var refresh = await catalog.RefreshAsync();

        Assert.Equal(CatalogStatus.Available, refresh.Status);
        Assert.Equal(1, refresh.StaleReports);
        Assert.True((await catalog.GetAsync(CountingParser.ReportDate)).Found);
        Assert.Contains(
            refresh.Warnings ?? [],
            warning => warning.Contains("stale cached data", StringComparison.OrdinalIgnoreCase));

        await catalog.RefreshAsync();
        Assert.Equal(3, parser.Count);
    }

    [Fact]
    public async Task RefreshAsyncNeverModifiesOrAddsFilesInSourceTree()
    {
        using var directory = new TemporaryDirectory();
        var sourceRoot = directory.File("source");
        var localCache = directory.File("local-cache/catalog.json");
        Directory.CreateDirectory(sourceRoot);
        var workbook = Path.Combine(sourceRoot, "anonymous.xlsx");
        SyntheticNielsenWorkbook.CreateCurrent(
            workbook,
            new DateOnly(2026, 9, 4),
            [new("6:00pm - 6:15pm", "ALPHA NEWS", 100m)]);
        var beforeFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(workbook));
        var beforeWriteTime = File.GetLastWriteTimeUtc(workbook);
        using var catalog = new NetworkReportCatalog(
            sourceRoot,
            new NielsenOpenXmlReportParser(),
            localCache);

        await catalog.RefreshAsync();

        var afterFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
        var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(workbook));
        Assert.Equal(beforeFiles, afterFiles);
        Assert.Equal(beforeHash, afterHash);
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(workbook));
        Assert.True(File.Exists(localCache));
    }

    [Fact]
    public void ConstructorRejectsCachePathInsideReadOnlySourceTree()
    {
        using var directory = new TemporaryDirectory();

        var exception = Assert.Throws<ArgumentException>(() => new NetworkReportCatalog(
            directory.Path,
            new NielsenOpenXmlReportParser(),
            directory.File("cache/catalog.json")));

        Assert.Equal("localCachePath", exception.ParamName);
    }

    private sealed class CountingParser : IVersionedReportParser
    {
        public static DateOnly ReportDate { get; } = new(2026, 9, 4);

        public int Count { get; private set; }

        public string CacheVersion => "counting-parser-v1";

        public ValueTask<ReportParseResult> ParseAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            var report = new RatingReport(
                ReportDate,
                path,
                "Synthetic",
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                [new RatingSegment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 1095, 0, 100m)]);
            return ValueTask.FromResult(ReportParseResult.Success([report]));
        }
    }

    private sealed class TransientParser : IVersionedReportParser
    {
        public int Count { get; private set; }

        public bool Fail { get; set; }

        public string CacheVersion => "transient-parser-v1";

        public ValueTask<ReportParseResult> ParseAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            if (Fail)
            {
                return ValueTask.FromResult(ReportParseResult.Failure(
                    ReportParseStatus.Unavailable,
                    "temporary_failure",
                    "The synthetic file is temporarily unavailable."));
            }

            var report = new RatingReport(
                CountingParser.ReportDate,
                path,
                "Synthetic",
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                [new RatingSegment("ALPHA 1.1", "ALPHA NEWS", "6:00pm - 6:15pm", 1080, 1095, 0, 100m)]);
            return ValueTask.FromResult(ReportParseResult.Success([report]));
        }
    }
}
