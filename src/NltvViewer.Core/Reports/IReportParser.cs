namespace NltvViewer.Core.Reports;

public interface IReportParser
{
    ValueTask<ReportParseResult> ParseAsync(string path, CancellationToken cancellationToken = default);
}

public interface IVersionedReportParser : IReportParser
{
    string CacheVersion { get; }
}

public interface IReportCatalog
{
    IReadOnlyList<DateOnly> AvailableDates { get; }

    CatalogRefreshResult LastRefresh { get; }

    ValueTask<CatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);

    ValueTask<ReportLookupResult> GetAsync(DateOnly reportDate, CancellationToken cancellationToken = default);
}

public enum CatalogStatus
{
    NotRefreshed,
    Available,
    NoReportFiles,
    NoSupportedReports,
    LocationNotFound,
    LocationInaccessible,
    Error
}

public sealed record CatalogRefreshResult(
    CatalogStatus Status,
    int FilesExamined,
    int ReportsIndexed,
    DateTimeOffset CompletedAtUtc,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyList<string>? Warnings = null,
    int StaleReports = 0)
{
    public static CatalogRefreshResult NotRefreshed { get; } =
        new(CatalogStatus.NotRefreshed, 0, 0, DateTimeOffset.MinValue);
}

public sealed record ReportLookupResult(
    DateOnly RequestedDate,
    RatingReport? Report,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool Found => Report is not null;
}
