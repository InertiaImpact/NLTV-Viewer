using Microsoft.Win32;

namespace NltvViewer.App.Configuration;

public interface IStartAtSignInManager
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public sealed class StartAtSignInManager : IStartAtSignInManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NLTV Viewer";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command
            && string.Equals(command, BuildCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not provide access to the sign-in startup settings.");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommand()
    {
        return $"\"{System.Windows.Forms.Application.ExecutablePath}\" --background";
    }
}
