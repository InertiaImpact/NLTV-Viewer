using System.Diagnostics;
using System.Globalization;
using NltvViewer.App.Configuration;
using NltvViewer.App.Hosting;
using NltvViewer.Core.Reports;

namespace NltvViewer.App.Forms;

public sealed class MainForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(18, 22, 28);
    private static readonly Color PanelBackground = Color.FromArgb(24, 29, 37);
    private static readonly Color InputBackground = Color.FromArgb(34, 41, 52);
    private static readonly Color ReadOnlyBackground = Color.FromArgb(27, 33, 42);
    private static readonly Color BorderColor = Color.FromArgb(67, 78, 94);
    private static readonly Color PrimaryText = Color.FromArgb(235, 239, 244);
    private static readonly Color SecondaryText = Color.FromArgb(166, 177, 191);
    private static readonly Color AccentColor = Color.FromArgb(91, 169, 255);
    private static readonly Color AccentHoverColor = Color.FromArgb(112, 181, 255);
    private static readonly Color SelectionColor = Color.FromArgb(34, 93, 153);
    private static readonly Color SuccessColor = Color.FromArgb(91, 210, 143);
    private static readonly Color ErrorColor = Color.FromArgb(255, 112, 112);

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
    private readonly ComboBox _defaultDemographicInput = new();
    private readonly ComboBox _defaultMetricInput = new();
    private readonly TextBox _defaultComparisonsText = new();
    private readonly CheckBox _startAtSignInCheck = new();
    private readonly DataGridView _showGroupsGrid = new();
    private readonly Label _serverStateLabel = new();
    private readonly Label _versionLabel = new();
    private readonly LinkLabel _viewerUrlLink = new();
    private readonly TextBox _statusText = new();
    private readonly Button _saveAndApplyButton = new();
    private readonly Button _refreshButton = new();
    private LocalApplicationSettings _appliedSettings;
    private bool _allowClose;
    private bool _busy;
    private bool _populatingRatingChoices;
    private RatingCapabilities _ratingCapabilities = RatingCapabilities.Empty;

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

        Text = $"NLTV Viewer Settings — v{ApplicationVersion.Informational}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1080, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = WindowBackground;
        ForeColor = PrimaryText;
        FormClosing += OnFormClosing;
        _webHost.StatusChanged += OnServerStatusChanged;

        Controls.Add(BuildLayout());
        ApplyDarkTheme(this);
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
        _versionLabel.AutoSize = true;
        _versionLabel.Text = $"v{ApplicationVersion.Informational}";
        _versionLabel.ForeColor = SecondaryText;
        _versionLabel.Font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));
        _versionLabel.Margin = new Padding(12, 6, 0, 0);
        _versionLabel.AccessibleName = $"Application version {ApplicationVersion.Informational}";
        panel.Controls.Add(_serverStateLabel);
        panel.Controls.Add(_viewerUrlLink);
        panel.Controls.Add(_versionLabel);
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

        _defaultDemographicInput.Dock = DockStyle.Fill;
        _defaultDemographicInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _defaultDemographicInput.SelectedIndexChanged += OnPreferredDemographicChanged;
        _defaultMetricInput.Dock = DockStyle.Fill;
        _defaultMetricInput.DropDownStyle = ComboBoxStyle.DropDownList;
        AddSetting(
            panel,
            3,
            "Preferred demographic",
            _defaultDemographicInput,
            "Preferred metric",
            _defaultMetricInput);

        _defaultComparisonsText.Dock = DockStyle.Fill;
        _defaultComparisonsText.PlaceholderText = "last-week; two-weeks-ago; last-year";
        var comparisonsHelp = new Label
        {
            Text = "Up to 4: previous-day, last-week, two-weeks-ago, last-month, last-year, or date:YYYY-MM-DD",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 1, 0, 4),
        };
        panel.Controls.Add(CreateLabel("Default comparisons"), 0, 4);
        panel.Controls.Add(_defaultComparisonsText, 1, 4);
        panel.SetColumnSpan(_defaultComparisonsText, 3);
        panel.Controls.Add(comparisonsHelp, 1, 5);
        panel.SetColumnSpan(comparisonsHelp, 3);

        _startAtSignInCheck.Text = "Start at Windows sign-in";
        _startAtSignInCheck.AutoSize = true;
        panel.Controls.Add(_startAtSignInCheck, 1, 6);
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
            ForeColor = SecondaryText,
            Padding = new Padding(0, 2, 0, 5),
        };
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(help, 0, 1);

        _showGroupsGrid.Dock = DockStyle.Fill;
        _showGroupsGrid.AutoGenerateColumns = false;
        _showGroupsGrid.AllowUserToAddRows = true;
        _showGroupsGrid.AllowUserToDeleteRows = true;
        _showGroupsGrid.RowHeadersVisible = false;
        _showGroupsGrid.EnableHeadersVisualStyles = false;
        _showGroupsGrid.BackgroundColor = PanelBackground;
        _showGroupsGrid.BorderStyle = BorderStyle.FixedSingle;
        _showGroupsGrid.GridColor = BorderColor;
        _showGroupsGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = InputBackground,
            ForeColor = PrimaryText,
            SelectionBackColor = InputBackground,
            SelectionForeColor = PrimaryText,
            Font = new Font(Font, FontStyle.Bold),
        };
        _showGroupsGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelBackground,
            ForeColor = PrimaryText,
            SelectionBackColor = SelectionColor,
            SelectionForeColor = Color.White,
        };
        _showGroupsGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(28, 34, 43),
            ForeColor = PrimaryText,
            SelectionBackColor = SelectionColor,
            SelectionForeColor = Color.White,
        };
        _showGroupsGrid.RowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelBackground,
            ForeColor = PrimaryText,
            SelectionBackColor = SelectionColor,
            SelectionForeColor = Color.White,
        };
        _showGroupsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _showGroupsGrid.Columns.Add(CreateTextColumn("Id", "ID", 110));
        _showGroupsGrid.Columns.Add(CreateTextColumn("DisplayName", "Display name", 160));
        _showGroupsGrid.Columns.Add(CreateTextColumn("Aliases", "Show-name filters", 250, fill: true));
        _showGroupsGrid.Columns.Add(CreateTextColumn("SourcePattern", "Viewing source", 130));
        _showGroupsGrid.Columns.Add(CreateTextColumn("StartTime", "Start", 70));
        _showGroupsGrid.Columns.Add(CreateTextColumn("EndTime", "End", 70));
        _showGroupsGrid.EditingControlShowing += (_, eventArgs) =>
        {
            eventArgs.Control.BackColor = InputBackground;
            eventArgs.Control.ForeColor = PrimaryText;
        };
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
        PopulateRatingChoices(
            _ratingCapabilities,
            settings.DefaultDemographicId,
            settings.DefaultMetricId);
        _defaultComparisonsText.Text = string.Join(
            "; ",
            settings.DefaultComparisonSpecs ?? []);
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
            DefaultDemographicId = SelectedChoiceId(_defaultDemographicInput),
            DefaultMetricId = SelectedChoiceId(_defaultMetricInput),
            DefaultComparisonSpecs = _defaultComparisonsText.Text
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList(),
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
        var selectedDemographic = SelectedChoiceId(_defaultDemographicInput);
        var selectedMetric = SelectedChoiceId(_defaultMetricInput);
        PopulateRatingChoices(status.Capabilities, selectedDemographic, selectedMetric);
        _serverStateLabel.Text = status.IsRunning ? "Server running" : "Server stopped";
        _serverStateLabel.ForeColor = status.IsRunning ? SuccessColor : ErrorColor;
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

    private static void ApplyDarkTheme(Control control)
    {
        var usesSecondaryText = control.ForeColor == SecondaryText;
        control.BackColor = control switch
        {
            TextBox textBox when textBox.ReadOnly => ReadOnlyBackground,
            TextBox or ComboBox or NumericUpDown => InputBackground,
            Panel or DataGridView => PanelBackground,
            _ => WindowBackground,
        };
        control.ForeColor = usesSecondaryText ? SecondaryText : PrimaryText;

        switch (control)
        {
            case Button button:
                button.UseVisualStyleBackColor = false;
                button.BackColor = InputBackground;
                button.ForeColor = PrimaryText;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = BorderColor;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 69);
                button.FlatAppearance.MouseDownBackColor = SelectionColor;
                button.EnabledChanged += OnThemedButtonEnabledChanged;
                break;
            case LinkLabel link:
                link.LinkColor = AccentColor;
                link.ActiveLinkColor = AccentHoverColor;
                link.VisitedLinkColor = Color.FromArgb(175, 136, 255);
                break;
            case TextBox input:
                input.BackColor = input.ReadOnly ? ReadOnlyBackground : InputBackground;
                input.ForeColor = PrimaryText;
                input.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.BackColor = InputBackground;
                combo.ForeColor = PrimaryText;
                combo.FlatStyle = FlatStyle.Flat;
                combo.EnabledChanged += OnThemedInputEnabledChanged;
                break;
            case NumericUpDown numeric:
                numeric.BackColor = InputBackground;
                numeric.ForeColor = PrimaryText;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                numeric.EnabledChanged += OnThemedInputEnabledChanged;
                break;
            case DataGridView grid:
                grid.BackgroundColor = PanelBackground;
                grid.GridColor = BorderColor;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyDarkTheme(child);
        }
    }

    private static void OnThemedButtonEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            button.BackColor = button.Enabled ? InputBackground : ReadOnlyBackground;
            button.ForeColor = button.Enabled ? PrimaryText : SecondaryText;
        }
    }

    private static void OnThemedInputEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Control input)
        {
            input.BackColor = input.Enabled ? InputBackground : ReadOnlyBackground;
            input.ForeColor = input.Enabled ? PrimaryText : SecondaryText;
        }
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

    private void PopulateRatingChoices(
        RatingCapabilities capabilities,
        string? selectedDemographicId,
        string? selectedMetricId)
    {
        _populatingRatingChoices = true;
        try
        {
            _ratingCapabilities = capabilities;
            _defaultDemographicInput.Items.Clear();
            _defaultDemographicInput.Items.Add(RatingChoice.Auto);
            foreach (var demographic in capabilities.Demographics)
            {
                _defaultDemographicInput.Items.Add(new RatingChoice(
                    demographic.Id,
                    demographic.Label));
            }

            AddUnavailableChoiceIfNeeded(
                _defaultDemographicInput,
                selectedDemographicId,
                "Unavailable audience");
            SelectChoice(_defaultDemographicInput, selectedDemographicId);
            PopulateMetricChoices(selectedMetricId);
        }
        finally
        {
            _populatingRatingChoices = false;
        }
    }

    private void PopulateMetricChoices(string? selectedMetricId)
    {
        var demographicId = SelectedChoiceId(_defaultDemographicInput);
        _defaultMetricInput.Items.Clear();
        _defaultMetricInput.Items.Add(RatingChoice.Auto);
        if (!string.IsNullOrWhiteSpace(demographicId))
        {
            var validMetricIds = _ratingCapabilities.AvailableSelections
                .Where(selection => string.Equals(
                    selection.DemographicId,
                    demographicId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(selection => selection.MetricId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var metric in _ratingCapabilities.Metrics.Where(metric =>
                         validMetricIds.Contains(metric.Id)))
            {
                var unit = string.IsNullOrWhiteSpace(metric.Unit) ? string.Empty : $" ({metric.Unit})";
                _defaultMetricInput.Items.Add(new RatingChoice(
                    metric.Id,
                    $"{metric.Label}{unit}"));
            }

            AddUnavailableChoiceIfNeeded(
                _defaultMetricInput,
                selectedMetricId,
                "Unavailable measure");
        }

        SelectChoice(
            _defaultMetricInput,
            string.IsNullOrWhiteSpace(demographicId) ? null : selectedMetricId);
        _defaultMetricInput.Enabled = !string.IsNullOrWhiteSpace(demographicId);
    }

    private void OnPreferredDemographicChanged(object? sender, EventArgs e)
    {
        if (!_populatingRatingChoices)
        {
            _populatingRatingChoices = true;
            try
            {
                PopulateMetricChoices(selectedMetricId: null);
            }
            finally
            {
                _populatingRatingChoices = false;
            }
        }
    }

    private static void AddUnavailableChoiceIfNeeded(
        ComboBox input,
        string? selectedId,
        string label)
    {
        if (string.IsNullOrWhiteSpace(selectedId)
            || input.Items.Cast<RatingChoice>().Any(choice => string.Equals(
                choice.Id,
                selectedId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        input.Items.Add(new RatingChoice(selectedId.Trim(), $"{label}: {selectedId.Trim()}"));
    }

    private static void SelectChoice(ComboBox input, string? selectedId)
    {
        var index = input.Items.Cast<RatingChoice>()
            .Select((choice, index) => (choice, index))
            .FirstOrDefault(pair => string.Equals(
                pair.choice.Id,
                selectedId?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            .index;
        input.SelectedIndex = index;
    }

    private static string SelectedChoiceId(ComboBox input) =>
        input.SelectedItem is RatingChoice choice ? choice.Id : string.Empty;

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

    private sealed record RatingChoice(string Id, string Label)
    {
        public static RatingChoice Auto { get; } = new(string.Empty, "Auto (report default)");

        public override string ToString() => Label;
    }
}
