using System.Text.Json;

namespace NltvViewer.App.Hosting;

internal static class LocalLog
{
    private static readonly Lock SyncRoot = new();
    private static DateOnly _lastCleanupDate;

    public static void Write(
        string level,
        string eventName,
        string message,
        Exception? exception = null)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = now,
                level,
                eventName,
                message,
                exceptionType = exception?.GetType().FullName,
            });

            lock (SyncRoot)
            {
                var logDirectory = GetLogDirectory();
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, $"nltv-viewer-{now:yyyyMMdd}.log"),
                    $"{entry}{Environment.NewLine}");
                CleanupOldLogs(logDirectory, DateOnly.FromDateTime(now.LocalDateTime));
            }
        }
        catch (Exception logException) when (logException is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // Logging must never stop the report viewer.
        }
    }

    private static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NLTV Viewer",
            "logs");
    }

    private static void CleanupOldLogs(string logDirectory, DateOnly currentDate)
    {
        if (_lastCleanupDate == currentDate)
        {
            return;
        }

        _lastCleanupDate = currentDate;
        var cutoff = DateTime.Now.AddDays(-14);
        foreach (var path in Directory.EnumerateFiles(logDirectory, "nltv-viewer-*.log"))
        {
            if (File.GetLastWriteTime(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }
}
