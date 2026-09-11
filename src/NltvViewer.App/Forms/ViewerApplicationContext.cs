using System.Drawing;
using NltvViewer.App.Hosting;

namespace NltvViewer.App.Forms;

public sealed class ViewerApplicationContext : ApplicationContext
{
    private readonly MainForm _mainForm;
    private readonly IEmbeddedWebHost _webHost;
    private readonly NotifyIcon _notifyIcon;
    private bool _exiting;

    public ViewerApplicationContext(
        MainForm mainForm,
        IEmbeddedWebHost webHost,
        bool startInBackground)
    {
        _mainForm = mainForm;
        _webHost = webHost;
        MainForm = mainForm;
        // Create the native handle without showing the settings window. A
        // hidden top-level Form can otherwise defer handle creation, which
        // prevents the WinForms startup timer from receiving its first tick.
        _ = _mainForm.Handle;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Open viewer", null, (_, _) => _mainForm.OpenViewer());
        menu.Items.Add("Refresh reports", null, async (_, _) =>
            await _mainForm.RefreshReportsAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExitClick);

        _notifyIcon = new NotifyIcon
        {
            Text = "NLTV Viewer",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        if (!startInBackground)
        {
            ShowSettings();
        }

        // Start immediately instead of depending on a hidden form's Idle or
        // Timer messages. The method owns its exception handling, so this
        // deliberately remains fire-and-forget while the UI loop begins.
        _ = _mainForm.InitializeServerAsync();
    }

    public void ShowSettings()
    {
        if (_mainForm.IsDisposed)
        {
            return;
        }

        _mainForm.ShowAndActivate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private async void OnExitClick(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _notifyIcon.Visible = false;
        try
        {
            await _webHost.StopAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LocalLog.Write("Error", "server_stop_failed", exception.Message, exception);
        }
        finally
        {
            _mainForm.AllowClose();
            _mainForm.Close();
            ExitThread();
        }
    }
}
