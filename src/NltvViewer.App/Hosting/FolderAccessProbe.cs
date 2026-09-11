namespace NltvViewer.App.Hosting;

public sealed record FolderAccessResult(bool IsAccessible, string Message)
{
    public static FolderAccessResult Success(string folder) =>
        new(true, $"Folder is accessible: {folder}");

    public static FolderAccessResult Failure(string folder) =>
        new(false, $"Cannot access {folder}. Check desktop app/server for fileshare permissions.");
}

public interface IFolderAccessProbe
{
    Task<FolderAccessResult> ProbeAsync(string folder, CancellationToken cancellationToken = default);
}

public sealed class FolderAccessProbe : IFolderAccessProbe
{
    public async Task<FolderAccessResult> ProbeAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return FolderAccessResult.Failure("the configured report location");
        }

        try
        {
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var enumerator = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                    _ = enumerator.MoveNext();
                },
                cancellationToken).ConfigureAwait(false);
            return FolderAccessResult.Success(folder);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or DirectoryNotFoundException
            or DriveNotFoundException
            or IOException)
        {
            LocalLog.Write("Warning", "folder_access_failed", exception.Message, exception);
            return FolderAccessResult.Failure(folder);
        }
    }
}
