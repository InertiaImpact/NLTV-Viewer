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
    RatingCapabilities Capabilities,
    IReadOnlyList<string> Warnings);

public interface IViewerDataGateway : IDisposable
{
    ViewerDataStatus Status { get; }

    Task RefreshAsync(LocalApplicationSettings settings, CancellationToken cancellationToken);

    Task<object> GetDaysAsync(CancellationToken cancellationToken);

    Task<object?> GetRatingsAsync(
        DateOnly reportDate,
        string? demographicId,
        string? metricId,
        CancellationToken cancellationToken);
}

internal sealed class RatingSelectionException : Exception
{
    public RatingSelectionException(
        string message,
        RatingCapabilities capabilities,
        bool isInvalidRequest,
        Exception innerException)
        : base(message, innerException)
    {
        Capabilities = capabilities;
        IsInvalidRequest = isInvalidRequest;
    }

    public RatingCapabilities Capabilities { get; }

    public bool IsInvalidRequest { get; }
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
        RatingCapabilities.Empty,
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
        var capabilities = await GetLatestCapabilitiesAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        UpdateStatus(new ViewerDataStatus(
            result.CompletedAtUtc,
            result.Status != CatalogStatus.Available || result.StaleReports > 0,
            sourceAvailable,
            snapshot.SourceDisplayName,
            errorCode,
            message,
            snapshot.Catalog.AvailableDates.Count > 0,
            capabilities,
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
            capabilities = status.Capabilities,
        };
        return Task.FromResult(response);
    }

    public async Task<object?> GetRatingsAsync(
        DateOnly reportDate,
        string? demographicId,
        string? metricId,
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

        ComparisonView comparison;
        try
        {
            var selection = ResolveSelection(
                selected.Report!,
                snapshot,
                demographicId,
                metricId);
            comparison = _comparisonService.Build(
                selected.Report!,
                comparisonReports,
                snapshot.ShowGroups,
                selection);
        }
        catch (ArgumentException exception)
        {
            throw new RatingSelectionException(
                exception.Message,
                selected.Report!.EffectiveCapabilities,
                isInvalidRequest: true,
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new RatingSelectionException(
                exception.Message,
                selected.Report!.EffectiveCapabilities,
                isInvalidRequest: false,
                exception);
        }
        var status = Status;
        var legacySelection = string.Equals(
                comparison.Selection.DemographicId,
                "p18-plus",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                comparison.Selection.MetricId,
                "imp",
                StringComparison.OrdinalIgnoreCase);
        return new
        {
            comparison.SelectedDate,
            comparison.SourceLastModifiedUtc,
            shows = comparison.Shows.Select(show => new
            {
                show.GroupId,
                show.DisplayName,
                show.ViewingSource,
                show.ProgramName,
                show.State,
                show.StateMessage,
                segments = show.Segments.Select(segment => new
                {
                    segment.SegmentNumber,
                    current = new
                    {
                        segment.Current.ViewingSource,
                        segment.Current.ProgramName,
                        segment.Current.TimeLabel,
                        segment.Current.StartMinute,
                        segment.Current.EndMinute,
                        segment.Current.BroadcastOrder,
                        p18PlusImpressions = legacySelection
                            ? segment.Current.P18PlusImpressions
                            : null,
                    },
                    segment.Value,
                    comparisons = segment.Comparisons.Select(period => new
                    {
                        period.Period,
                        period.TargetDate,
                        period.ActualTimeLabel,
                        period.ActualProgramName,
                        impressions = legacySelection ? period.Impressions : null,
                        period.Delta,
                        period.Value,
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
            comparison.Capabilities,
            comparison.Selection,
            demographic = comparison.Selection.DemographicId,
            metric = comparison.Selection.MetricId,
            isStale = status.IsStale,
            errorCode = status.ErrorCode,
            message = status.Message,
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
                    PreferredDemographicId = NormalizeOptionalId(settings.DefaultDemographicId),
                    PreferredMetricId = NormalizeOptionalId(settings.DefaultMetricId),
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
            CloneShowGroups(settings),
            NormalizeOptionalId(settings.DefaultDemographicId),
            NormalizeOptionalId(settings.DefaultMetricId));
    }

    private static List<ShowGroupSettings> CloneShowGroups(
        LocalApplicationSettings settings) =>
        settings.ToViewerSettings().ShowGroups;

    private static string DisplaySource(string reportRoot) =>
        string.IsNullOrWhiteSpace(reportRoot) ? "the configured report location" : reportRoot;

    private static string? NormalizeOptionalId(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static RatingSelection? ResolveSelection(
        RatingReport report,
        CatalogSnapshot snapshot,
        string? demographicId,
        string? metricId)
    {
        var requestedDemographic = NormalizeOptionalId(demographicId ?? string.Empty);
        var requestedMetric = NormalizeOptionalId(metricId ?? string.Empty);
        if ((requestedDemographic is null) != (requestedMetric is null))
        {
            throw new ArgumentException(
                "demographic and metric must be supplied together.");
        }

        if (requestedDemographic is not null && requestedMetric is not null)
        {
            var explicitSelection = new RatingSelection(requestedDemographic, requestedMetric);
            _ = report.EffectiveCapabilities.Resolve(explicitSelection);
            return explicitSelection;
        }

        if (snapshot.PreferredDemographicId is null || snapshot.PreferredMetricId is null)
        {
            return null;
        }

        var preferred = new RatingSelection(
            snapshot.PreferredDemographicId,
            snapshot.PreferredMetricId);
        try
        {
            return report.EffectiveCapabilities.Resolve(preferred);
        }
        catch (ArgumentException)
        {
            // A local preference is advisory because capabilities can differ by
            // report date. Explicit API selections above never fall back.
            return null;
        }
    }

    private static async Task<RatingCapabilities> GetLatestCapabilitiesAsync(
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var availableDates = snapshot.Catalog.AvailableDates;
        if (availableDates.Count == 0)
        {
            return RatingCapabilities.Empty;
        }

        var latestDate = availableDates.Max();

        var lookup = await snapshot.Catalog
            .GetAsync(latestDate, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = lookup.Report?.EffectiveCapabilities ?? RatingCapabilities.Empty;
        return ApplyPreferredSelection(capabilities, snapshot);
    }

    private static RatingCapabilities ApplyPreferredSelection(
        RatingCapabilities capabilities,
        CatalogSnapshot snapshot)
    {
        if (snapshot.PreferredDemographicId is null || snapshot.PreferredMetricId is null)
        {
            return capabilities;
        }

        try
        {
            var preferred = capabilities.Resolve(new RatingSelection(
                snapshot.PreferredDemographicId,
                snapshot.PreferredMetricId));
            return capabilities with
            {
                DefaultDemographicId = preferred.DemographicId,
                DefaultMetricId = preferred.MetricId,
            };
        }
        catch (ArgumentException)
        {
            return capabilities;
        }
    }

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
        IReadOnlyList<ShowGroupSettings> ShowGroups,
        string? PreferredDemographicId,
        string? PreferredMetricId);
}
