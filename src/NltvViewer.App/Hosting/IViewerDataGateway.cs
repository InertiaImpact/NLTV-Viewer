using NltvViewer.App.Configuration;
using NltvViewer.Core.Comparisons;
using NltvViewer.Core.Configuration;
using NltvViewer.Core.Reports;
using NltvViewer.Infrastructure.Reports;

namespace NltvViewer.App.Hosting;

/// <summary>
/// Separates the Windows process and HTTP host from report ingestion. A Windows
/// Service can reuse an implementation without depending on Windows Forms.
/// </summary>
public sealed record ViewerDataStatus(
    DateTimeOffset? LastRefreshUtc,
    bool IsStale,
    bool SourceAvailable,
    string SourceDisplayName,
    string? ErrorCode,
    string Message,
    bool HasUsableReports,
    IReadOnlyList<string> Warnings);

public interface IViewerDataGateway : IDisposable
{
    ViewerDataStatus Status { get; }

    Task RefreshAsync(LocalApplicationSettings settings, CancellationToken cancellationToken);

    Task<object> GetDaysAsync(CancellationToken cancellationToken);

    Task<object?> GetRatingsAsync(DateOnly reportDate, CancellationToken cancellationToken);
}

public static class ViewerDataGatewayFactory
{
    public static IViewerDataGateway Create(LocalApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ViewerDataGateway(new ReportComparisonService(), settings);
    }
}

public sealed class ViewerDataGateway : IViewerDataGateway
{
    public const string NoUsableReportsErrorCode = "no_usable_reports";

    private readonly IReportComparisonService _comparisonService;
    private readonly object _catalogSync = new();
    private readonly object _statusSync = new();
    private readonly List<NetworkReportCatalog> _retiredCatalogs = [];
    private CatalogSnapshot _current;
    private bool _disposed;
    private ViewerDataStatus _status = new(
        null,
        true,
        false,
        string.Empty,
        "not_refreshed",
        "Reports have not been refreshed.",
        false,
        []);

    public ViewerDataGateway(
        IReportComparisonService comparisonService,
        LocalApplicationSettings initialSettings)
    {
        _comparisonService = comparisonService ?? throw new ArgumentNullException(nameof(comparisonService));
        ArgumentNullException.ThrowIfNull(initialSettings);
        _current = CreateSnapshot(initialSettings);
        _status = _status with
        {
            SourceDisplayName = _current.SourceDisplayName,
            HasUsableReports = _current.Catalog.AvailableDates.Count > 0,
        };
    }

    public ViewerDataStatus Status
    {
        get
        {
            lock (_statusSync)
            {
                return _status;
            }
        }
    }

    public async Task RefreshAsync(
        LocalApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Catalog replacement and settings capture are brief. The potentially
        // slow network enumeration/parsing happens after releasing the lock.
        var snapshot = CaptureOrReplace(settings);
        var result = await snapshot.Catalog
            .RefreshAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrent(snapshot))
        {
            return;
        }

        var sourceAvailable = result.Status is not (
            CatalogStatus.NotRefreshed
            or CatalogStatus.LocationNotFound
            or CatalogStatus.LocationInaccessible);
        var errorCode = DetermineErrorCode(result);
        var message = BuildRefreshMessage(result, snapshot.SourceDisplayName);
        var warnings = result.Warnings?.ToArray() ?? [];
        UpdateStatus(new ViewerDataStatus(
            result.CompletedAtUtc,
            result.Status != CatalogStatus.Available || result.StaleReports > 0,
            sourceAvailable,
            snapshot.SourceDisplayName,
            errorCode,
            message,
            snapshot.Catalog.AvailableDates.Count > 0,
            warnings));
        LocalLog.Write(
            sourceAvailable && errorCode is null ? "Information" : "Warning",
            "catalog_refreshed",
            $"{message} Warnings: {warnings.Length}.");
    }

    public Task<object> GetDaysAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = CaptureCurrent();
        var dates = snapshot.Catalog.AvailableDates.Order().ToArray();
        var status = Status;
        object response = new
        {
            days = dates.Select(reportDate => reportDate.ToString("yyyy-MM-dd", null)).ToArray(),
            latestAvailableDate = dates.Length == 0
                ? null
                : dates[^1].ToString("yyyy-MM-dd", null),
            isStale = status.IsStale,
            errorCode = status.ErrorCode,
            message = status.Message,
            warnings = status.Warnings,
        };
        return Task.FromResult(response);
    }

    public async Task<object?> GetRatingsAsync(
        DateOnly reportDate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = CaptureCurrent();
        var selected = await snapshot.Catalog
            .GetAsync(reportDate, cancellationToken)
            .ConfigureAwait(false);
        if (!selected.Found)
        {
            return null;
        }

        var comparisonReports = new Dictionary<DateOnly, RatingReport>();
        foreach (var daysBack in ReportComparisonService.Periods.Select(period => period.DaysBack))
        {
            var comparisonDate = reportDate.AddDays(-daysBack);
            var lookup = await snapshot.Catalog
                .GetAsync(comparisonDate, cancellationToken)
                .ConfigureAwait(false);
            if (lookup.Report is not null)
            {
                comparisonReports[comparisonDate] = lookup.Report;
            }
        }

        var comparison = _comparisonService.Build(
            selected.Report!,
            comparisonReports,
            snapshot.ShowGroups);
        var status = Status;
        return new
        {
            comparison.SelectedDate,
            comparison.SourceLastModifiedUtc,
            comparison.Shows,
            isStale = status.IsStale,
            errorCode = status.ErrorCode,
            message = status.Message,
            warnings = status.Warnings,
        };
    }

    public void Dispose()
    {
        List<NetworkReportCatalog> catalogs;
        lock (_catalogSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            catalogs = [.. _retiredCatalogs, _current.Catalog];
            _retiredCatalogs.Clear();
        }

        foreach (var catalog in catalogs)
        {
            catalog.Dispose();
        }
    }

    private CatalogSnapshot CaptureCurrent()
    {
        lock (_catalogSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current;
        }
    }

    private CatalogSnapshot CaptureOrReplace(LocalApplicationSettings settings)
    {
        var reportRoot = settings.ReportFolder.Trim();
        lock (_catalogSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.Equals(_current.ReportRoot, reportRoot, StringComparison.OrdinalIgnoreCase))
            {
                _current = _current with
                {
                    ShowGroups = CloneShowGroups(settings),
                    SourceDisplayName = DisplaySource(reportRoot),
                };
                return _current;
            }

            var replacement = CreateSnapshot(settings);
            _retiredCatalogs.Add(_current.Catalog);
            _current = replacement;
            return replacement;
        }
    }

    private bool IsCurrent(CatalogSnapshot snapshot)
    {
        lock (_catalogSync)
        {
            return !_disposed && ReferenceEquals(snapshot.Catalog, _current.Catalog);
        }
    }

    private static CatalogSnapshot CreateSnapshot(LocalApplicationSettings settings)
    {
        var reportRoot = settings.ReportFolder.Trim();
        var catalog = new NetworkReportCatalog(
            reportRoot,
            new NielsenOpenXmlReportParser(),
            GetCachePath(reportRoot));
        return new CatalogSnapshot(
            reportRoot,
            DisplaySource(reportRoot),
            catalog,
            CloneShowGroups(settings));
    }

    private static List<ShowGroupSettings> CloneShowGroups(
        LocalApplicationSettings settings) =>
        settings.ToViewerSettings().ShowGroups;

    private static string DisplaySource(string reportRoot) =>
        string.IsNullOrWhiteSpace(reportRoot) ? "the configured report location" : reportRoot;

    private static string GetCachePath(string reportRoot)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(reportRoot.ToUpperInvariant())))[..16];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NLTV Viewer",
            "cache",
            $"catalog-{hash}.json");
    }

    private static string? DetermineErrorCode(CatalogRefreshResult result)
    {
        if (result.Status == CatalogStatus.NoSupportedReports)
        {
            return NoUsableReportsErrorCode;
        }

        if (result.Status == CatalogStatus.NoReportFiles)
        {
            return "no_report_files";
        }

        return result.ErrorCode;
    }

    private static string BuildRefreshMessage(
        CatalogRefreshResult result,
        string sourceDisplayName)
    {
        if (result.Status is CatalogStatus.LocationNotFound or CatalogStatus.LocationInaccessible)
        {
            return $"Cannot access {sourceDisplayName}. Check desktop app/server for fileshare permissions.";
        }

        return result.Message ?? (result.Status == CatalogStatus.Available
            ? $"Indexed {result.ReportsIndexed} report(s) from {result.FilesExamined} file(s)."
            : "The report catalog could not be refreshed.");
    }

    public static bool IsReportFormatError(string? errorCode) =>
        errorCode is NoUsableReportsErrorCode or "no_reports_parsed";

    private void UpdateStatus(ViewerDataStatus status)
    {
        lock (_statusSync)
        {
            _status = status;
        }
    }

    private sealed record CatalogSnapshot(
        string ReportRoot,
        string SourceDisplayName,
        NetworkReportCatalog Catalog,
        IReadOnlyList<ShowGroupSettings> ShowGroups);
}
