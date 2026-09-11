using System.Text.Json;

namespace NltvViewer.App.Configuration;

public interface ISettingsTransferService
{
    Task ExportAsync(
        string path,
        LocalApplicationSettings settings,
        CancellationToken cancellationToken = default);

    Task<LocalApplicationSettings> ImportAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public sealed class SettingsTransferService : ISettingsTransferService
{
    public async Task ExportAsync(
        string path,
        LocalApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = LocalApplicationSettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Settings cannot be exported:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }

        if (IsWithinDirectory(path, settings.ReportFolder))
        {
            throw new InvalidOperationException(
                "Settings cannot be exported into the report folder. Choose another local location.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The export path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<LocalApplicationSettings> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var settings = await SettingsJsonSerializer
            .DeserializeFileAsync(Path.GetFullPath(path), cancellationToken)
            .ConfigureAwait(false);
        var errors = LocalApplicationSettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Imported settings are not valid:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }

        return settings;
    }

    private static bool IsWithinDirectory(string candidatePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(candidatePath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
