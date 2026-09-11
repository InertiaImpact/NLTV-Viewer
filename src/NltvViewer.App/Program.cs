using NltvViewer.App.Configuration;
using NltvViewer.App.Forms;
using NltvViewer.App.Hosting;

namespace NltvViewer.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = SingleInstanceCoordinator.TryAcquire();
        if (singleInstance is null)
        {
            SingleInstanceCoordinator.RequestActivation();
            return;
        }

        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(defaultValue: false);

        var settingsStore = new LocalSettingsStore();
        LocalApplicationSettings settings;
        string? settingsError = null;
        try
        {
            settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
            settingsError = settingsStore.LoadWarning;
            if (settingsError is not null)
            {
                LocalLog.Write("Warning", "settings_recovered", settingsError);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            settings = new LocalApplicationSettings();
            settingsError = exception.Message;
            LocalLog.Write("Error", "settings_load_failed", exception.Message, exception);
        }

        var startAtSignIn = new StartAtSignInManager();
        try
        {
            settings.StartAtSignIn = startAtSignIn.IsEnabled();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            settingsError ??= "Windows startup settings could not be read.";
            LocalLog.Write("Warning", "autostart_read_failed", exception.Message, exception);
        }

        var folderAccessProbe = new FolderAccessProbe();
        var settingsTransfer = new SettingsTransferService();
        var dataGateway = ViewerDataGatewayFactory.Create(settings);
        var webHost = new EmbeddedWebHost(dataGateway);
        var mainForm = new MainForm(
            settings,
            settingsError,
            settingsStore,
            settingsTransfer,
            startAtSignIn,
            folderAccessProbe,
            webHost);
        using var applicationContext = new ViewerApplicationContext(
            mainForm,
            webHost,
            args.Any(argument => string.Equals(
                argument,
                "--background",
                StringComparison.OrdinalIgnoreCase)));
        singleInstance.ActivationRequested += (_, _) => applicationContext.ShowSettings();
        try
        {
            System.Windows.Forms.Application.Run(applicationContext);
        }
        finally
        {
            webHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
