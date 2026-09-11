using NltvViewer.Core.Reports;
using System.Text.Json;

namespace NltvViewer.Infrastructure.Reports;

/// <summary>
/// Builds a report index without modifying the configured source tree. Source workbooks are
/// enumerated and opened read-only; the optional persistent cache must resolve outside that tree.
/// </summary>
public sealed class NetworkReportCatalog : IReportCatalog, IDisposable
{
    private const int MaximumWarnings = 100;
    private const int PersistentCacheSchemaVersion = 2;
    private const int MaximumDetailedReports = 16;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly string _reportRoot;
    private readonly IReportParser _parser;
    private readonly string? _localCachePath;
    private readonly string _parserCacheVersion;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _detailLoadLock = new(1, 1);
    private readonly object _detailSync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DateOnly, DetailCacheEntry> _detailCache = [];
    private IReadOnlyDictionary<DateOnly, RatingReport> _reports = new Dictionary<DateOnly, RatingReport>();
    private IReadOnlyList<DateOnly> _availableDates = Array.Empty<DateOnly>();
    private CatalogRefreshResult _lastRefresh = CatalogRefreshResult.NotRefreshed;
    private string? _persistentCacheWarning;
    private bool _disposed;

    public NetworkReportCatalog(
        string reportRoot,
        IReportParser parser,
        string? localCachePath = null)
    {
        _reportRoot = reportRoot?.Trim() ?? string.Empty;
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _localCachePath = string.IsNullOrWhiteSpace(localCachePath) ? null : localCachePath;
        if (_localCachePath is not null && IsPathWithinRoot(_localCachePath, _reportRoot))
        {
            throw new ArgumentException(
                "The local cache path must be outside the read-only report source tree.",
                nameof(localCachePath));
        }

        _parserCacheVersion = parser is IVersionedReportParser versioned
            ? versioned.CacheVersion
            : parser.GetType().AssemblyQualifiedName ?? parser.GetType().FullName ?? "unknown-parser";
        LoadPersistentCache();
    }

    public IReadOnlyList<DateOnly> AvailableDates => Volatile.Read(ref _availableDates);

    public CatalogRefreshResult LastRefresh => Volatile.Read(ref _lastRefresh);

    public async ValueTask<CatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask<ReportLookupResult> GetAsync(
        DateOnly reportDate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var reports = Volatile.Read(ref _reports);
        if (!reports.TryGetValue(reportDate, out var report))
        {
            return new ReportLookupResult(
                reportDate,
                null,
                "report_date_not_found",
                $"No supported 15-minute report is available for {reportDate:yyyy-MM-dd}.");
        }

        if (report.IsDetailComplete)
        {
            return new ReportLookupResult(reportDate, report);
        }

        if (TryGetDetailedReport(report, out var detailed))
        {
            return new ReportLookupResult(reportDate, detailed);
        }

        await _detailLoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetDetailedReport(report, out detailed))
            {
                return new ReportLookupResult(reportDate, detailed);
            }

            var parsed = await _parser.ParseAsync(report.SourcePath, cancellationToken).ConfigureAwait(false);
            var loaded = parsed.Reports.FirstOrDefault(candidate =>
                candidate.ReportDate == reportDate &&
                string.Equals(
                    candidate.WorksheetName,
                    report.WorksheetName,
                    StringComparison.OrdinalIgnoreCase));
            if (loaded is not null)
            {
                StoreDetailedReport(loaded);
                return new ReportLookupResult(reportDate, loaded);
            }

            return new ReportLookupResult(
                reportDate,
                report,
                "report_detail_unavailable",
                $"Detailed ratings for {reportDate:yyyy-MM-dd} could not be loaded; cached P18+ impressions may be stale. {parsed.Message}");
        }
        finally
        {
            _detailLoadLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _refreshLock.Dispose();
        _detailLoadLock.Dispose();
        _disposed = true;
    }

    private async ValueTask<CatalogRefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_reportRoot))
        {
            return SetRefresh(new CatalogRefreshResult(
                CatalogStatus.LocationNotFound,
                0,
                Volatile.Read(ref _reports).Count,
                completedAt,
                "report_location_not_configured",
                "The report location has not been configured."));
        }

        string[] files;
        try
        {
            _ = File.GetAttributes(_reportRoot);
            files = Directory.EnumerateFiles(
                    _reportRoot,
                    "*.xlsx",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        MatchCasing = MatchCasing.CaseInsensitive,
                        ReturnSpecialDirectories = false
                    })
                .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
                .ToArray();
        }
        catch (FileNotFoundException)
        {
            return SetLocationFailure(CatalogStatus.LocationNotFound, "report_location_not_found", completedAt);
        }
        catch (DirectoryNotFoundException)
        {
            return SetLocationFailure(CatalogStatus.LocationNotFound, "report_location_not_found", completedAt);
        }
        catch (DriveNotFoundException)
        {
            return SetLocationFailure(CatalogStatus.LocationNotFound, "report_drive_not_found", completedAt);
        }
        catch (UnauthorizedAccessException)
        {
            return SetLocationFailure(CatalogStatus.LocationInaccessible, "report_location_access_denied", completedAt);
        }
        catch (IOException)
        {
            return SetLocationFailure(CatalogStatus.LocationInaccessible, "report_location_io_error", completedAt);
        }

        var indexed = new Dictionary<DateOnly, RatingReport>();
        var warnings = new List<string>();
        if (_persistentCacheWarning is not null)
        {
            AddWarning(warnings, _persistentCacheWarning);
            _persistentCacheWarning = null;
        }

        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
        {
            return SetRefresh(new CatalogRefreshResult(
                CatalogStatus.NoReportFiles,
                0,
                Volatile.Read(ref _reports).Count,
                DateTimeOffset.UtcNow,
                "no_report_files",
                $"No XLSX report files were found under '{_reportRoot}'.",
                warnings));
        }

        var unsupportedFiles = 0;
        var failedFiles = 0;
        var staleReports = 0;

        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            activePaths.Add(path);

            FileStamp stamp;
            try
            {
                var info = new FileInfo(path);
                stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedFiles++;
                AddWarning(warnings, $"Could not inspect '{path}': {exception.Message}");
                continue;
            }

            ReportParseResult parsed;
            _cache.TryGetValue(path, out var cached);
            if (cached is not null && cached.Stamp == stamp)
            {
                parsed = cached.Result;
            }
            else
            {
                parsed = await _parser.ParseAsync(path, cancellationToken).ConfigureAwait(false);
                if (parsed.IsSuccess)
                {
                    foreach (var detailedReport in parsed.Reports)
                    {
                        StoreDetailedReport(detailedReport);
                    }

                    parsed = CreateCatalogCacheResult(parsed);
                }

                if (parsed.Status == ReportParseStatus.Success ||
                    parsed.Status == ReportParseStatus.UnsupportedFormat && cached?.Result.IsSuccess != true)
                {
                    _cache[path] = new CacheEntry(stamp, parsed);
                }
            }

            if (!parsed.IsSuccess)
            {
                if (parsed.Status == ReportParseStatus.UnsupportedFormat)
                {
                    unsupportedFiles++;
                }
                else
                {
                    failedFiles++;
                }

                AddWarning(warnings, $"Skipped '{path}': {parsed.Message}");
                if (cached?.Result.IsSuccess == true)
                {
                    staleReports += cached.Result.Reports.Count;
                    AddWarning(
                        warnings,
                        $"Using stale cached data for '{path}' because its changed source could not be parsed.");
                    IndexReports(indexed, cached.Result.Reports, warnings);
                }

                continue;
            }

            foreach (var warning in parsed.Warnings ?? [])
            {
                AddWarning(warnings, $"'{path}': {warning}");
            }

            IndexReports(indexed, parsed.Reports, warnings);
        }

        if (indexed.Count == 0)
        {
            var priorCount = Volatile.Read(ref _reports).Count;
            var onlyUnsupported = unsupportedFiles > 0 && failedFiles == 0;
            return SetRefresh(new CatalogRefreshResult(
                onlyUnsupported ? CatalogStatus.NoSupportedReports : CatalogStatus.Error,
                files.Length,
                priorCount,
                DateTimeOffset.UtcNow,
                onlyUnsupported ? "no_supported_reports" : "no_reports_parsed",
                onlyUnsupported
                    ? $"Found {files.Length} XLSX file(s), but none use the supported Nielsen Time Period Standard 15-minute format."
                    : $"Found {files.Length} XLSX file(s), but none could be parsed successfully.",
                warnings));
        }

        foreach (var stalePath in _cache.Keys.Where(path => !activePaths.Contains(path)).ToArray())
        {
            _cache.Remove(stalePath);
        }

        try
        {
            await SavePersistentCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AddWarning(warnings, $"The local report cache could not be saved: {exception.Message}");
        }

        IReadOnlyDictionary<DateOnly, RatingReport> reportSnapshot = indexed;
        IReadOnlyList<DateOnly> dateSnapshot = indexed.Keys.Order().ToArray();
        Volatile.Write(ref _reports, reportSnapshot);
        Volatile.Write(ref _availableDates, dateSnapshot);

        var result = new CatalogRefreshResult(
            CatalogStatus.Available,
            files.Length,
            indexed.Count,
            DateTimeOffset.UtcNow,
            Warnings: warnings,
            StaleReports: staleReports);
        return SetRefresh(result);
    }

    private CatalogRefreshResult SetLocationFailure(
        CatalogStatus status,
        string errorCode,
        DateTimeOffset completedAt) =>
        SetRefresh(new CatalogRefreshResult(
            status,
            0,
            Volatile.Read(ref _reports).Count,
            completedAt,
            errorCode,
            $"Cannot access '{_reportRoot}'. Check the desktop app/server account's file-share permissions."));

    private CatalogRefreshResult SetRefresh(CatalogRefreshResult result)
    {
        Volatile.Write(ref _lastRefresh, result);
        return result;
    }

    private static bool IsPreferred(RatingReport candidate, RatingReport existing)
    {
        var modifiedComparison = candidate.SourceLastModifiedUtc.CompareTo(existing.SourceLastModifiedUtc);
        if (modifiedComparison != 0)
        {
            return modifiedComparison > 0;
        }

        var pathComparison = StringComparer.OrdinalIgnoreCase.Compare(candidate.SourcePath, existing.SourcePath);
        return pathComparison > 0 ||
               pathComparison == 0 && string.Compare(
                   candidate.WorksheetName,
                   existing.WorksheetName,
                   StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static void IndexReports(
        Dictionary<DateOnly, RatingReport> indexed,
        IReadOnlyList<RatingReport> reports,
        List<string> warnings)
    {
        foreach (var report in reports)
        {
            if (!indexed.TryGetValue(report.ReportDate, out var existing) || IsPreferred(report, existing))
            {
                if (existing is not null)
                {
                    AddWarning(
                        warnings,
                        $"Multiple reports cover {report.ReportDate:yyyy-MM-dd}; using the most recently modified source.");
                }

                indexed[report.ReportDate] = report;
            }
            else
            {
                AddWarning(
                    warnings,
                    $"Multiple reports cover {report.ReportDate:yyyy-MM-dd}; ignored an older source.");
            }
        }
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(candidatePath);
        var root = Path.GetFullPath(rootPath);
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(warning);
        }
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);

    private sealed record CacheEntry(FileStamp Stamp, ReportParseResult Result);

    private sealed record DetailCacheEntry(RatingReport Report, long LastAccess);

    private static ReportParseResult CreateCatalogCacheResult(ReportParseResult result) =>
        ReportParseResult.Success(
            result.Reports.Select(report => report with
            {
                Segments = report.Segments.Select(segment => segment with { Values = null }).ToArray(),
                IsDetailComplete = false
            }).ToArray(),
            result.Warnings);

    private bool TryGetDetailedReport(RatingReport locator, out RatingReport report)
    {
        lock (_detailSync)
        {
            if (_detailCache.TryGetValue(locator.ReportDate, out var cached) &&
                string.Equals(cached.Report.SourcePath, locator.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    cached.Report.WorksheetName,
                    locator.WorksheetName,
                    StringComparison.OrdinalIgnoreCase) &&
                cached.Report.SourceLastModifiedUtc == locator.SourceLastModifiedUtc)
            {
                report = cached.Report;
                _detailCache[locator.ReportDate] = cached with { LastAccess = DateTime.UtcNow.Ticks };
                return true;
            }
        }

        report = null!;
        return false;
    }

    private void StoreDetailedReport(RatingReport report)
    {
        lock (_detailSync)
        {
            _detailCache[report.ReportDate] = new DetailCacheEntry(report, DateTime.UtcNow.Ticks);
            while (_detailCache.Count > MaximumDetailedReports)
            {
                var oldest = _detailCache.MinBy(pair => pair.Value.LastAccess).Key;
                _detailCache.Remove(oldest);
            }
        }
    }

    private void LoadPersistentCache()
    {
        if (_localCachePath is null || !File.Exists(_localCachePath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(
                _localCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<PersistentCacheDocument>(stream, CacheJsonOptions);
            if (document is null ||
                document.SchemaVersion != PersistentCacheSchemaVersion ||
                !string.Equals(document.ParserVersion, _parserCacheVersion, StringComparison.Ordinal))
            {
                _persistentCacheWarning = "The local report cache used an incompatible format and was ignored.";
                return;
            }

            foreach (var entry in document.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) ||
                    entry.Result.Status is not (ReportParseStatus.Success or ReportParseStatus.UnsupportedFormat))
                {
                    continue;
                }

                _cache[entry.Path] = new CacheEntry(
                    new FileStamp(entry.Length, entry.LastWriteTimeUtc),
                    entry.Result);
            }

            RestoreSnapshotFromCache();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _cache.Clear();
            _persistentCacheWarning = $"The local report cache could not be loaded and was ignored: {exception.Message}";
        }
    }

    private void RestoreSnapshotFromCache()
    {
        var indexed = new Dictionary<DateOnly, RatingReport>();
        foreach (var report in _cache.Values
                     .Where(entry => entry.Result.IsSuccess)
                     .SelectMany(entry => entry.Result.Reports))
        {
            if (!indexed.TryGetValue(report.ReportDate, out var existing) || IsPreferred(report, existing))
            {
                indexed[report.ReportDate] = report;
            }
        }

        Volatile.Write(ref _reports, indexed);
        Volatile.Write(ref _availableDates, indexed.Keys.Order().ToArray());
    }

    private async ValueTask SavePersistentCacheAsync(CancellationToken cancellationToken)
    {
        if (_localCachePath is null)
        {
            return;
        }

        var cacheDirectory = Path.GetDirectoryName(Path.GetFullPath(_localCachePath));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new IOException("The local cache path does not have a valid parent directory.");
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporaryPath = Path.Combine(
            cacheDirectory,
            $".{Path.GetFileName(_localCachePath)}.{Guid.NewGuid():N}.tmp");
        var document = new PersistentCacheDocument(
            PersistentCacheSchemaVersion,
            _parserCacheVersion,
            _cache.Select(pair => new PersistentCacheEntry(
                    pair.Key,
                    pair.Value.Stamp.Length,
                    pair.Value.Stamp.LastWriteTimeUtc,
                    pair.Value.Result))
                .ToArray());

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    CacheJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _localCachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record PersistentCacheDocument(
    int SchemaVersion,
    string ParserVersion,
    IReadOnlyList<PersistentCacheEntry> Entries);

internal sealed record PersistentCacheEntry(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc,
    ReportParseResult Result);
