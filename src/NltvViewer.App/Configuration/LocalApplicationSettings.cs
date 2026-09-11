using System.Net;
using System.Text.Json;
using NltvViewer.Core.Configuration;

namespace NltvViewer.App.Configuration;

public sealed class LocalApplicationSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ReportFolder { get; set; } = string.Empty;

    public int Port { get; set; } = 5085;

    public string BindAddress { get; set; } = IPAddress.Loopback.ToString();

    public string PathBase { get; set; } = string.Empty;

    public int PollingIntervalMinutes { get; set; } = 15;

    public bool StartAtSignIn { get; set; }

    public List<ShowGroupConfiguration> ShowGroups { get; set; } = [];

    public string NormalizedPathBase()
    {
        var value = PathBase.Trim();
        if (string.IsNullOrEmpty(value) || value == "/")
        {
            return string.Empty;
        }

        return $"/{value.Trim('/')}";
    }

    public ViewerSettings ToViewerSettings()
    {
        return new ViewerSettings
        {
            ReportRoot = ReportFolder.Trim(),
            PollIntervalSeconds = checked(PollingIntervalMinutes * 60),
            ShowGroups = ShowGroups.Select(group => group.ToViewerSettings()).ToList(),
        };
    }
}

public sealed class ShowGroupConfiguration
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string? SourcePattern { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public ShowGroupSettings ToViewerSettings()
    {
        return new ShowGroupSettings
        {
            Id = Id.Trim(),
            DisplayName = DisplayName.Trim(),
            Aliases = Aliases.Select(alias => alias.Trim()).Where(alias => alias.Length > 0).ToList(),
            SourcePattern = string.IsNullOrWhiteSpace(SourcePattern) ? null : SourcePattern.Trim(),
            StartTime = StartTime,
            EndTime = EndTime,
        };
    }
}

public static class LocalApplicationSettingsValidator
{
    public static IReadOnlyList<string> Validate(LocalApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        if (settings.SchemaVersion != LocalApplicationSettings.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported settings schema version {settings.SchemaVersion}.");
        }

        if (settings.Port is < 1024 or > 65535)
        {
            errors.Add("Port must be between 1024 and 65535.");
        }

        if (!IsValidBindAddress(settings.BindAddress))
        {
            errors.Add("Bind address must be localhost or a valid IP address.");
        }

        if (settings.PollingIntervalMinutes is < 1 or > 1440)
        {
            errors.Add("Polling interval must be between 1 and 1440 minutes.");
        }

        ValidatePathBase(settings.PathBase, errors);
        ValidateShowGroups(settings.ShowGroups, errors);
        return errors;
    }

    private static bool IsValidBindAddress(string value)
    {
        return string.Equals(value.Trim(), "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(value.Trim(), out _);
    }

    private static void ValidatePathBase(string value, List<string> errors)
    {
        var normalized = value.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized == "/")
        {
            return;
        }

        if (!normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            errors.Add("Path base must start with '/' and must not end with '/'.");
            return;
        }

        if (normalized.Contains("//", StringComparison.Ordinal)
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Any(char.IsWhiteSpace))
        {
            errors.Add("Path base cannot contain whitespace, empty segments, or '..'.");
        }
    }

    private static void ValidateShowGroups(
        IEnumerable<ShowGroupConfiguration> showGroups,
        List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in showGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Id))
            {
                errors.Add("Every show group must have an ID.");
            }
            else if (!ids.Add(group.Id.Trim()))
            {
                errors.Add($"Show group IDs must be unique: '{group.Id.Trim()}'.");
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                errors.Add($"Show group '{group.Id}' needs a display name.");
            }

            if (group.Aliases.Count == 0 || group.Aliases.All(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Show group '{group.Id}' needs at least one show-name search filter.");
            }

            if (group.StartTime.HasValue != group.EndTime.HasValue)
            {
                errors.Add($"Show group '{group.Id}' must set both start and end time, or neither.");
            }
        }
    }
}

public interface ILocalSettingsStore
{
    string SettingsPath { get; }

    string? LoadWarning { get; }

    Task<LocalApplicationSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LocalApplicationSettings settings, CancellationToken cancellationToken = default);
}

public sealed class LocalSettingsStore : ILocalSettingsStore
{
    private bool _loadedFromBackup;

    public LocalSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NLTV Viewer",
            "settings.json"))
    {
    }

    internal LocalSettingsStore(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public string? LoadWarning { get; private set; }

    public async Task<LocalApplicationSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        LoadWarning = null;
        _loadedFromBackup = false;
        var backupPath = $"{SettingsPath}.bak";
        if (!File.Exists(SettingsPath))
        {
            if (!File.Exists(backupPath))
            {
                return new LocalApplicationSettings();
            }

            var recovered = await SettingsJsonSerializer
                .DeserializeFileAsync(backupPath, cancellationToken)
                .ConfigureAwait(false);
            _loadedFromBackup = true;
            LoadWarning = $"The primary settings file is missing. Recovered settings from {backupPath}.";
            return recovered;
        }

        try
        {
            return await SettingsJsonSerializer
                .DeserializeFileAsync(SettingsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException primaryException) when (File.Exists(backupPath))
        {
            try
            {
                var recovered = await SettingsJsonSerializer
                    .DeserializeFileAsync(backupPath, cancellationToken)
                    .ConfigureAwait(false);
                _loadedFromBackup = true;
                LoadWarning = $"The primary settings file is invalid. Recovered settings from {backupPath}.";
                return recovered;
            }
            catch (InvalidDataException backupException)
            {
                throw new InvalidDataException(
                    $"Both the primary and backup settings files are invalid: {SettingsPath}",
                    new AggregateException(primaryException, backupException));
            }
        }
    }

    public async Task SaveAsync(
        LocalApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        var backupPath = _loadedFromBackup
            ? $"{SettingsPath}.corrupt"
            : $"{SettingsPath}.bak";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        SettingsJsonSerializer.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }

            _loadedFromBackup = false;
            LoadWarning = null;
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

internal static class SettingsJsonSerializer
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
    };

    internal static async Task<LocalApplicationSettings> DeserializeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<LocalApplicationSettings>(
                    stream,
                    Options,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"The settings file is empty: {path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The settings file is invalid: {path}", exception);
        }
    }
}
