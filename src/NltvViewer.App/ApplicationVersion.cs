using System.Reflection;

namespace NltvViewer.App;

public static class ApplicationVersion
{
    public static string Informational { get; } =
        typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(ApplicationVersion).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";
}
