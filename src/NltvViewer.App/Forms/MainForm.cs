using System.Diagnostics;
using System.Globalization;
using NltvViewer.App.Configuration;
using NltvViewer.App.Hosting;

namespace NltvViewer.App.Forms;

public sealed class MainForm : Form
{
    private readonly ILocalSettingsStore _settingsStore;
    private readonly ISettingsTransferService _settingsTransferService;
    private readonly IStartAtSignInManager _startAtSignInManager;
    private readonly IFolderAccessProbe _folderAccessProbe;
    private readonly IEmbeddedWebHost _webHost;
    private readonly TextBox _reportFolderText = new();
    private readonly NumericUpDown _portInput = new();
    private readonly TextBox _bindAddressText = new();
    private readonly TextBox _pathBaseText = new();
    private readonly NumericUpDown _pollingIntervalInput = new();
    private readonly CheckBox _startAtSignInCheck = new();
    private readonly DataGridView _showGroupsGrid = new();
    private readonly Label _serverStateLabel = new();
    private readonly LinkLabel _viewerUrlLink = new();
    private readonly TextBox _statusText = new();
    private readonly Button _saveAndApplyButton = new();
    private readonly Button _refreshButton = new();
    private LocalApplicationSettings _appliedSettings;
    private bool _allowClose;
    private bool _busy;

    public MainForm(
        LocalApplicationSettings settings,
        string? initialError,
        ILocalSettingsStore settingsStore,
        ISettingsTransferService settingsTransferService,
        IStartAtSignInManager startAtSignInManager,
        IFolderAccessProbe folderAccessProbe,
        IEmbeddedWebHost webHost)
    {
        _appliedSettings = settings;
        _settingsStore = settingsStore;
        _settingsTransferService = settingsTransferService;
        _startAtSignInManager = startAtSignInManager;
        _folderAccessProbe = folderAccessProbe;
        _webHost = webHost;

        Text = "NLTV Viewer Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1080, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormClosing += OnFormClosing;
        _webHost.StatusChanged += OnServerStatusChanged;

        Controls.Add(BuildLayout());
        Populate(settings);
        UpdateServerStatus(_webHost.Status);
        if (!string.IsNullOrWhiteSpace(initialError))
        {
            _statusText.Text = initialError;
        }
    }

    public async Task InitializeServerAsync()
    {
        try
        {
            await _webHost.StartAsync(_appliedSettings).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SetStatusText(exception.Message);
        }
    }

    public async Task RefreshReportsAsync()
    {
        if (_busy)
        {
            return;
        }

        await RunBusyAsync(() => _webHost.RefreshAsync()).ConfigureAwait(true);
    }

    public void OpenViewer()
    {
        var url = _webHost.Status.ViewerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            _statusText.Text = "The viewer is not running.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _statusText.Text = $"The viewer could not be opened: {exception.Message}";
        }
    }

    public void ShowAndActivate()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ShowAndActivate);
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void AllowClose() => _allowClose = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webHost.StatusChanged -= OnServerStatusChanged;
        }

        base.Dispose(disposing);
    }

    private TableLayoutPanel BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));

        root.Controls.Add(BuildServerPanel(), 0, 0);
        root.Controls.Add(BuildSettingsPanel(), 0, 1);
        root.Controls.Add(BuildShowGroupsPanel(), 0, 2);
        root.Controls.Add(BuildButtonPanel(), 0, 3);

        _statusText.Dock = DockStyle.Fill;
        _statusText.Multiline = true;
        _statusText.ReadOnly = true;
        _statusText.ScrollBars = ScrollBars.Vertical;
        _statusText.AccessibleName = "Server and report status details";
        root.Controls.Add(_statusText, 0, 4);
        return root;
    }

    private FlowLayoutPanel BuildServerPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 10),
        };
        _serverStateLabel.AutoSize = true;
        _serverStateLabel.Font = new Font(Font, FontStyle.Bold);
        _serverStateLabel.Margin = new Padding(0, 5, 16, 0);
        _viewerUrlLink.AutoSize = true;
        _viewerUrlLink.Margin = new Padding(0, 5, 0, 0);
        _viewerUrlLink.LinkClicked += (_, _) => OpenViewer();
        panel.Controls.Add(_serverStateLabel);
        panel.Controls.Add(_viewerUrlLink);
        return panel;
    }

    private TableLayoutPanel BuildSettingsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(0, 0, 0, 10),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        var browseButton = new Button { Text = "Browse...", AutoSize = true };
        browseButton.Click += OnBrowseFolderClick;
        var testButton = new Button { Text = "Test folder", AutoSize = true };
        testButton.Click += OnTestFolderClick;
        var folderActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        folderActions.Controls.Add(browseButton);
        folderActions.Controls.Add(testButton);

        _reportFolderText.Dock = DockStyle.Fill;
        AddSetting(panel, 0, "Report folder", _reportFolderText, folderActions);

        _portInput.Minimum = 1024;
        _portInput.Maximum = 65535;
        _portInput.Width = 100;
        _bindAddressText.Dock = DockStyle.Fill;
        AddSetting(panel, 1, "Port", _portInput, "Bind address", _bindAddressText);

        _pathBaseText.Dock = DockStyle.Fill;
        _pathBaseText.PlaceholderText = "/ratings (optional)";
        _pollingIntervalInput.Minimum = 1;
        _pollingIntervalInput.Maximum = 1440;
        _pollingIntervalInput.Width = 100;
        AddSetting(panel, 2, "nginx path base", _pathBaseText, "Polling (minutes)", _pollingIntervalInput);

        _startAtSignInCheck.Text = "Start at Windows sign-in";
        _startAtSignInCheck.AutoSize = true;
        panel.Controls.Add(_startAtSignInCheck, 1, 3);
        panel.SetColumnSpan(_startAtSignInCheck, 3);
        return panel;
    }

    private TableLayoutPanel BuildShowGroupsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 4, 0, 8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Show groups",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        };
        var help = new Label
        {
            Text = "Separate multiple show-name wildcard filters with semicolons. Example: NEWS*; EVENING NEWS?",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 2, 0, 5),
        };
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(help, 0, 1);

        _showGroupsGrid.Dock = DockStyle.Fill;
        _showGroupsGrid.AutoGenerateColumns = false;
        _showGroupsGrid.AllowUserToAddRows = true;
        _showGroupsGrid.AllowUserToDeleteRows = true;
        _showGroupsGrid.RowHeadersVisible = false;
        _showGroupsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _showGroupsGrid.Columns.Add(CreateTextColumn("Id", "ID", 110));
        _showGroupsGrid.Columns.Add(CreateTextColumn("DisplayName", "Display name", 160));
        _showGroupsGrid.Columns.Add(CreateTextColumn("Aliases", "Show-name filters", 250, fill: true));
        _showGroupsGrid.Columns.Add(CreateTextColumn("SourcePattern", "Viewing source", 130));
        _showGroupsGrid.Columns.Add(CreateTextColumn("StartTime", "Start", 70));
        _showGroupsGrid.Columns.Add(CreateTextColumn("EndTime", "End", 70));
        _showGroupsGrid.AccessibleName = "Configured show groups";
        panel.Controls.Add(_showGroupsGrid, 0, 2);
        return panel;
    }

    private FlowLayoutPanel BuildButtonPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 5, 0, 5),
        };
        _saveAndApplyButton.Text = "Save && apply";
        _saveAndApplyButton.AutoSize = true;
        _saveAndApplyButton.Click += OnSaveAndApplyClick;
        _refreshButton.Text = "Refresh reports";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += async (_, _) => await RefreshReportsAsync().ConfigureAwait(true);
        var openViewerButton = new Button { Text = "Open viewer", AutoSize = true };
        openViewerButton.Click += (_, _) => OpenViewer();
        var exportButton = new Button { Text = "Export settings...", AutoSize = true };
        exportButton.Click += OnExportSettingsClick;
        var importButton = new Button { Text = "Import settings...", AutoSize = true };
        importButton.Click += OnImportSettingsClick;
        panel.Controls.Add(_saveAndApplyButton);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(openViewerButton);
        panel.Controls.Add(exportButton);
        panel.Controls.Add(importButton);
        return panel;
    }

    private static void AddSetting(
        TableLayoutPanel panel,
        int row,
        string firstLabel,
        Control firstControl,
        string secondLabel,
        Control secondControl)
    {
        panel.Controls.Add(CreateLabel(firstLabel), 0, row);
        panel.Controls.Add(firstControl, 1, row);
        panel.Controls.Add(CreateLabel(secondLabel), 2, row);
        panel.Controls.Add(secondControl, 3, row);
    }

    private static void AddSetting(
        TableLayoutPanel panel,
        int row,
        string label,
        Control control,
        Control trailingControl)
    {
        panel.Controls.Add(CreateLabel(label), 0, row);
        panel.Controls.Add(control, 1, row);
        panel.SetColumnSpan(control, 2);
        panel.Controls.Add(trailingControl, 3, row);
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 10, 3),
    };

    private static DataGridViewTextBoxColumn CreateTextColumn(
        string name,
        string header,
        int width,
        bool fill = false) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
    };

    private void Populate(LocalApplicationSettings settings)
    {
        _reportFolderText.Text = settings.ReportFolder;
        _portInput.Value = Math.Clamp(settings.Port, (int)_portInput.Minimum, (int)_portInput.Maximum);
        _bindAddressText.Text = settings.BindAddress;
        _pathBaseText.Text = settings.PathBase;
        _pollingIntervalInput.Value = Math.Clamp(
            settings.PollingIntervalMinutes,
            (int)_pollingIntervalInput.Minimum,
            (int)_pollingIntervalInput.Maximum);
        _startAtSignInCheck.Checked = settings.StartAtSignIn;
        _showGroupsGrid.Rows.Clear();
        foreach (var group in settings.ShowGroups)
        {
            _showGroupsGrid.Rows.Add(
                group.Id,
                group.DisplayName,
                string.Join("; ", group.Aliases),
                group.SourcePattern ?? string.Empty,
                FormatTime(group.StartTime),
                FormatTime(group.EndTime));
        }
    }

    private LocalApplicationSettings ReadSettings()
    {
        var groups = new List<ShowGroupConfiguration>();
        foreach (DataGridViewRow row in _showGroupsGrid.Rows)
        {
            if (row.IsNewRow || RowIsEmpty(row))
            {
                continue;
            }

            groups.Add(new ShowGroupConfiguration
            {
                Id = CellText(row, "Id"),
                DisplayName = CellText(row, "DisplayName"),
                Aliases = SplitFilters(CellText(row, "Aliases")),
                SourcePattern = NullIfWhiteSpace(CellText(row, "SourcePattern")),
                StartTime = ParseOptionalTime(CellText(row, "StartTime"), "Start time"),
                EndTime = ParseOptionalTime(CellText(row, "EndTime"), "End time"),
            });
        }

        return new LocalApplicationSettings
        {
            ReportFolder = _reportFolderText.Text.Trim(),
            Port = Decimal.ToInt32(_portInput.Value),
            BindAddress = _bindAddressText.Text.Trim(),
            PathBase = _pathBaseText.Text.Trim(),
            PollingIntervalMinutes = Decimal.ToInt32(_pollingIntervalInput.Value),
            StartAtSignIn = _startAtSignInCheck.Checked,
            ShowGroups = groups,
        };
    }

    private async void OnSaveAndApplyClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        LocalApplicationSettings candidate;
        try
        {
            _showGroupsGrid.EndEdit();
            candidate = ReadSettings();
        }
        catch (FormatException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var errors = LocalApplicationSettingsValidator.Validate(candidate);
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            var previous = _appliedSettings;
            var previousAutostart = previous.StartAtSignIn;
            try
            {
                await _webHost.RestartAsync(candidate).ConfigureAwait(true);
                _startAtSignInManager.SetEnabled(candidate.StartAtSignIn);
                await _settingsStore.SaveAsync(candidate).ConfigureAwait(true);
                _appliedSettings = candidate;
                _statusText.Text = $"Settings saved to {_settingsStore.SettingsPath}{Environment.NewLine}{_webHost.Status.Message}";
            }
            catch
            {
                TrySetAutostart(previousAutostart);
                try
                {
                    await _webHost.RestartAsync(previous).ConfigureAwait(true);
                }
                catch (Exception rollbackException)
                {
                    LocalLog.Write("Error", "settings_rollback_failed", rollbackException.Message, rollbackException);
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    private async void OnTestFolderClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await _folderAccessProbe
                .ProbeAsync(_reportFolderText.Text.Trim())
                .ConfigureAwait(true);
            _statusText.Text = result.Message;
        }).ConfigureAwait(true);
    }

    private async void OnExportSettingsClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        LocalApplicationSettings settings;
        try
        {
            _showGroupsGrid.EndEdit();
            settings = ReadSettings();
        }
        catch (FormatException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var errors = LocalApplicationSettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export NLTV Viewer settings",
            Filter = "JSON settings (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            FileName = "nltv-viewer-settings.json",
            OverwritePrompt = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _settingsTransferService
                .ExportAsync(dialog.FileName, settings)
                .ConfigureAwait(true);
            _statusText.Text = $"Settings exported to {dialog.FileName}.";
        }).ConfigureAwait(true);
    }

    private async void OnImportSettingsClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Import NLTV Viewer settings",
            Filter = "JSON settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var imported = await _settingsTransferService
                .ImportAsync(dialog.FileName)
                .ConfigureAwait(true);
            Populate(imported);
            _statusText.Text =
                $"Imported settings from {dialog.FileName}.{Environment.NewLine}"
                + "Review them, then click Save & apply to use them.";
        }).ConfigureAwait(true);
    }

    private void OnBrowseFolderClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder containing ratings reports",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_reportFolderText.Text))
        {
            dialog.SelectedPath = _reportFolderText.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _reportFolderText.Text = dialog.SelectedPath;
        }
    }

    private void OnServerStatusChanged(object? sender, ViewerServerStatus status)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateServerStatus(status));
            return;
        }

        UpdateServerStatus(status);
    }

    private void UpdateServerStatus(ViewerServerStatus status)
    {
        _serverStateLabel.Text = status.IsRunning ? "Server running" : "Server stopped";
        _serverStateLabel.ForeColor = status.IsRunning ? Color.DarkGreen : Color.DarkRed;
        _viewerUrlLink.Text = status.ViewerUrl;
        _viewerUrlLink.Visible = !string.IsNullOrWhiteSpace(status.ViewerUrl);
        _statusText.Text = BuildStatusText(status);
    }

    private void SetStatusText(string message)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatusText(message));
            return;
        }

        _statusText.Text = message;
    }

    private static string BuildStatusText(ViewerServerStatus status)
    {
        var lines = new List<string> { status.Message };
        if (status.LastRefreshAttempt is not null)
        {
            lines.Add($"Last refresh attempt: {status.LastRefreshAttempt:yyyy-MM-dd HH:mm:ss zzz}");
        }

        if (status.LastSuccessfulRefresh is not null)
        {
            lines.Add($"Last successful refresh: {status.LastSuccessfulRefresh:yyyy-MM-dd HH:mm:ss zzz}");
        }

        if (status.Warnings.Count > 0)
        {
            lines.Add("Warnings:");
            lines.AddRange(status.Warnings.Take(10));
            if (status.Warnings.Count > 10)
            {
                lines.Add($"...and {status.Warnings.Count - 10} more warning(s). See the local logs for details.");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        _saveAndApplyButton.Enabled = false;
        _refreshButton.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LocalLog.Write("Error", "desktop_action_failed", exception.Message, exception);
            _statusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _saveAndApplyButton.Enabled = true;
            _refreshButton.Enabled = true;
            _busy = false;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void TrySetAutostart(bool enabled)
    {
        try
        {
            _startAtSignInManager.SetEnabled(enabled);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            LocalLog.Write("Error", "autostart_rollback_failed", exception.Message, exception);
        }
    }

    private static string CellText(DataGridViewRow row, string columnName) =>
        Convert.ToString(row.Cells[columnName].Value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;

    private static bool RowIsEmpty(DataGridViewRow row) =>
        row.Cells.Cast<DataGridViewCell>().All(cell => string.IsNullOrWhiteSpace(
            Convert.ToString(cell.Value, CultureInfo.CurrentCulture)));

    private static List<string> SplitFilters(string value) =>
        value.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatTime(TimeOnly? value) =>
        value?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    private static TimeOnly? ParseOptionalTime(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeOnly.TryParseExact(
            value.Trim(),
            ["H:mm", "HH:mm", "h:mm tt", "hh:mm tt"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var time))
        {
            return time;
        }

        throw new FormatException($"{fieldName} '{value}' is invalid. Use HH:mm, such as 17:30.");
    }
}
