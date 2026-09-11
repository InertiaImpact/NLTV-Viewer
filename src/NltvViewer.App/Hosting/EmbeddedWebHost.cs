using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using NltvViewer.App.Configuration;

namespace NltvViewer.App.Hosting;

public sealed record ViewerServerStatus(
    bool IsRunning,
    bool IsRefreshing,
    string ViewerUrl,
    string Message,
    DateTimeOffset? LastRefreshAttempt,
    DateTimeOffset? LastSuccessfulRefresh,
    bool SourceAccessible,
    IReadOnlyList<string> Warnings);

public interface IEmbeddedWebHost : IAsyncDisposable
{
    ViewerServerStatus Status { get; }

    event EventHandler<ViewerServerStatus>? StatusChanged;

    Task StartAsync(LocalApplicationSettings settings, CancellationToken cancellationToken = default);

    Task RestartAsync(LocalApplicationSettings settings, CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class EmbeddedWebHost : IEmbeddedWebHost
{
    private readonly IViewerDataGateway _dataGateway;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _statusSync = new();
    private WebApplication? _application;
    private LocalApplicationSettings? _settings;
    private CancellationTokenSource? _pollingCancellation;
    private Task? _pollingTask;
    private ViewerServerStatus _status = new(
        false,
        false,
        string.Empty,
        "Server is stopped.",
        null,
        null,
        false,
        []);

    public EmbeddedWebHost(IViewerDataGateway dataGateway)
    {
        _dataGateway = dataGateway;
    }

    public ViewerServerStatus Status
    {
        get
        {
            lock (_statusSync)
            {
                return _status;
            }
        }
    }

    public event EventHandler<ViewerServerStatus>? StatusChanged;

    public async Task StartAsync(
        LocalApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                throw new InvalidOperationException("The web server is already running.");
            }

            var errors = LocalApplicationSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }

            var application = BuildApplication(settings);
            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _application = application;
            _settings = settings;
            _pollingCancellation = new CancellationTokenSource();
            _pollingTask = Task.Run(
                () => PollAsync(_pollingCancellation.Token),
                _pollingCancellation.Token);
            UpdateStatus(Status with
            {
                IsRunning = true,
                ViewerUrl = BuildViewerUrl(settings),
                Message = "Server is running.",
            });
            LocalLog.Write("Information", "server_started", BuildViewerUrl(settings));
        }
        catch (Exception exception)
        {
            LocalLog.Write("Error", "server_start_failed", exception.Message, exception);
            UpdateStatus(Status with
            {
                IsRunning = false,
                Message = $"Server could not start: {exception.Message}",
            });
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(
        LocalApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings;
        if (settings is null)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempt = DateTimeOffset.Now;
            UpdateStatus(Status with
            {
                IsRefreshing = true,
                LastRefreshAttempt = attempt,
                Message = "Checking the report folder...",
            });

            await _dataGateway.RefreshAsync(settings, cancellationToken).ConfigureAwait(false);
            var dataStatus = _dataGateway.Status;
            UpdateStatus(Status with
            {
                IsRefreshing = false,
                LastRefreshAttempt = attempt,
                LastSuccessfulRefresh = dataStatus.SourceAvailable
                    ? dataStatus.LastRefreshUtc ?? DateTimeOffset.Now
                    : Status.LastSuccessfulRefresh,
                SourceAccessible = dataStatus.SourceAvailable,
                Message = dataStatus.Message,
                Warnings = dataStatus.Warnings,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateStatus(Status with { IsRefreshing = false });
        }
        catch (Exception exception)
        {
            LocalLog.Write("Error", "report_refresh_failed", exception.Message, exception);
            var reportFolder = settings.ReportFolder;
            UpdateStatus(Status with
            {
                IsRefreshing = false,
                SourceAccessible = false,
                Message = $"Cannot access {reportFolder}. Check desktop app/server for fileshare permissions.",
            });
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is null)
            {
                return;
            }

            if (_pollingCancellation is not null)
            {
                await _pollingCancellation.CancelAsync().ConfigureAwait(false);
            }

            if (_pollingTask is not null)
            {
                try
                {
                    await _pollingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
            }

            await _application.StopAsync(cancellationToken).ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
            _settings = null;
            _pollingCancellation?.Dispose();
            _pollingCancellation = null;
            _pollingTask = null;
            UpdateStatus(Status with
            {
                IsRunning = false,
                IsRefreshing = false,
                Message = "Server is stopped.",
            });
            LocalLog.Write("Information", "server_stopped", "Server stopped normally.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _dataGateway.Dispose();
        _lifecycleGate.Dispose();
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private WebApplication BuildApplication(LocalApplicationSettings settings)
    {
        var options = new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            if (string.Equals(settings.BindAddress.Trim(), "localhost", StringComparison.OrdinalIgnoreCase))
            {
                serverOptions.ListenLocalhost(settings.Port);
            }
            else
            {
                serverOptions.Listen(IPAddress.Parse(settings.BindAddress.Trim()), settings.Port);
            }
        });
        builder.Services.Configure<ForwardedHeadersOptions>(forwardedOptions =>
        {
            forwardedOptions.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedProto;
            forwardedOptions.ForwardLimit = 1;
        });

        var application = builder.Build();
        application.UseForwardedHeaders();
        var pathBase = settings.NormalizedPathBase();
        if (!string.IsNullOrEmpty(pathBase))
        {
            application.UsePathBase(pathBase);
        }

        // Endpoint selection must happen after UsePathBase has removed the
        // externally visible prefix. Without the explicit ordering, minimal
        // hosting can route /prefix/api/... as an SPA fallback instead.
        application.UseRouting();

        application.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; "
                + "img-src 'self' data:; connect-src 'self'; base-uri 'self'; frame-ancestors 'none'";
            context.Response.Headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });
        application.UseDefaultFiles();
        application.UseStaticFiles();

        application.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        application.MapGet("/health/ready", () =>
            Status.IsRunning
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        application.MapGet("/api/v1/status", () =>
        {
            var status = Status;
            var dataStatus = _dataGateway.Status;
            return Results.Ok(new
            {
                lastRefreshUtc = dataStatus.LastRefreshUtc,
                isStale = dataStatus.IsStale,
                sourceAvailable = dataStatus.SourceAvailable,
                sourceDisplayName = dataStatus.SourceDisplayName,
                errorCode = dataStatus.ErrorCode,
                message = status.Message,
                hasUsableReports = dataStatus.HasUsableReports,
                running = status.IsRunning,
                refreshing = status.IsRefreshing,
                viewerUrl = status.ViewerUrl,
                lastRefreshAttempt = status.LastRefreshAttempt,
                lastSuccessfulRefresh = status.LastSuccessfulRefresh,
                sourceAccessible = status.SourceAccessible,
                stale = dataStatus.IsStale,
                warnings = dataStatus.Warnings,
            });
        });
        application.MapGet("/api/v1/days", async (CancellationToken cancellationToken) =>
        {
            var dataStatus = _dataGateway.Status;
            if (!dataStatus.HasUsableReports
                && ViewerDataGateway.IsReportFormatError(dataStatus.ErrorCode))
            {
                return Results.UnprocessableEntity(new
                {
                    errorCode = dataStatus.ErrorCode,
                    message = dataStatus.Message,
                    sourceAvailable = dataStatus.SourceAvailable,
                    sourceDisplayName = dataStatus.SourceDisplayName,
                    warnings = dataStatus.Warnings,
                });
            }

            return Results.Ok(
                await _dataGateway.GetDaysAsync(cancellationToken).ConfigureAwait(false));
        });
        application.MapGet("/api/v1/ratings", async (string? date, CancellationToken cancellationToken) =>
        {
            if (!DateOnly.TryParseExact(
                date,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
            {
                return Results.BadRequest(new { error = "date must use YYYY-MM-DD format" });
            }

            var dataStatus = _dataGateway.Status;
            if (!dataStatus.HasUsableReports
                && ViewerDataGateway.IsReportFormatError(dataStatus.ErrorCode))
            {
                return Results.UnprocessableEntity(new
                {
                    errorCode = dataStatus.ErrorCode,
                    message = dataStatus.Message,
                    sourceAvailable = dataStatus.SourceAvailable,
                    sourceDisplayName = dataStatus.SourceDisplayName,
                    warnings = dataStatus.Warnings,
                });
            }

            var result = await _dataGateway
                .GetRatingsAsync(parsedDate, cancellationToken)
                .ConfigureAwait(false);
            return result is null
                ? Results.NotFound(new { error = $"No ratings are available for {date}." })
                : Results.Ok(result);
        });
        application.MapFallbackToFile("index.html");
        return application;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_settings?.PollingIntervalMinutes ?? 15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildViewerUrl(LocalApplicationSettings settings)
    {
        var pathBase = settings.NormalizedPathBase();
        var configuredAddress = settings.BindAddress.Trim();
        var host = "localhost";
        if (IPAddress.TryParse(configuredAddress, out var address)
            && !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.Any)
            && !address.Equals(IPAddress.IPv6Any))
        {
            host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{address}]"
                : address.ToString();
        }

        return $"http://{host}:{settings.Port}{pathBase}/";
    }

    private void UpdateStatus(ViewerServerStatus status)
    {
        lock (_statusSync)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }
}
