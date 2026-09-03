using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace StreakWatchV935;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        // Native Messaging mode MUST exit before any WinForms UI or single-instance logic.
        // Firefox launches this same executable as a background stdio host.
        string[] args = Environment.GetCommandLineArgs();
        bool nativeHostMode = args.Skip(1).Any(a =>
            a.Equals("--native-host", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("native-host", StringComparison.OrdinalIgnoreCase));

        if (nativeHostMode)
        {
            NativeMessagingHost.Run();
            return;
        }

        // Restart handoff mode: the newly spawned process waits for the old
        // StreakWatch PID to fully exit before attempting the single-instance mutex.
        int restartIndex = Array.FindIndex(args, a =>
            a.Equals("--restart-wait", StringComparison.OrdinalIgnoreCase));
        if (restartIndex >= 0 &&
            restartIndex + 1 < args.Length &&
            int.TryParse(args[restartIndex + 1], out int oldPid))
        {
            try
            {
                using Process oldProcess = Process.GetProcessById(oldPid);
                oldProcess.WaitForExit(10000);
            }
            catch
            {
                // Old process already exited (or PID no longer exists).
            }
        }

        bool createdNew;
        _mutex = new Mutex(true, @"Local\StreakWatchV90_SingleInstance", out createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "StreakWatch is already running.",
                "StreakWatch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        AppPaths.EnsureDirectories();
        Logger.Initialize();
        SettingsStore.TryMigrateLegacySettings();

        var settings = SettingsStore.Load();
        Application.Run(new MainForm(settings));
    }
}

internal static class AppPaths
{
    public static readonly string AppDataFolder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreakWatch");

    public static readonly string SettingsPath =
        Path.Combine(AppDataFolder, "settings.json");

    public static readonly string StatePath =
        Path.Combine(AppDataFolder, "state.json");

    public static readonly string LogsFolder =
        Path.Combine(AppDataFolder, "Logs");

    public static readonly string LatestLogPath =
        Path.Combine(LogsFolder, "latest.log");

    public static string LogPath => Logger.CurrentLogPath ?? LatestLogPath;

    public static readonly string BrowserBridgePath =
        Path.Combine(AppDataFolder, "browser-bridge.json");

    public static readonly string BrowserStatusPath =
        Path.Combine(AppDataFolder, "browser-status.json");

    public static readonly string NativeHeartbeatPath =
        Path.Combine(AppDataFolder, "native-heartbeat.json");

    public static readonly string RemoteSnapshotPath =
        Path.Combine(AppDataFolder, "remote-monitor-snapshot.json");

    public static readonly string StartupHealthPath =
        Path.Combine(AppDataFolder, "startup-health.txt");

    public static readonly string DiscordSecretPath =
        Path.Combine(AppDataFolder, "discord-secret.json");

    public static readonly string DiscordQueuePath =
        Path.Combine(AppDataFolder, "discord-queue.json");

    public static readonly string EventHistoryPath =
        Path.Combine(AppDataFolder, "event-history.log");

    public static readonly string DiagnosticsPath =
        Path.Combine(AppDataFolder, "diagnostics-latest.txt");

    public static readonly string BackupsFolder =
        Path.Combine(AppDataFolder, "Backups");

    public static readonly string UpdatesPath =
        Path.Combine(AppContext.BaseDirectory, "chatgptupdates.md");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(BackupsFolder);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

internal static class EventHistory
{
    private static readonly object Sync = new();
    public static void Add(string category, string channel, string message)
    {
        try { AppPaths.EnsureDirectories(); lock(Sync) File.AppendAllText(AppPaths.EventHistoryPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {category} | {channel} | {message}" + Environment.NewLine); } catch { }
    }
    public static string ReadRecent(int maxLines=500)
    {
        try { return File.Exists(AppPaths.EventHistoryPath) ? string.Join(Environment.NewLine, File.ReadLines(AppPaths.EventHistoryPath).TakeLast(maxLines)) : "No event history yet."; } catch(Exception ex) { return ex.Message; }
    }
}

internal static class SettingsBackup
{
    public static void Create()
    {
        try { if(!File.Exists(AppPaths.SettingsPath)) return; AppPaths.EnsureDirectories(); string d=Path.Combine(AppPaths.BackupsFolder,$"settings-{DateTime.Now:yyyyMMdd-HHmmss}.json"); File.Copy(AppPaths.SettingsPath,d,true); foreach(string f in Directory.GetFiles(AppPaths.BackupsFolder,"settings-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(10)) File.Delete(f); } catch { }
    }
}

internal sealed class MainForm : Form
{
    private Settings _settings;
    private readonly TwitchWatcher _watcher;
    private readonly NotifyIcon _trayIcon;

    private readonly DataGridView _grid = new();
    private readonly TextBox _channelText = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _checkNowButton = new();
    private readonly Button _pauseButton = new();
    private readonly ComboBox _browserCombo = new();
    private readonly CheckBox _autoOpenCheck = new();
    private readonly CheckBox _startupCheck = new();
    private readonly CheckBox _openStartupLiveCheck = new();
    private readonly NumericUpDown _intervalNumeric = new();
    private readonly Label _statusLabel = new();
    private readonly Label _dashboardLabel = new();
    private readonly Label _bridgeStatusLabel = new();
    private readonly System.Windows.Forms.Timer _bridgeStatusTimer = new();
    private readonly DiscordNotifier _discordNotifier;
    private readonly Dictionary<string, string> _previousPlayback =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _recoveryAlertsSent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _recoveryNotifyTimer = new();
    private readonly System.Windows.Forms.Timer _livePlaybackAlertTimer = new();
    private readonly Dictionary<string, ChannelUiStatus> _latestStatuses =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshingGrid;

    public MainForm(Settings settings)
    {
        _settings = settings;
        _discordNotifier = new DiscordNotifier(_settings);

        Text = "StreakWatch V9.3.5";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Width = 1120;
        Height = 680;
        MinimumSize = new Size(920, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        LoadSettingsIntoUi();

        _watcher = new TwitchWatcher(_settings);
        _watcher.StatusChanged += Watcher_StatusChanged;
        _watcher.GeneralStatusChanged += message =>
        {
            if (IsDisposed) return;
            BeginInvoke(() => _statusLabel.Text = message);
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open StreakWatch", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Check now", null, async (_, _) => await _watcher.CheckAllAsync(true));
        trayMenu.Items.Add("Open log", null, (_, _) => OpenFile(AppPaths.LogPath));
        trayMenu.Items.Add("Open settings folder", null, (_, _) => OpenFolder(AppPaths.AppDataFolder));
        trayMenu.Items.Add("Firefox extension setup", null, (_, _) =>
        {
            string setup = Path.Combine(AppContext.BaseDirectory, "FirefoxExtension", "INSTALL.txt");
            if (File.Exists(setup))
                OpenFile(setup);
            else
                MessageBox.Show("FirefoxExtension\\INSTALL.txt was not found next to StreakWatch.");
        });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, async (_, _) => await ExitApplicationAsync("Exit selected from system tray"));

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "StreakWatch",
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        _watcher.PlaybackProblem += message =>
        {
            EventHistory.Add("PLAYBACK-PROBLEM", "", message);
            if (IsDisposed) return;
            BeginInvoke(() =>
                _trayIcon.ShowBalloonTip(
                    6000,
                    "StreakWatch playback problem",
                    message,
                    ToolTipIcon.Warning));
        };

        _watcher.LiveStarted += (channel, streamId) =>
        {
            EventHistory.Add("LIVE", channel, $"stream={streamId ?? "unknown"}");
            if (_settings.DiscordEnabled)
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.Live,
                    $"🔴 **{channel} is LIVE**",
                    $"https://www.twitch.tv/{channel}\nStream ID: `{streamId ?? "unknown"}`");
        };

        _watcher.NetworkChanged += restored =>
        {
            EventHistory.Add("NETWORK", "", restored ? "RESTORED" : "LOST");
            if (!_settings.DiscordEnabled) return;
            if (restored && _settings.DiscordNotifyNetworkRestored)
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.NetworkRestored,
                    "🌐 **Internet/Twitch connection restored**",
                    "StreakWatch resumed checks and browser recovery.");
            else if (!restored && _settings.DiscordNotifyNetworkLost)
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.NetworkLost,
                    "⚠️ **Internet/Twitch connection lost**",
                    "StreakWatch will keep retrying automatically.");
        };

        _watcher.PlaybackProblem += message =>
        {
            if (_settings.DiscordEnabled)
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.PlaybackProblem,
                    "⚠️ **Playback problem**",
                    message);
        };

        _watcher.StreamOffline += channel =>
        {
            EventHistory.Add("OFFLINE", channel, "grace-confirmed");
            if (_settings.DiscordEnabled)
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.Offline,
                    $"⚫ **{channel} went OFFLINE**",
                    $"https://www.twitch.tv/{channel}");
        };

        _recoveryNotifyTimer.Interval = 30_000;
        _recoveryNotifyTimer.Tick += (_, _) => CheckRecoveryImportanceNotifications();
        _recoveryNotifyTimer.Start();
        _livePlaybackAlertTimer.Interval=10000; _livePlaybackAlertTimer.Tick += (_,_) => CheckLiveNotPlayingAlerts(); _livePlaybackAlertTimer.Start();

        _bridgeStatusTimer.Interval = 2000;
        _bridgeStatusTimer.Tick += (_, _) => RefreshBridgeStatus();
        _bridgeStatusTimer.Start();
        RefreshBridgeStatus();

        var startupHealthTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        startupHealthTimer.Tick += async (_, _) =>
        {
            startupHealthTimer.Stop();
            await RunStartupHealthCheckAsync();
            startupHealthTimer.Dispose();
        };
        startupHealthTimer.Start();

        FormClosing += MainForm_FormClosing;
        Shown += async (_, _) =>
        {
            await _watcher.CheckAllAsync(true);
            if(_settings.UpdateCheckEnabled) _=CheckForUpdatesAsync();
        };
    }

    private void RefreshBridgeStatus()
    {
        bool firefoxRunning;
        try
        {
            firefoxRunning = Process.GetProcessesByName("firefox").Length > 0;
        }
        catch
        {
            firefoxRunning = false;
        }

        DateTime? heartbeatUtc = null;
        string extensionVersion = "unknown";
        try
        {
            if (File.Exists(AppPaths.NativeHeartbeatPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AppPaths.NativeHeartbeatPath));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("updatedUtc", out JsonElement updated) &&
                    updated.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(updated.GetString(), out DateTime parsed))
                    heartbeatUtc = parsed.ToUniversalTime();

                if (root.TryGetProperty("extensionVersion", out JsonElement version) &&
                    version.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(version.GetString()))
                    extensionVersion = version.GetString()!;
            }
        }
        catch { }

        double? ageSeconds = heartbeatUtc.HasValue
            ? Math.Max(0, (DateTime.UtcNow - heartbeatUtc.Value).TotalSeconds)
            : null;
        bool connected = firefoxRunning && ageSeconds.HasValue && ageSeconds.Value <= 10;

        string heartbeatText = ageSeconds.HasValue
            ? $"{Math.Round(ageSeconds.Value):0}s ago"
            : "never";
        _bridgeStatusLabel.Text =
            $"Firefox: {(firefoxRunning ? "Running" : "Not running")} | " +
            $"Bridge: {(connected ? "Connected ✓" : "Disconnected ⚠")} | " +
            $"Extension: {extensionVersion} | Heartbeat: {heartbeatText}";
    }

    private async Task RunStartupHealthCheckAsync()
    {
        try
        {
            bool firefox = Process.GetProcessesByName("firefox").Length > 0;
            bool bridge = File.Exists(AppPaths.NativeHeartbeatPath) &&
                          (DateTime.UtcNow - File.GetLastWriteTimeUtc(AppPaths.NativeHeartbeatPath)).TotalSeconds <= 10;
            bool twitch = false;
            try
            {
                await _watcher.CheckAllAsync(true);
                twitch = true;
            }
            catch { }

            string report =
                $"StreakWatch V9.3.5 startup health{Environment.NewLine}" +
                $"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                $"Firefox: {(firefox ? "OK" : "NOT RUNNING")}{Environment.NewLine}" +
                $"Bridge: {(bridge ? "CONNECTED" : "DISCONNECTED")}{Environment.NewLine}" +
                $"Twitch check: {(twitch ? "COMPLETED" : "FAILED")}{Environment.NewLine}" +
                $"Discord configured: {(DiscordSecretStore.LoadWebhooks().Count > 0 ? "YES" : "NO")}";

            File.WriteAllText(AppPaths.StartupHealthPath, report);
            EventHistory.Add("STARTUP-HEALTH", "", report.Replace(Environment.NewLine, " | "));
            if (!bridge && firefox)
                _trayIcon.ShowBalloonTip(5000, "StreakWatch startup health", "Firefox is running but Browser Bridge is not connected.", ToolTipIcon.Warning);
        }
        catch (Exception ex) { Logger.Log($"STARTUP HEALTH | FAILED | {ex.Message}"); }
    }

    private void BuildUi()
    {
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            Padding = new Padding(10)
        };

        _channelText.PlaceholderText = "Twitch channel name or URL";
        _channelText.Width = 430;
        _channelText.Left = 10;
        _channelText.Top = 15;

        _addButton.Text = "Add";
        _addButton.Width = 90;
        _addButton.Left = 450;
        _addButton.Top = 13;
        _addButton.Click += (_, _) => AddChannel();

        _removeButton.Text = "Remove selected";
        _removeButton.Width = 130;
        _removeButton.Left = 550;
        _removeButton.Top = 13;
        _removeButton.Click += (_, _) => RemoveSelected();

        _checkNowButton.Text = "Check now";
        _checkNowButton.Width = 100;
        _checkNowButton.Left = 690;
        _checkNowButton.Top = 13;
        _checkNowButton.Click += async (_, _) => await _watcher.CheckAllAsync(true);

        var restartButton = new Button
        {
            Text = "Restart",
            Width = 90,
            Left = 800,
            Top = 13
        };
        restartButton.Click += (_, _) => RestartApplication();

        _pauseButton.Text = "Pause monitoring";
        _pauseButton.Width = 130;
        _pauseButton.Left = 900;
        _pauseButton.Top = 13;
        _pauseButton.Click += (_, _) => ToggleMonitoringPause();

        var topHint = new Label
        {
            Text = "Tip: right-click a channel for Test channel and recovery actions.",
            AutoSize = true,
            Left = 10,
            Top = 58
        };

        top.Controls.AddRange([
            _channelText, _addButton, _removeButton, _checkNowButton,
            restartButton, _pauseButton, topHint
        ]);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;

        _grid.Columns.Add("Channel", "Channel");
        _grid.Columns.Add("Status", "Live");
        _grid.Columns.Add("Playback", "Playback");
        _grid.Columns.Add("Confidence", "Detection");
        _grid.Columns.Add("StreamId", "Stream ID");
        _grid.Columns.Add("LastSeen", "Last seen");
        _grid.Columns.Add("LastPlayback", "Last playback OK");

        var autoCol = new DataGridViewCheckBoxColumn
        {
            Name = "AutoOpen",
            HeaderText = "Auto open",
            FillWeight = 55
        };
        var closeCol = new DataGridViewCheckBoxColumn
        {
            Name = "CloseOffline",
            HeaderText = "Close offline",
            FillWeight = 65
        };
        var muteCol = new DataGridViewCheckBoxColumn
        {
            Name = "Mute",
            HeaderText = "Mute",
            FillWeight = 45
        };

        _grid.Columns.Add(autoCol);
        _grid.Columns.Add(closeCol);
        var recoveryCol = new DataGridViewTextBoxColumn
        {
            Name = "Recovery",
            HeaderText = "Recovery deadline",
            FillWeight = 90,
            ReadOnly = true
        };

        _grid.Columns.Add(muteCol);
        var safetyCol = new DataGridViewTextBoxColumn
        {
            Name = "Safety",
            HeaderText = "Safety",
            FillWeight = 75,
            ReadOnly = true
        };
        _grid.Columns.Add(safetyCol);
        _grid.Columns.Add(recoveryCol);

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name is not ("AutoOpen" or "CloseOffline" or "Mute"))
                col.ReadOnly = true;
        }

        _grid.CellValueChanged += (_, e) => SaveChannelPolicyFromGrid(e.RowIndex, e.ColumnIndex);
        var recoveryMenu = new ContextMenuStrip();
        recoveryMenu.Items.Add("Test channel", null, async (_, _) => await TestSelectedChannelAsync());
        recoveryMenu.Items.Add(new ToolStripSeparator());
        recoveryMenu.Items.Add("Start 24h recovery", null, (_, _) => StartRecoveryForSelected());
        recoveryMenu.Items.Add("Clear recovery", null, (_, _) => ClearRecoveryForSelected());
        _grid.ContextMenuStrip = recoveryMenu;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.DataError += (_, e) =>
        {
            Logger.Log($"UI | DATAGRID DATA ERROR | row={e.RowIndex} col={e.ColumnIndex} | {e.Exception?.Message}");
            e.ThrowException = false;
            e.Cancel = false;
        };

        var settingsPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 205,
            Padding = new Padding(10)
        };

        var browserLabel = new Label
        {
            Text = "Browser:",
            AutoSize = true,
            Left = 10,
            Top = 15
        };

        _browserCombo.Left = 75;
        _browserCombo.Top = 10;
        _browserCombo.Width = 130;
        _browserCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _browserCombo.Items.AddRange(["Firefox", "Default"]);
        _browserCombo.SelectedIndex = 0;

        _autoOpenCheck.Text = "Open new LIVE streams automatically";
        _autoOpenCheck.AutoSize = true;
        _autoOpenCheck.Left = 230;
        _autoOpenCheck.Top = 12;

        _startupCheck.Text = "Start with Windows";
        _startupCheck.AutoSize = true;
        _startupCheck.Left = 510;
        _startupCheck.Top = 12;

        _openStartupLiveCheck.Text = "Open channels already LIVE when StreakWatch starts";
        _openStartupLiveCheck.AutoSize = true;
        _openStartupLiveCheck.Left = 10;
        _openStartupLiveCheck.Top = 48;

        var intervalLabel = new Label
        {
            Text = "Check every:",
            AutoSize = true,
            Left = 410,
            Top = 49
        };

        _intervalNumeric.Left = 490;
        _intervalNumeric.Top = 44;
        _intervalNumeric.Minimum = 10;
        _intervalNumeric.Maximum = 3600;
        _intervalNumeric.Width = 75;

        var secondsLabel = new Label
        {
            Text = "seconds",
            AutoSize = true,
            Left = 570,
            Top = 49
        };

        _saveButton.Text = "Save settings";
        _saveButton.Width = 120;
        _saveButton.Left = 650;
        _saveButton.Top = 42;
        _saveButton.Click += (_, _) => SaveSettingsFromUi();

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = true;
        _statusLabel.Left = 10;
        _statusLabel.Top = 82;

        _dashboardLabel.Text = "LIVE: 0 | Playing: 0 | Recovering: 0 | Offline: 0";
        _dashboardLabel.AutoSize = true;
        _dashboardLabel.Left = 10;
        _dashboardLabel.Top = 105;

        _bridgeStatusLabel.Text = "Firefox: checking... | Bridge: checking...";
        _bridgeStatusLabel.AutoSize = true;
        _bridgeStatusLabel.Left = 410;
        _bridgeStatusLabel.Top = 82;

        var exportButton = new Button
        {
            Text = "Export settings",
            Width = 110,
            Height = 30
        };
        exportButton.Click += (_, _) => ExportSettings();

        var importButton = new Button
        {
            Text = "Import settings",
            Width = 110,
            Height = 30
        };
        importButton.Click += (_, _) => ImportSettings();

        var discordButton = new Button
        {
            Text = "Discord",
            Width = 100,
            Height = 30
        };
        discordButton.Click += (_, _) => OpenDiscordSettings();

        var logsButton = new Button
        {
            Text = "Log history",
            Width = 100,
            Height = 30
        };
        logsButton.Click += (_, _) => OpenFolder(AppPaths.LogsFolder);

        var historyButton = new Button
        {
            Text = "History",
            Width = 85,
            Height = 30
        };
        historyButton.Click += (_, _) => OpenHistoryWindow();

        var diagnosticsButton = new Button
        {
            Text = "Diagnostics",
            Width = 95,
            Height = 30
        };
        diagnosticsButton.Click += (_, _) => OpenDiagnosticsWindow();

        var advancedButton = new Button
        {
            Text = "Advanced",
            Width = 90,
            Height = 30
        };
        advancedButton.Click += (_, _) => OpenAdvancedSettings();

        var actionsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6, 3, 6, 3),
            Margin = Padding.Empty
        };
        actionsFlow.Controls.AddRange([
            discordButton, exportButton, importButton, logsButton,
            historyButton, diagnosticsButton, advancedButton
        ]);

        var linksFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6, 5, 6, 0),
            Margin = Padding.Empty
        };

        var openLog = new LinkLabel
        {
            Text = "Open log",
            AutoSize = true,
            Margin = new Padding(3, 2, 20, 0)
        };
        openLog.Click += (_, _) => OpenFile(AppPaths.LogPath);

        var openFolder = new LinkLabel
        {
            Text = "Open AppData folder",
            AutoSize = true,
            Margin = new Padding(3, 2, 20, 0)
        };
        openFolder.Click += (_, _) => OpenFolder(AppPaths.AppDataFolder);

        linksFlow.Controls.AddRange([openLog, openFolder]);

        settingsPanel.Controls.AddRange([
            browserLabel, _browserCombo, _autoOpenCheck, _startupCheck,
            _openStartupLiveCheck, intervalLabel, _intervalNumeric, secondsLabel,
            _saveButton, _statusLabel, _dashboardLabel, _bridgeStatusLabel
        ]);

        settingsPanel.Controls.Add(actionsFlow);
        settingsPanel.Controls.Add(linksFlow);

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(settingsPanel);
    }

    private void LoadSettingsIntoUi()
    {
        _browserCombo.SelectedItem =
            _settings.Browser.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                ? "Firefox"
                : "Default";

        _autoOpenCheck.Checked = _settings.OpenStreamAutomatically;
        _startupCheck.Checked = _settings.StartWithWindows;
        _openStartupLiveCheck.Checked = _settings.OpenAlreadyLiveOnStartup;
        _intervalNumeric.Value = Math.Clamp(_settings.CheckEverySeconds, 10, 3600);

        RefreshGridFromSettings();
    }

    private void RefreshGridFromSettings()
    {
        var existing = _grid.Rows
            .Cast<DataGridViewRow>()
            .Where(r => r.Cells["Channel"].Value is string)
            .ToDictionary(
                r => (string)r.Cells["Channel"].Value!,
                r => r,
                StringComparer.OrdinalIgnoreCase);

        foreach (string channel in _settings.Channels
                     .Select(ChannelTools.Normalize)
                     .Where(c => c.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.ContainsKey(channel))
            {
                ChannelPreference pref = ChannelPreferenceTools.Get(_settings, channel);
                _refreshingGrid = true;
                _grid.Rows.Add(
                    channel,          // Channel
                    "Unknown",        // Live
                    "Unknown",        // Playback
                    "Unknown",        // Detection
                    "",               // Stream ID
                    "",               // Last seen
                    "",               // Last playback OK
                    pref.AutoOpen,    // Auto open
                    pref.CloseWhenOffline, // Close offline
                    pref.Mute,        // Mute
                    "",               // Safety
                    RecoveryText(pref)); // Recovery deadline
                _refreshingGrid = false;
            }
            else
            {
                ChannelPreference pref = ChannelPreferenceTools.Get(_settings, channel);
                _refreshingGrid = true;
                existing[channel].Cells["AutoOpen"].Value = pref.AutoOpen;
                existing[channel].Cells["CloseOffline"].Value = pref.CloseWhenOffline;
                existing[channel].Cells["Mute"].Value = pref.Mute;
                existing[channel].Cells["Recovery"].Value = RecoveryText(pref);
                _refreshingGrid = false;
            }
        }

        var wanted = _settings.Channels
            .Select(ChannelTools.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string stale in _latestStatuses.Keys
                     .Where(channel => !wanted.Contains(channel))
                     .ToList())
        {
            _latestStatuses.Remove(stale);
        }

        foreach (DataGridViewRow row in _grid.Rows.Cast<DataGridViewRow>().ToList())
        {
            string channel = row.Cells["Channel"].Value?.ToString() ?? "";
            if (!wanted.Contains(channel))
                _grid.Rows.Remove(row);
        }

        UpdateDashboard();
    }

    private void AddChannel()
    {
        string channel = ChannelTools.Normalize(_channelText.Text);

        if (channel.Length == 0)
        {
            MessageBox.Show("Enter a Twitch channel name or URL.");
            return;
        }

        if (_settings.Channels
            .Select(ChannelTools.Normalize)
            .Contains(channel, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("That channel is already in the list.");
            return;
        }

        _settings.Channels.Add(channel);
        _settings.ChannelPreferences[channel] = new ChannelPreference();
        _channelText.Clear();
        SaveSettingsAndApply();
    }

    private void RemoveSelected()
    {
        var selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Cells["Channel"].Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();

        if (selected.Count == 0)
            return;

        _settings.Channels = _settings.Channels
            .Where(c => !selected.Contains(
                ChannelTools.Normalize(c),
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (string channel in selected)
        {
            string normalized = ChannelTools.Normalize(channel);
            _settings.ChannelPreferences.Remove(normalized);
            _latestStatuses.Remove(normalized);
        }

        UpdateDashboard();
        SaveSettingsAndApply();
    }

    private void SaveSettingsFromUi()
    {
        _settings.Browser =
            (_browserCombo.SelectedItem?.ToString() ?? "Firefox")
                .Equals("Firefox", StringComparison.OrdinalIgnoreCase)
                ? "firefox"
                : "default";

        _settings.OpenStreamAutomatically = _autoOpenCheck.Checked;
        _settings.StartWithWindows = _startupCheck.Checked;
        _settings.OpenAlreadyLiveOnStartup = _openStartupLiveCheck.Checked;
        _settings.CheckEverySeconds = (int)_intervalNumeric.Value;

        SaveSettingsAndApply();

        MessageBox.Show(
            "Settings saved.",
            "StreakWatch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void SaveSettingsAndApply()
    {
        SettingsBackup.Create();
        SettingsStore.Save(_settings);

        if (_settings.StartWithWindows)
            StartupManager.Enable(Logger.Log);
        else
            StartupManager.Disable(Logger.Log);

        _watcher.ApplySettings(_settings);
        _discordNotifier.ApplySettings(_settings);
        RefreshGridFromSettings();
    }

    private void Watcher_StatusChanged(ChannelUiStatus status)
    {
        if (IsDisposed) return;

        BeginInvoke(() =>
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(
                        row.Cells["Channel"].Value?.ToString(),
                        status.Channel,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                row.Cells["Status"].Value = status.Status;
                row.Cells["Playback"].Value = status.PlaybackStatus;
                row.Cells["Confidence"].Value = status.DetectionConfidence;
                row.Cells["Safety"].Value = SafetyText(status.Channel);
                row.Cells["StreamId"].Value = status.StreamId ?? "";
                row.Cells["LastSeen"].Value =
                    status.LastSeenLocal?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                row.Cells["LastPlayback"].Value =
                    status.LastSuccessfulPlaybackLocal?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                break;
            }

            string previousPlayback = _previousPlayback.TryGetValue(status.Channel, out string? prev)
                ? prev
                : "";
            _previousPlayback[status.Channel] = status.PlaybackStatus;

            if (_settings.DiscordEnabled &&
                _settings.DiscordNotifyPlaybackStarted &&
                status.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase) &&
                !previousPlayback.Equals("Playing", StringComparison.OrdinalIgnoreCase))
            {
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.PlaybackStarted,
                    "▶️ **Playback started**",
                    $"{status.Channel} is playing in Firefox.");
            }

            _latestStatuses[status.Channel] = status;
            UpdateDashboard();
        });
    }

    private async Task TestSelectedChannelAsync()
    {
        string? channel = SelectedChannel();
        if (string.IsNullOrWhiteSpace(channel)) return;
        channel = ChannelTools.Normalize(channel);
        _statusLabel.Text = $"Testing {channel}...";
        EventHistory.Add("TEST", channel, "manual channel test started");
        await _watcher.CheckChannelAsync(channel);
        if (_latestStatuses.TryGetValue(channel, out ChannelUiStatus result))
        {
            MessageBox.Show(
                $"Channel: {channel}\nLive: {result.Status}\nPlayback: {result.PlaybackStatus}\nDetection: {result.DetectionConfidence}\nLast seen: {result.LastSeenLocal:yyyy-MM-dd HH:mm:ss}",
                "StreakWatch channel test",
                MessageBoxButtons.OK,
                result.Status.Equals("Error", StringComparison.OrdinalIgnoreCase) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    private void ToggleMonitoringPause()
    {
        bool paused = !_watcher.IsPaused;
        _watcher.SetPaused(paused);
        _pauseButton.Text = paused ? "Resume monitoring" : "Pause monitoring";
        _statusLabel.Text = paused
            ? "Monitoring PAUSED - existing Firefox tabs are left untouched"
            : $"Watching {_settings.Channels.Count} channel(s)";
        EventHistory.Add("MONITORING", "", paused ? "PAUSED" : "RESUMED");
    }

    private string SafetyText(string channel)
    {
        if (!_watcher.TryGetSafetyStatus(channel, out int elapsed, out int target, out bool reached))
            return "";
        if (reached) return $"OK {target / 60}m";
        return $"{elapsed / 60}:{elapsed % 60:00}/{target / 60}:00";
    }

    private static string RecoveryText(ChannelPreference pref)
    {
        if (pref.RecoveryDeadlineUtc is null) return "";
        DateTime deadline = pref.RecoveryDeadlineUtc.Value.ToUniversalTime();
        TimeSpan left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return "EXPIRED";
        return $"{deadline.ToLocalTime():yyyy-MM-dd HH:mm} ({(int)left.TotalHours}h {left.Minutes}m left)";
    }

    private string? SelectedChannel()
    {
        if (_grid.SelectedRows.Count > 0)
            return _grid.SelectedRows[0].Cells["Channel"].Value?.ToString();
        return _grid.CurrentRow?.Cells["Channel"].Value?.ToString();
    }

    private void StartRecoveryForSelected()
    {
        string? channel = SelectedChannel();
        if (string.IsNullOrWhiteSpace(channel)) return;
        channel = ChannelTools.Normalize(channel);
        var pref = ChannelPreferenceTools.Get(_settings, channel);
        pref.RecoveryDeadlineUtc = DateTime.UtcNow.AddHours(24);
        pref.AutoOpen = true;
        Logger.Log($"{channel} | RECOVERY | STARTED | deadline={pref.RecoveryDeadlineUtc:O}");
        SaveSettingsAndApply();
        RefreshGridFromSettings();
    }

    private void ClearRecoveryForSelected()
    {
        string? channel = SelectedChannel();
        if (string.IsNullOrWhiteSpace(channel)) return;
        channel = ChannelTools.Normalize(channel);
        ChannelPreferenceTools.Get(_settings, channel).RecoveryDeadlineUtc = null;
        Logger.Log($"{channel} | RECOVERY | CLEARED");
        SaveSettingsAndApply();
        RefreshGridFromSettings();
    }

    private void SaveChannelPolicyFromGrid(int rowIndex, int columnIndex)
    {
        if (_refreshingGrid || rowIndex < 0 || columnIndex < 0)
            return;

        string column = _grid.Columns[columnIndex].Name;
        if (column is not ("AutoOpen" or "CloseOffline" or "Mute"))
            return;

        var row = _grid.Rows[rowIndex];
        string channel = ChannelTools.Normalize(
            row.Cells["Channel"].Value?.ToString() ?? "");

        if (channel.Length == 0)
            return;

        ChannelPreference pref = ChannelPreferenceTools.Get(_settings, channel);

        if (column == "AutoOpen")
            pref.AutoOpen = Convert.ToBoolean(row.Cells["AutoOpen"].Value ?? true);
        else if (column == "CloseOffline")
            pref.CloseWhenOffline = Convert.ToBoolean(row.Cells["CloseOffline"].Value ?? true);
        else
            pref.Mute = Convert.ToBoolean(row.Cells["Mute"].Value ?? true);

        _settings.ChannelPreferences[channel] = pref;
        SaveSettingsAndApply();
    }

    private void UpdateDashboard()
    {
        int live = _latestStatuses.Values.Count(s =>
            s.Status.StartsWith("LIVE", StringComparison.OrdinalIgnoreCase));
        int offline = _latestStatuses.Values.Count(s =>
            s.Status.Equals("OFFLINE", StringComparison.OrdinalIgnoreCase));
        int playing = _latestStatuses.Values.Count(s =>
            s.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase));
        int recovering = _latestStatuses.Values.Count(s =>
            s.PlaybackStatus.Contains("Recover", StringComparison.OrdinalIgnoreCase) ||
            s.PlaybackStatus.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            s.PlaybackStatus.Contains("Stalled", StringComparison.OrdinalIgnoreCase) ||
            s.PlaybackStatus.Contains("Starting", StringComparison.OrdinalIgnoreCase));

        _dashboardLabel.Text =
            $"LIVE: {live} | Playing: {playing} | Recovering: {recovering} | Offline: {offline}";
    }

    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = $"StreakWatch-settings-{DateTime.Now:yyyyMMdd-HHmm}.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllText(
                dialog.FileName,
                JsonSerializer.Serialize(_settings, JsonOptions.Default));
            MessageBox.Show(
                "Settings exported. Discord webhook secret was NOT included.",
                "StreakWatch");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "StreakWatch");
        }
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            Settings? imported = JsonSerializer.Deserialize<Settings>(
                File.ReadAllText(dialog.FileName),
                JsonOptions.Default);

            if (imported is null)
                throw new InvalidDataException("Invalid settings file.");

            _settings = imported;
            SettingsStore.Save(_settings);
            LoadSettingsIntoUi();
            _watcher.ApplySettings(_settings);
            MessageBox.Show("Settings imported.", "StreakWatch");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "StreakWatch");
        }
    }

    private void CheckRecoveryImportanceNotifications()
    {
        if (!_settings.DiscordEnabled)
            return;

        DateTime now = DateTime.UtcNow;

        foreach (string channel in _settings.Channels
                     .Select(ChannelTools.Normalize)
                     .Where(c => c.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ChannelPreference pref = ChannelPreferenceTools.Get(_settings, channel);
            if (pref.RecoveryDeadlineUtc is not DateTime rawDeadline)
            {
                _recoveryAlertsSent.Remove(channel);
                continue;
            }

            DateTime deadline = rawDeadline.ToUniversalTime();
            TimeSpan left = deadline - now;

            if (!_recoveryAlertsSent.TryGetValue(channel, out HashSet<string>? sent))
            {
                sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _recoveryAlertsSent[channel] = sent;
            }

            void TrySendThreshold(string key, TimeSpan threshold, string label)
            {
                if (left > TimeSpan.Zero && left <= threshold && sent.Add(key))
                {
                    _ = _discordNotifier.NotifyEventAsync(
                        DiscordEventKeys.RecoveryImportant,
                        $"🚨 **IMPORTANT — Recovery deadline: {channel}**",
                        $"{label} remaining before the recovery deadline.\\nDeadline: {deadline.ToLocalTime():yyyy-MM-dd HH:mm:ss}\\nhttps://www.twitch.tv/{channel}");
                }
            }

            TrySendThreshold("6h", TimeSpan.FromHours(6), "6 hours or less");
            TrySendThreshold("1h", TimeSpan.FromHours(1), "1 hour or less");
            TrySendThreshold("15m", TimeSpan.FromMinutes(15), "15 minutes or less");

            if (left <= TimeSpan.Zero && sent.Add("expired"))
            {
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.RecoveryImportant,
                    $"⛔ **Recovery deadline expired: {channel}**",
                    $"Deadline was: {deadline.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if(string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl)) return;
        try { using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(8) }; string json=await http.GetStringAsync(_settings.UpdateManifestUrl); using var doc=JsonDocument.Parse(json); string? latest=doc.RootElement.TryGetProperty("version",out var v)?v.GetString():null; string? page=doc.RootElement.TryGetProperty("url",out var u)?u.GetString():null; if(!string.IsNullOrWhiteSpace(latest) && !latest.Equals("9.2.0",StringComparison.OrdinalIgnoreCase)) { var r=MessageBox.Show($"StreakWatch {latest} is available. Open release page?","Update available",MessageBoxButtons.YesNo); if(r==DialogResult.Yes && Uri.TryCreate(page,UriKind.Absolute,out var uri)) Process.Start(new ProcessStartInfo { FileName=uri.ToString(),UseShellExecute=true }); } } catch(Exception ex) { Logger.Log($"UPDATE CHECK | FAILED | {ex.GetType().Name}"); }
    }

    private void OpenHistoryWindow()
    {
        using var f=new Form { Text="History", Width=1000, Height=620, StartPosition=FormStartPosition.CenterParent };
        f.Controls.Add(new TextBox { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, WordWrap=false, Font=new Font("Consolas",9), Text=EventHistory.ReadRecent() }); f.ShowDialog(this);
    }
    private void OpenDiagnosticsWindow()
    {
        using var f = new Form
        {
            Text = "Diagnostics / Test tools",
            Width = 760,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent
        };

        var report = new TextBox
        {
            Left = 15,
            Top = 15,
            Width = 710,
            Height = 390,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9),
            Text = "Use Run diagnostics for a real status report. Simulation tools below only test UI/events."
        };

        var run = new Button { Text = "Run diagnostics", Left = 15, Top = 420, Width = 130 };
        run.Click += async (_, _) =>
        {
            await RunDiagnosticsAsync(false);
            try { report.Text = File.ReadAllText(AppPaths.DiagnosticsPath); } catch { }
        };

        var simLive = new Button { Text = "Simulate LIVE", Left = 160, Top = 420, Width = 120 };
        simLive.Click += (_, _) => SimulateSelected("LIVE");

        var simPlayback = new Button { Text = "Playback problem", Left = 295, Top = 420, Width = 135 };
        simPlayback.Click += (_, _) => SimulateSelected("PLAYBACK");

        var simNetwork = new Button { Text = "Connection lost", Left = 445, Top = 420, Width = 125 };
        simNetwork.Click += (_, _) => SimulateSelected("NETWORK");

        var clear = new Button { Text = "Clear simulation", Left = 585, Top = 420, Width = 130 };
        clear.Click += (_, _) => SimulateSelected("CLEAR");

        f.Controls.AddRange([report, run, simLive, simPlayback, simNetwork, clear]);
        f.ShowDialog(this);
    }

    private void SimulateSelected(string kind)
    {
        string? channel = SelectedChannel();
        if (kind != "NETWORK" && kind != "CLEAR" && string.IsNullOrWhiteSpace(channel))
        {
            MessageBox.Show("Select a channel in the main window first.", "Simulation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (kind == "NETWORK")
        {
            _statusLabel.Text = "[SIMULATION] Connection lost";
            EventHistory.Add("SIMULATION", "", "connection lost");
            MessageBox.Show("Simulation only: the real network state was not changed.", "Simulation");
            return;
        }
        if (kind == "CLEAR")
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string rowChannel = row.Cells["Channel"].Value?.ToString() ?? "";
                if (_latestStatuses.TryGetValue(rowChannel, out ChannelUiStatus actual))
                {
                    row.Cells["Status"].Value = actual.Status;
                    row.Cells["Playback"].Value = actual.PlaybackStatus;
                    row.Cells["Confidence"].Value = actual.DetectionConfidence;
                    row.Cells["StreamId"].Value = actual.StreamId ?? "";
                }
            }
            _statusLabel.Text = $"Watching {_settings.Channels.Count} channel(s)";
            EventHistory.Add("SIMULATION", "", "cleared");
            return;
        }

        channel = ChannelTools.Normalize(channel!);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!string.Equals(row.Cells["Channel"].Value?.ToString(), channel, StringComparison.OrdinalIgnoreCase))
                continue;
            row.Cells["Status"].Value = "LIVE [SIM]";
            row.Cells["Confidence"].Value = "Simulation";
            row.Cells["Playback"].Value = kind == "LIVE" ? "Playing [SIM]" : "Problem [SIM]";
            break;
        }
        EventHistory.Add("SIMULATION", channel, kind);
        _statusLabel.Text = $"[SIMULATION] {kind}: {channel}";
    }

    private async Task RunDiagnosticsAsync(bool showMessage = true)
    {
        var lines=new List<string>{"StreakWatch V9.3.5 Diagnostics",$"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",$"Firefox bridge state: {File.Exists(AppPaths.BrowserBridgePath)}",$"Native heartbeat: {File.Exists(AppPaths.NativeHeartbeatPath)}",$"Browser status: {File.Exists(AppPaths.BrowserStatusPath)}",$"Startup health report: {File.Exists(AppPaths.StartupHealthPath)}",$"Remote snapshot: {File.Exists(AppPaths.RemoteSnapshotPath)}",$"Discord rooms: {DiscordSecretStore.LoadWebhooks().Count}",$"Channels: {_settings.Channels.Count}",$"Safe Mode: {_settings.SafeMode}"};
        try { await _watcher.CheckAllAsync(true); lines.Add("Twitch check: OK"); } catch(Exception ex) { lines.Add("Twitch check: "+ex.Message); }
        foreach(var s in _latestStatuses.Values.OrderBy(x=>x.Channel)) lines.Add($"{s.Channel}: {s.Status} | {s.PlaybackStatus} | {s.DetectionConfidence}");
        string report=string.Join(Environment.NewLine,lines); File.WriteAllText(AppPaths.DiagnosticsPath,report); if(showMessage) MessageBox.Show(report,"Diagnostics");
    }
    private void OpenAdvancedSettings()
    {
        using var f=new Form { Text="Advanced / Safety / Updates", Width=640, Height=590, StartPosition=FormStartPosition.CenterParent };
        var safe=new CheckBox { Text="Safe Mode (block suspicious mass-LIVE opening)", Left=20, Top=20, Width=520, Checked=_settings.SafeMode };
        var n=new NumericUpDown { Left=270, Top=55, Width=80, Minimum=2, Maximum=20, Value=Math.Clamp(_settings.SafeModeBurstThreshold,2,20) }; var nl=new Label { Text="Burst threshold:",Left=20,Top=59,Width=220 };
        var alert=new CheckBox { Text="Emergency alert if LIVE but playback does not start",Left=20,Top=95,Width=520,Checked=_settings.LiveNotPlayingAlert };
        var secs=new NumericUpDown { Left=270,Top=130,Width=80,Minimum=20,Maximum=600,Value=Math.Clamp(_settings.LiveNotPlayingSeconds,20,600) }; var sl=new Label { Text="Emergency alert after seconds:",Left=20,Top=134,Width=240 };
        var safety=new NumericUpDown { Left=270,Top=170,Width=80,Minimum=30,Maximum=1800,Value=Math.Clamp(_settings.PlaybackSafetyTargetSeconds,30,1800) }; var safel=new Label { Text="Playback safety target (seconds):",Left=20,Top=174,Width=240 };
        var smart=new NumericUpDown { Left=270,Top=210,Width=80,Minimum=3,Maximum=15,Value=Math.Clamp(_settings.SmartLiveRecheckSeconds,3,15) }; var smartl=new Label { Text="Smart LIVE/problem recheck (seconds):",Left=20,Top=214,Width=245 };
        var heal=new NumericUpDown { Left=270,Top=250,Width=80,Minimum=10,Maximum=120,Value=Math.Clamp(_settings.BridgeSelfHealSeconds,10,120) }; var heall=new Label { Text="Bridge self-heal after (seconds):",Left=20,Top=254,Width=240 };
        var remote=new CheckBox { Text="Write remote-monitor snapshot (VPS detection handoff foundation)",Left=20,Top=290,Width=550,Checked=_settings.RemoteMonitorSnapshotEnabled };
        var upd=new CheckBox { Text="Enable update checker",Left=20,Top=330,Width=300,Checked=_settings.UpdateCheckEnabled };
        var url=new TextBox { Left=20,Top=365,Width=580,Text=_settings.UpdateManifestUrl??"",PlaceholderText="Update manifest URL (GitHub raw later)" };
        var backup=new Button { Text="Backup settings now",Left=20,Top=410,Width=160 }; backup.Click += (_,_)=>{SettingsBackup.Create();MessageBox.Show("Backup created.");};
        var save=new Button { Text="Save",Left=390,Top=485,Width=90 }; var cancel=new Button { Text="Cancel",Left=500,Top=485,Width=90,DialogResult=DialogResult.Cancel };
        save.Click += (_,_)=>{
            _settings.SafeMode=safe.Checked;
            _settings.SafeModeBurstThreshold=(int)n.Value;
            _settings.LiveNotPlayingAlert=alert.Checked;
            _settings.LiveNotPlayingSeconds=(int)secs.Value;
            _settings.PlaybackSafetyTargetSeconds=(int)safety.Value;
            _settings.SmartLiveRecheckSeconds=(int)smart.Value;
            _settings.BridgeSelfHealSeconds=(int)heal.Value;
            _settings.RemoteMonitorSnapshotEnabled=remote.Checked;
            _settings.UpdateCheckEnabled=upd.Checked;
            _settings.UpdateManifestUrl=url.Text.Trim();
            SaveSettingsAndApply(); f.Close();
        };
        f.Controls.AddRange([safe,n,nl,alert,secs,sl,safety,safel,smart,smartl,heal,heall,remote,upd,url,backup,save,cancel]);
        f.ShowDialog(this);
    }
    private void CheckLiveNotPlayingAlerts()
    {
        if(!_settings.LiveNotPlayingAlert) return;
        int sec=Math.Clamp(_settings.LiveNotPlayingSeconds,20,600);
        foreach(var s in _latestStatuses.Values)
        {
            if(!s.Status.StartsWith("LIVE",StringComparison.OrdinalIgnoreCase) || s.PlaybackStatus.Equals("Playing",StringComparison.OrdinalIgnoreCase) || !_watcher.ShouldAlertLiveNotPlaying(s.Channel,sec)) continue;
            string detail=$"No confirmed playback for {sec}+ seconds. Detection: {s.DetectionConfidence}. Status: {s.PlaybackStatus}";
            if(_settings.DiscordEnabled) _=_discordNotifier.NotifyEventAsync(DiscordEventKeys.PlaybackProblem,$"🚨 **EMERGENCY: {s.Channel} is LIVE but playback is not confirmed**",detail);
            EventHistory.Add("EMERGENCY-PLAYBACK",s.Channel,detail);
            _trayIcon.ShowBalloonTip(8000,"StreakWatch emergency",$"{s.Channel} is LIVE but playback is not confirmed.",ToolTipIcon.Warning);
        }
    }

    private void OpenDiscordSettings()
    {
        var webhooks = DiscordSecretStore.LoadWebhooks();

        using var form = new Form
        {
            Text = "Discord Notifications & Routing",
            Width = 980,
            Height = 650,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimumSize = new Size(820, 560)
        };

        var enabled = new CheckBox
        {
            Text = "Enable Discord notifications",
            Left = 20,
            Top = 18,
            Width = 250,
            Checked = _settings.DiscordEnabled
        };

        var roomsLabel = new Label
        {
            Text = "Discord destinations / rooms",
            Left = 20,
            Top = 55,
            AutoSize = true
        };

        var rooms = new ListBox
        {
            Left = 20,
            Top = 78,
            Width = 320,
            Height = 180,
            DisplayMember = nameof(DiscordWebhookSecret.Name)
        };

        void RefreshRoomList(string? selectId = null)
        {
            rooms.BeginUpdate();
            rooms.Items.Clear();
            foreach (var item in webhooks.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                rooms.Items.Add(item);
            rooms.EndUpdate();

            if (selectId is not null)
            {
                for (int i = 0; i < rooms.Items.Count; i++)
                {
                    if (rooms.Items[i] is DiscordWebhookSecret w &&
                        w.Id.Equals(selectId, StringComparison.OrdinalIgnoreCase))
                    {
                        rooms.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        bool EditWebhook(DiscordWebhookSecret target, bool isNew)
        {
            using var edit = new Form
            {
                Text = isNew ? "Add Discord destination" : "Edit Discord destination",
                Width = 620,
                Height = 250,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var nameLabel = new Label { Text = "Name / room label", Left = 20, Top = 20, Width = 160 };
            var nameBox = new TextBox { Left = 20, Top = 42, Width = 560, Text = target.Name };
            var urlLabel = new Label { Text = "Discord Webhook URL", Left = 20, Top = 78, Width = 180 };
            var urlBox = new TextBox
            {
                Left = 20,
                Top = 100,
                Width = 560,
                Text = target.WebhookUrl,
                UseSystemPasswordChar = true
            };
            var showUrl = new CheckBox { Text = "Show URL", Left = 20, Top = 132, Width = 100 };
            showUrl.CheckedChanged += (_, _) => urlBox.UseSystemPasswordChar = !showUrl.Checked;

            var saveButton = new Button { Text = "Save", Left = 380, Top = 155, Width = 95 };
            var cancelButton = new Button
            {
                Text = "Cancel",
                Left = 485,
                Top = 155,
                Width = 95,
                DialogResult = DialogResult.Cancel
            };

            saveButton.Click += (_, _) =>
            {
                string name = nameBox.Text.Trim();
                string url = urlBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show("Enter a destination name.", "StreakWatch");
                    return;
                }

                if (!DiscordNotifier.IsValidWebhook(url))
                {
                    MessageBox.Show("Enter a valid Discord webhook URL.", "StreakWatch");
                    return;
                }

                target.Name = name;
                target.WebhookUrl = url;
                edit.DialogResult = DialogResult.OK;
                edit.Close();
            };

            edit.Controls.AddRange([
                nameLabel, nameBox, urlLabel, urlBox, showUrl, saveButton, cancelButton
            ]);
            edit.AcceptButton = saveButton;
            edit.CancelButton = cancelButton;

            return edit.ShowDialog(form) == DialogResult.OK;
        }

        var addRoom = new Button { Text = "Add", Left = 20, Top = 268, Width = 70 };
        var editRoom = new Button { Text = "Edit", Left = 98, Top = 268, Width = 70 };
        var removeRoom = new Button { Text = "Remove", Left = 176, Top = 268, Width = 78 };
        var testRoom = new Button { Text = "Test", Left = 262, Top = 268, Width = 78 };

        var routeLabel = new Label
        {
            Text = "Choose exactly where each notification goes. Check multiple rooms if you want the same alert sent to more than one room.",
            Left = 370,
            Top = 55,
            Width = 560,
            Height = 42
        };

        var routeGrid = new DataGridView
        {
            Left = 370,
            Top = 98,
            Width = 560,
            Height = 370,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };

        void RebuildRouteGrid()
        {
            var selected = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (DataGridViewRow row in routeGrid.Rows)
            {
                string? key = row.Tag as string;
                if (key is null) continue;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DiscordWebhookSecret webhook in webhooks)
                {
                    if (!routeGrid.Columns.Contains(webhook.Id))
                        continue;
                    if (Convert.ToBoolean(row.Cells[webhook.Id].Value ?? false))
                        set.Add(webhook.Id);
                }

                selected[key] = set;
            }

            routeGrid.Columns.Clear();
            routeGrid.Rows.Clear();

            routeGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Event",
                HeaderText = "Notification",
                ReadOnly = true,
                FillWeight = 170
            });

            foreach (DiscordWebhookSecret webhook in webhooks)
            {
                routeGrid.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    Name = webhook.Id,
                    HeaderText = webhook.Name,
                    FillWeight = 90
                });
            }

            foreach (var (key, label) in DiscordEventKeys.All)
            {
                int rowIndex = routeGrid.Rows.Add();
                DataGridViewRow row = routeGrid.Rows[rowIndex];
                row.Tag = key;
                row.Cells["Event"].Value = label;

                HashSet<string> routeIds;
                if (selected.TryGetValue(key, out HashSet<string>? current))
                {
                    routeIds = current;
                }
                else if (_settings.DiscordRoutes is not null &&
                         _settings.DiscordRoutes.TryGetValue(key, out List<string>? saved))
                {
                    routeIds = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
                }
                else if (webhooks.Count == 1)
                {
                    routeIds = new HashSet<string>(
                        [webhooks[0].Id],
                        StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (DiscordWebhookSecret webhook in webhooks)
                    row.Cells[webhook.Id].Value = routeIds.Contains(webhook.Id);
            }
        }

        addRoom.Click += (_, _) =>
        {
            var item = new DiscordWebhookSecret
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"Room {webhooks.Count + 1}"
            };

            if (!EditWebhook(item, true))
                return;

            webhooks.Add(item);
            RefreshRoomList(item.Id);
            RebuildRouteGrid();
        };

        editRoom.Click += (_, _) =>
        {
            if (rooms.SelectedItem is not DiscordWebhookSecret item)
                return;

            if (!EditWebhook(item, false))
                return;

            RefreshRoomList(item.Id);
            RebuildRouteGrid();
        };

        removeRoom.Click += (_, _) =>
        {
            if (rooms.SelectedItem is not DiscordWebhookSecret item)
                return;

            if (MessageBox.Show(
                    $"Remove '{item.Name}' from StreakWatch?",
                    "StreakWatch",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            webhooks.RemoveAll(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            RefreshRoomList();
            RebuildRouteGrid();
        };

        testRoom.Click += async (_, _) =>
        {
            if (rooms.SelectedItem is not DiscordWebhookSecret item)
            {
                MessageBox.Show("Select a Discord destination first.", "StreakWatch");
                return;
            }

            testRoom.Enabled = false;
            try
            {
                bool ok = await DiscordNotifier.SendTestAsync(item.WebhookUrl, item.Name);
                MessageBox.Show(
                    ok ? $"Test sent to '{item.Name}'." : "Discord test failed. Check the webhook or internet connection.",
                    "StreakWatch");
            }
            finally
            {
                testRoom.Enabled = true;
            }
        };

        var note = new Label
        {
            Left = 20,
            Top = 320,
            Width = 320,
            Height = 150,
            Text =
                "Each destination has its own Webhook URL.\r\n\r\n" +
                "Webhook URLs are stored separately in %AppData%\\StreakWatch and are not included in Export settings or logs.\r\n\r\n" +
                "If a Discord send fails while the internet is down, it is queued for retry."
        };

        var save = new Button { Text = "Save", Left = 720, Top = 505, Width = 100 };
        var cancel = new Button
        {
            Text = "Cancel",
            Left = 830,
            Top = 505,
            Width = 100,
            DialogResult = DialogResult.Cancel
        };

        save.Click += (_, _) =>
        {
            _settings.DiscordEnabled = enabled.Checked;
            _settings.DiscordRoutes ??=
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _settings.DiscordRoutes.Clear();

            foreach (DataGridViewRow row in routeGrid.Rows)
            {
                string? key = row.Tag as string;
                if (key is null) continue;

                var ids = new List<string>();
                foreach (DiscordWebhookSecret webhook in webhooks)
                {
                    if (routeGrid.Columns.Contains(webhook.Id) &&
                        Convert.ToBoolean(row.Cells[webhook.Id].Value ?? false))
                    {
                        ids.Add(webhook.Id);
                    }
                }

                _settings.DiscordRoutes[key] = ids;
            }

            // Keep legacy toggles aligned with whether an event has at least one route.
            bool HasRoute(string key) =>
                _settings.DiscordRoutes.TryGetValue(key, out List<string>? ids) &&
                ids.Count > 0;

            _settings.DiscordNotifyLive = HasRoute(DiscordEventKeys.Live);
            _settings.DiscordNotifyOffline = HasRoute(DiscordEventKeys.Offline);
            _settings.DiscordNotifyPlaybackStarted = HasRoute(DiscordEventKeys.PlaybackStarted);
            _settings.DiscordNotifyPlaybackProblem = HasRoute(DiscordEventKeys.PlaybackProblem);
            _settings.DiscordNotifyNetworkLost = HasRoute(DiscordEventKeys.NetworkLost);
            _settings.DiscordNotifyNetworkRestored = HasRoute(DiscordEventKeys.NetworkRestored);
            _settings.DiscordNotifyRecoveryImportant = HasRoute(DiscordEventKeys.RecoveryImportant);

            DiscordSecretStore.SaveWebhooks(webhooks);
            SaveSettingsAndApply();

            form.DialogResult = DialogResult.OK;
            form.Close();
        };

        form.Controls.AddRange([
            enabled, roomsLabel, rooms, addRoom, editRoom, removeRoom, testRoom,
            routeLabel, routeGrid, note, save, cancel
        ]);

        RefreshRoomList();
        RebuildRouteGrid();

        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.ShowDialog(this);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isExiting)
            return;

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();

            _trayIcon.ShowBalloonTip(
                2500,
                "StreakWatch is still running",
                "It will keep watching channels in the background.",
                ToolTipIcon.Info);
            return;
        }

        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            // Windows may terminate the process quickly, so this is best-effort.
            try
            {
                _ = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.AppExit,
                    "⛔ **StreakWatch stopping**",
                    $"Reason: {e.CloseReason}. Monitoring on this PC is stopping.");
            }
            catch { }
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RestartApplication()
    {
        try
        {
            EventHistory.Add("APP-RESTART", "", "Manual restart requested");
            Logger.Log("APP RESTART | Manual restart requested");

            string exe = Application.ExecutablePath;
            int currentPid = Environment.ProcessId;

            // Start the replacement process immediately. It enters --restart-wait
            // mode and waits for this PID to release the mutex before continuing.
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--restart-wait {currentPid}",
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            _recoveryNotifyTimer.Stop();
            _livePlaybackAlertTimer.Stop();
            _bridgeStatusTimer.Stop();
            _watcher.Dispose();
            _discordNotifier.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            FormClosing -= MainForm_FormClosing;
            Close();
            Application.Exit();
        }
        catch (Exception ex)
        {
            Logger.Log($"APP RESTART | FAILED | {ex.Message}");
            MessageBox.Show(
                $"Could not restart StreakWatch.\n\n{ex.Message}",
                "StreakWatch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool _isExiting;

    private async Task ExitApplicationAsync(string reason)
    {
        if (_isExiting)
            return;

        _isExiting = true;
        EventHistory.Add("APP-EXIT", "", reason);
        Logger.Log($"APP EXIT | {reason}");

        // Best effort: send the routed Discord shutdown message before disposing
        // the notifier. If internet is unavailable, DiscordNotifier persists it
        // to its existing retry queue on disk.
        if (_settings.DiscordEnabled)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                Task send = _discordNotifier.NotifyEventAsync(
                    DiscordEventKeys.AppExit,
                    "⛔ **StreakWatch stopped**",
                    $"Reason: {reason}\nMonitoring is no longer running on this PC.");

                await Task.WhenAny(send, Task.Delay(6000, timeout.Token));
            }
            catch (Exception ex)
            {
                Logger.Log($"APP EXIT | DISCORD NOTICE FAILED | {ex.GetType().Name}");
            }
        }

        _recoveryNotifyTimer.Stop();
        _recoveryNotifyTimer.Dispose();
        _livePlaybackAlertTimer.Stop();
        _livePlaybackAlertTimer.Dispose();
        _bridgeStatusTimer.Stop();
        _bridgeStatusTimer.Dispose();
        _watcher.Dispose();
        _discordNotifier.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        FormClosing -= MainForm_FormClosing;
        Close();
        Application.Exit();
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, "");

            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}

internal sealed class TwitchWatcher : IDisposable
{
    private Settings _settings;
    private readonly TwitchStatusClient _client = new();
    private readonly ConcurrentDictionary<string, ChannelRuntime> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _parallelGate;
    private readonly System.Windows.Forms.Timer _checkTimer;
    private readonly System.Windows.Forms.Timer _networkTimer;
    private readonly System.Windows.Forms.Timer _heartbeatTimer;
    private readonly System.Windows.Forms.Timer _browserWatchdogTimer;
    private readonly System.Windows.Forms.Timer _browserStatusTimer;
    private readonly Dictionary<string, DateTime> _problemNotifiedUtc =
        new(StringComparer.OrdinalIgnoreCase);

    private int _checking;
    private bool? _networkHealthy;
    private int _networkRecoveryGeneration;
    private bool _firstRun = true;
    private bool _paused;
    public bool IsPaused => _paused;

    public event Action<ChannelUiStatus>? StatusChanged;
    public event Action<string>? GeneralStatusChanged;
    public event Action<string>? PlaybackProblem;
    public event Action<string, string?>? LiveStarted;
    public event Action<string>? StreamOffline;
    public event Action<bool>? NetworkChanged;

    public TwitchWatcher(Settings settings)
    {
        _settings = CloneSettings(settings);
        _parallelGate = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrentChecks, 1, 8));

        LoadRuntimeState();
        ApplySettingsInternal(_settings, initialize: true);

        // Clear any stale browser-bridge LIVE list from an older/crashed version
        // before Firefox or the browser watchdog can act on it.
        SaveBrowserBridge();
        Logger.Log("STARTUP | STALE LIVE STATE INVALIDATED | fresh Twitch check required");

        _checkTimer = new System.Windows.Forms.Timer();
        _checkTimer.Tick += async (_, _) => await CheckAllAsync(false);

        _networkTimer = new System.Windows.Forms.Timer();
        _networkTimer.Tick += async (_, _) => await NetworkRecoveryTickAsync();

        _heartbeatTimer = new System.Windows.Forms.Timer();
        _heartbeatTimer.Tick += (_, _) =>
        {
            Logger.Log($"HEARTBEAT | healthy | channels={_channels.Count} | network={FormatNetworkState()}");
            SaveState();
        };

        _browserWatchdogTimer = new System.Windows.Forms.Timer
        {
            Interval = 8000
        };
        _browserWatchdogTimer.Tick += async (_, _) => await BrowserWatchdogTickAsync();

        _browserStatusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000
        };
        _browserStatusTimer.Tick += (_, _) => BrowserStatusTick();

        ApplyTimerIntervals();
        _checkTimer.Start();
        _networkTimer.Start();
        _heartbeatTimer.Start();
        _browserWatchdogTimer.Start();
        _browserStatusTimer.Start();

        Logger.Log($"STARTED | V9.3.5 | channels={_channels.Count}");
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _checkTimer.Stop();
            _browserWatchdogTimer.Stop();
            GeneralStatusChanged?.Invoke("Monitoring PAUSED");
            Logger.Log("MONITORING | PAUSED");
        }
        else
        {
            _checkTimer.Start();
            _browserWatchdogTimer.Start();
            GeneralStatusChanged?.Invoke($"Watching {_channels.Count} channel(s)");
            Logger.Log("MONITORING | RESUMED");
            _ = CheckAllAsync(true);
        }
    }

    public async Task CheckChannelAsync(string channel)
    {
        channel = ChannelTools.Normalize(channel);
        if (!_channels.TryGetValue(channel, out ChannelRuntime? runtime))
        {
            GeneralStatusChanged?.Invoke($"{channel}: channel is not in the watch list");
            return;
        }
        Logger.Log($"{channel} | MANUAL TEST | START");
        await CheckOneAsync(runtime, true);
        SaveState();
        SaveBrowserBridge();
        Logger.Log($"{channel} | MANUAL TEST | COMPLETE");
    }

    public void ApplySettings(Settings settings)
    {
        _settings = CloneSettings(settings);
        _settings.ChannelPreferences ??= new Dictionary<string, ChannelPreference>(
            StringComparer.OrdinalIgnoreCase);
        ApplySettingsInternal(_settings, initialize: false);
        ApplyTimerIntervals();

        Logger.Log($"SETTINGS | APPLIED | channels={_channels.Count}");
        SaveBrowserBridge();
    }

    private void ApplySettingsInternal(Settings settings, bool initialize)
    {
        var wanted = settings.Channels
            .Select(ChannelTools.Normalize)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string existing in _channels.Keys.ToList())
        {
            if (!wanted.Contains(existing))
            {
                _channels.TryRemove(existing, out _);
                Logger.Log($"{existing} | SETTINGS | REMOVED");
            }
        }

        foreach (string channel in wanted)
        {
            if (_channels.ContainsKey(channel))
                continue;

            _channels[channel] = new ChannelRuntime
            {
                Channel = channel,
                LastKnownLive = null
            };

            if (!initialize)
                Logger.Log($"{channel} | SETTINGS | ADDED");
        }
    }

    private void ApplyTimerIntervals()
    {
        _checkTimer.Interval = 10 * 1000;
        _networkTimer.Interval = Math.Clamp(_settings.NetworkRetrySeconds, 3, 60) * 1000;
        _heartbeatTimer.Interval = Math.Clamp(_settings.HeartbeatMinutes, 5, 1440) * 60 * 1000;
    }

    public async Task CheckAllAsync(bool force)
    {
        if (_paused && !force)
            return;
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        GeneralStatusChanged?.Invoke("Checking Twitch...");

        try
        {
            var tasks = _channels.Values
                .Select(c => CheckOneAsync(c, force))
                .ToArray();

            await Task.WhenAll(tasks);
            SaveState();
            SaveBrowserBridge();
            SaveRemoteMonitorSnapshot();

            GeneralStatusChanged?.Invoke(
                _networkHealthy == false
                    ? "Connection issue - channel states preserved"
                    : $"Watching {_channels.Count} channel(s)");
        }
        finally
        {
            _firstRun = false;
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task CheckOneAsync(ChannelRuntime runtime, bool force)
    {
        if (!force)
        {
            int wanted = Math.Clamp(_settings.CheckEverySeconds, 10, 3600);
            if (runtime.LastKnownLive == true || runtime.OfflineCandidateSinceUtc is not null || runtime.ConsecutiveFailures > 0)
                wanted = Math.Min(wanted, Math.Clamp(_settings.SmartLiveRecheckSeconds, 3, 15));
            if(runtime.LastPriorityCheckUtc is DateTime lp && (DateTime.UtcNow-lp).TotalSeconds < wanted) return;
            if(runtime.NextAllowedCheckUtc is DateTime next && DateTime.UtcNow < next) return;
        }

        await _parallelGate.WaitAsync();

        try
        {
            ChannelStatus result;

            try
            {
                result = await _client.GetStatusAsync(runtime.Channel);
                HandleNetworkSuccess();
                ResetBackoff(runtime);
            }
            catch (TwitchRequestException ex)
            {
                HandleRequestFailure(runtime, ex);
                PublishError(runtime, ex.Kind.ToString());
                return;
            }
            catch (Exception ex)
            {
                HandleRequestFailure(
                    runtime,
                    new TwitchRequestException(
                        TwitchErrorKind.Unknown,
                        ex.Message));

                PublishError(runtime, "Error");
                return;
            }

            if (!result.Exists)
            {
                Logger.Log($"{runtime.Channel} | CHANNEL NOT FOUND");
                StatusChanged?.Invoke(new ChannelUiStatus(
                    runtime.Channel,
                    "Not found",
                    runtime.PlaybackStatus,
                    null,
                    DateTime.Now,
                    runtime.LastSuccessfulPlaybackUtc?.ToLocalTime()));
                return;
            }

            runtime.FreshStatusObservedUtc = DateTime.UtcNow;
            runtime.DetectionConfidence = result.Signal;
            runtime.LastPriorityCheckUtc = DateTime.UtcNow;
            ProcessStatus(runtime, result);
        }
        finally
        {
            _parallelGate.Release();
        }
    }

    private void ProcessStatus(ChannelRuntime runtime, ChannelStatus result)
    {
        bool? wasLive = runtime.LastKnownLive;
        string? previousStreamId = runtime.LastStreamId;

        runtime.LastSeenUtc = DateTime.UtcNow;

        // V8.1 fix:
        // A persisted state can say the channel was already LIVE before this app restart.
        // On the first check after startup, honor "OpenAlreadyLiveOnStartup" by comparing
        // the current Twitch Stream ID with LastOpenedStreamId.
        //
        // This opens an already-live stream once when StreakWatch starts, but it will not
        // reopen the same broadcast every polling cycle or after another restart if that
        // exact Stream ID was already opened.
        if (_firstRun &&
            _settings.OpenAlreadyLiveOnStartup &&
            result.IsLive &&
            wasLive is not null)
        {
            bool alreadyOpenedCurrentStream =
                result.StreamId is not null &&
                string.Equals(
                    runtime.LastOpenedStreamId,
                    result.StreamId,
                    StringComparison.Ordinal);

            if (!alreadyOpenedCurrentStream)
            {
                Logger.Log(
                    $"{runtime.Channel} | STARTUP LIVE | stream={result.StreamId ?? "unknown"} | opening=true");

                _ = OnNewLiveAsync(runtime, result.StreamId);
            }
            else
            {
                Logger.Log(
                    $"{runtime.Channel} | STARTUP LIVE | stream={result.StreamId ?? "unknown"} | already-opened=true");
            }
        }

        if (wasLive is null)
        {
            runtime.LastKnownLive = result.IsLive;
            runtime.LastStreamId = result.StreamId;

            if (result.IsLive)
            {
                runtime.LiveObservedSinceUtc ??= DateTime.UtcNow;
                runtime.LiveDetectedUtc ??= DateTime.UtcNow;
                runtime.LiveNotPlayingAlertSent = false;
                runtime.PlaybackConfirmedSinceUtc = null;
                runtime.SafetyTargetReached = false;

                Logger.Log(
                    $"{runtime.Channel} | INITIAL | LIVE | stream={result.StreamId ?? "unknown"}");

                bool alreadyOpened =
                    result.StreamId is not null &&
                    string.Equals(
                        runtime.LastOpenedStreamId,
                        result.StreamId,
                        StringComparison.Ordinal);

                if (_settings.OpenAlreadyLiveOnStartup &&
                    !alreadyOpened)
                {
                    _ = OnNewLiveAsync(runtime, result.StreamId);
                }
            }
            else
            {
                runtime.LiveObservedSinceUtc = null;
                Logger.Log($"{runtime.Channel} | INITIAL | OFFLINE");
            }

            PublishStatus(runtime, result.IsLive ? "LIVE" : "OFFLINE");
            return;
        }

        if (result.IsLive)
        {
            if (runtime.OfflineCandidateSinceUtc is not null)
            {
                Logger.Log($"{runtime.Channel} | OFFLINE CANDIDATE CLEARED | stream still LIVE");
                runtime.OfflineCandidateSinceUtc = null;
            }

            bool transitionedToLive = wasLive == false;

            bool recoveredStreamId =
                result.StreamId is not null &&
                previousStreamId is null;

            bool streamIdChanged =
                result.StreamId is not null &&
                previousStreamId is not null &&
                !string.Equals(
                    result.StreamId,
                    previousStreamId,
                    StringComparison.Ordinal);

            runtime.LastKnownLive = true;
            runtime.LastStreamId = result.StreamId;

            if (recoveredStreamId)
                Logger.Log($"{runtime.Channel} | STREAM ID RECOVERED | stream={result.StreamId}");

            if (transitionedToLive || streamIdChanged)
            {
                runtime.LiveObservedSinceUtc = DateTime.UtcNow;
                runtime.LiveDetectedUtc = DateTime.UtcNow;
                runtime.LiveNotPlayingAlertSent = false;
                runtime.PlaybackConfirmedSinceUtc = null;
                runtime.SafetyTargetReached = false;

                string reason =
                    streamIdChanged && !transitionedToLive
                        ? "NEW STREAM ID"
                        : "OFFLINE -> LIVE";

                Logger.Log(
                    $"{runtime.Channel} | {reason} | stream={result.StreamId ?? "unknown"}");
                LiveStarted?.Invoke(runtime.Channel, result.StreamId);

                bool alreadyOpened =
                    result.StreamId is not null &&
                    string.Equals(
                        runtime.LastOpenedStreamId,
                        result.StreamId,
                        StringComparison.Ordinal);

                if (!alreadyOpened)
                    _ = OnNewLiveAsync(runtime, result.StreamId);
            }

            PublishStatus(runtime, "LIVE");
        }
        else
        {
            if (wasLive == true)
            {
                int graceSeconds = Math.Clamp(_settings.OfflineGraceSeconds, 0, 300);

                if (graceSeconds > 0)
                {
                    runtime.OfflineCandidateSinceUtc ??= DateTime.UtcNow;
                    int elapsed = (int)(DateTime.UtcNow - runtime.OfflineCandidateSinceUtc.Value).TotalSeconds;

                    if (elapsed < graceSeconds)
                    {
                        Logger.Log(
                            $"{runtime.Channel} | OFFLINE CANDIDATE | {elapsed}s/{graceSeconds}s");
                        PublishStatus(runtime, $"LIVE (offline? {elapsed}s/{graceSeconds}s)");
                        return;
                    }
                }

                Logger.Log(
                    $"{runtime.Channel} | LIVE -> OFFLINE | grace-confirmed | observed-duration={FormatObservedDuration(runtime.LiveObservedSinceUtc)}");
                StreamOffline?.Invoke(runtime.Channel);
            }

            runtime.LastKnownLive = false;
            runtime.LastStreamId = null;
            runtime.LiveObservedSinceUtc = null;
            runtime.LiveDetectedUtc = null;
            runtime.LiveNotPlayingAlertSent = false;
            runtime.PlaybackConfirmedSinceUtc = null;
            runtime.SafetyTargetReached = false;
            runtime.OfflineCandidateSinceUtc = null;
            runtime.PlaybackStatus = "Offline";

            PublishStatus(runtime, "OFFLINE");
        }
    }

    private async Task OnNewLiveAsync(ChannelRuntime runtime, string? streamId)
    {
        if (!_settings.OpenStreamAutomatically)
            return;

        ChannelPreference preference = ChannelPreferenceTools.Get(_settings, runtime.Channel);
        if (!preference.AutoOpen)
        {
            Logger.Log($"{runtime.Channel} | AUTO OPEN SKIPPED | per-channel disabled");
            return;
        }

        if (streamId is not null)
        {
            if (string.Equals(
                    runtime.LastOpenedStreamId,
                    streamId,
                    StringComparison.Ordinal))
                return;

            runtime.LastOpenedStreamId = streamId;
            SaveState();
        }

        string url =
            $"https://www.twitch.tv/{Uri.EscapeDataString(runtime.Channel)}";

        // Firefox tab management belongs to the Firefox bridge.
        // Avoid racing the extension when several channels become LIVE together.
        if (_settings.Browser.Equals("firefox", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(
                $"{runtime.Channel} | FIREFOX BRIDGE QUEUED | stream={streamId ?? "unknown"}");
            return;
        }

        bool requested = TryOpenBrowser(url, runtime.Channel, 1);

        await Task.Delay(
            TimeSpan.FromSeconds(
                Math.Clamp(_settings.BrowserCheckDelaySeconds, 1, 30)));

        if (BrowserDetected())
        {
            Logger.Log(
                $"{runtime.Channel} | BROWSER PROCESS DETECTED | playback NOT verified");
            return;
        }

        int retrySeconds =
            Math.Clamp(_settings.BrowserRetrySeconds, 3, 60);

        Logger.Log(
            $"{runtime.Channel} | BROWSER NOT DETECTED | retrying in {retrySeconds}s");

        await Task.Delay(TimeSpan.FromSeconds(retrySeconds));

        requested = TryOpenBrowser(url, runtime.Channel, 2);

        await Task.Delay(
            TimeSpan.FromSeconds(
                Math.Clamp(_settings.BrowserCheckDelaySeconds, 1, 30)));

        if (requested && BrowserDetected())
        {
            Logger.Log(
                $"{runtime.Channel} | BROWSER PROCESS DETECTED after retry | playback NOT verified");
        }
        else
        {
            Logger.Log(
                $"{runtime.Channel} | WARNING | Browser not detected after retry");
        }
    }

    private bool TryOpenBrowser(string url, string channel, int attempt)
    {
        try
        {
            string fileName =
                _settings.Browser.Equals(
                    "firefox",
                    StringComparison.OrdinalIgnoreCase)
                    ? "firefox.exe"
                    : url;

            var psi =
                _settings.Browser.Equals(
                    "firefox",
                    StringComparison.OrdinalIgnoreCase)
                    ? new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = $"\"{url}\"",
                        UseShellExecute = true
                    }
                    : new ProcessStartInfo(url)
                    {
                        UseShellExecute = true
                    };

            Process.Start(psi);

            Logger.Log(
                $"{channel} | BROWSER OPEN REQUEST | browser={_settings.Browser} | attempt={attempt}");

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"{channel} | BROWSER OPEN FAILED | attempt={attempt} | {ex.Message}");

            return false;
        }
    }

    private bool BrowserDetected()
    {
        if (_settings.Browser.Equals(
                "firefox",
                StringComparison.OrdinalIgnoreCase))
        {
            return Process.GetProcessesByName("firefox").Length > 0;
        }

        // For default browser we cannot reliably know which process Windows chose.
        return true;
    }

    private void HandleRequestFailure(
        ChannelRuntime runtime,
        TwitchRequestException ex)
    {
        runtime.ConsecutiveFailures++;

        int backoffSeconds =
            Math.Min(
                60,
                5 * (int)Math.Pow(
                    2,
                    Math.Min(runtime.ConsecutiveFailures - 1, 4)));

        runtime.NextAllowedCheckUtc =
            DateTime.UtcNow.AddSeconds(backoffSeconds);

        string label = ex.Kind switch
        {
            TwitchErrorKind.NetworkOffline => "NETWORK OFFLINE",
            TwitchErrorKind.NetworkTimeout => "NETWORK TIMEOUT",
            TwitchErrorKind.RateLimited => "TWITCH RATE LIMITED",
            TwitchErrorKind.ApiError => "TWITCH API ERROR",
            TwitchErrorKind.GraphQlError => "TWITCH GRAPHQL ERROR",
            _ => "UNKNOWN ERROR"
        };

        Logger.Log(
            $"{runtime.Channel} | {label} | backoff={backoffSeconds}s | {ex.Message}");

        if (ex.Kind is TwitchErrorKind.NetworkOffline
            or TwitchErrorKind.NetworkTimeout)
        {
            bool firstLoss = _networkHealthy != false;
            _networkHealthy = false;
            if (firstLoss)
                NetworkChanged?.Invoke(false);
        }
    }

    private void ResetBackoff(ChannelRuntime runtime)
    {
        runtime.ConsecutiveFailures = 0;
        runtime.NextAllowedCheckUtc = null;
    }

    private void HandleNetworkSuccess()
    {
        bool wasDown = _networkHealthy == false;

        if (wasDown)
        {
            _networkRecoveryGeneration++;
            Logger.Log(
                $"NETWORK | RESTORED | generation={_networkRecoveryGeneration} | browser recovery queued");
            NetworkChanged?.Invoke(true);
        }

        _networkHealthy = true;

        if (wasDown)
            SaveBrowserBridge();
    }

    private async Task NetworkRecoveryTickAsync()
    {
        if (_networkHealthy != false ||
            Volatile.Read(ref _checking) == 1)
            return;

        if (!NetworkInterface.GetIsNetworkAvailable())
            return;

        try
        {
            if (!await _client.ProbeTwitchAsync())
                return;

            _networkHealthy = true;
            _networkRecoveryGeneration++;
            Logger.Log(
                $"NETWORK | RESTORED | immediate full check | generation={_networkRecoveryGeneration} | browser recovery queued");
            NetworkChanged?.Invoke(true);

            // Tell Firefox immediately so tabs stuck on its "no internet" page
            // can reload without waiting for all Twitch status checks to finish.
            SaveBrowserBridge();

            foreach (var runtime in _channels.Values)
            {
                runtime.NextAllowedCheckUtc = null;
                runtime.LastPriorityCheckUtc = null;
            }

            await CheckAllAsync(true);
            await Task.Delay(1500);
            await CheckAllAsync(true);
            SaveRemoteMonitorSnapshot();
        }
        catch
        {
        }
    }

    private void PublishStatus(ChannelRuntime runtime, string status)
    {
        StatusChanged?.Invoke(
            new ChannelUiStatus(
                runtime.Channel,
                status,
                runtime.PlaybackStatus,
                runtime.LastStreamId,
                runtime.LastSeenUtc?.ToLocalTime(),
                runtime.LastSuccessfulPlaybackUtc?.ToLocalTime(),
                runtime.DetectionConfidence));
    }

    private void PublishError(ChannelRuntime runtime, string status)
    {
        StatusChanged?.Invoke(
            new ChannelUiStatus(
                runtime.Channel,
                status,
                runtime.PlaybackStatus,
                runtime.LastStreamId,
                DateTime.Now,
                runtime.LastSuccessfulPlaybackUtc?.ToLocalTime(),
                runtime.DetectionConfidence));
    }

    private void LoadRuntimeState()
    {
        var state = StateStore.Load();

        foreach (var pair in state.Channels)
        {
            // Persisted LIVE/OFFLINE is historical only. Never trust it to drive
            // Firefox after an app restart because a previous buggy/aborted session
            // may have left false LIVE values in state.json.
            _channels[pair.Key] = new ChannelRuntime
            {
                Channel = pair.Key,
                LastKnownLive = null,
                LastStreamId = null,
                LastOpenedStreamId = pair.Value.LastOpenedStreamId,
                LastSeenUtc = pair.Value.LastSeenUtc,
                LiveObservedSinceUtc = null,
                LastSuccessfulPlaybackUtc = pair.Value.LastSuccessfulPlaybackUtc,
                PlaybackStatus = "Unknown"
            };
        }
    }

    private void SaveState()
    {
        var state = new AppState();

        foreach (var runtime in _channels.Values)
        {
            state.Channels[runtime.Channel] =
                new PersistedChannelState
                {
                    LastKnownLive = runtime.LastKnownLive,
                    LastStreamId = runtime.LastStreamId,
                    LastOpenedStreamId = runtime.LastOpenedStreamId,
                    LastSeenUtc = runtime.LastSeenUtc,
                    LiveObservedSinceUtc = runtime.LiveObservedSinceUtc,
                    LastSuccessfulPlaybackUtc = runtime.LastSuccessfulPlaybackUtc
                };
        }

        StateStore.Save(state);
    }


    private void BrowserStatusTick()
    {
        try
        {
            if (!File.Exists(AppPaths.BrowserStatusPath))
                return;

            BrowserReportEnvelope? envelope =
                JsonSerializer.Deserialize<BrowserReportEnvelope>(
                    File.ReadAllText(AppPaths.BrowserStatusPath),
                    JsonOptions.Default);

            if (envelope?.Reports is null)
                return;

            foreach (var pair in envelope.Reports)
            {
                if (!_channels.TryGetValue(pair.Key, out ChannelRuntime? runtime))
                    continue;

                BrowserChannelReport report = pair.Value;
                runtime.PlaybackStatus =
                    string.IsNullOrWhiteSpace(report.Status) ? "Unknown" : report.Status;

                if (report.LastSuccessfulPlaybackUtc is not null &&
                    (runtime.LastSuccessfulPlaybackUtc is null ||
                     report.LastSuccessfulPlaybackUtc > runtime.LastSuccessfulPlaybackUtc))
                {
                    runtime.LastSuccessfulPlaybackUtc = report.LastSuccessfulPlaybackUtc;
                }

                if (runtime.LastKnownLive == true && runtime.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase))
                {
                    runtime.PlaybackConfirmedSinceUtc ??= DateTime.UtcNow;
                    int target = Math.Clamp(_settings.PlaybackSafetyTargetSeconds, 30, 1800);
                    int elapsed = (int)(DateTime.UtcNow - runtime.PlaybackConfirmedSinceUtc.Value).TotalSeconds;
                    if (!runtime.SafetyTargetReached && elapsed >= target)
                    {
                        runtime.SafetyTargetReached = true;
                        Logger.Log($"{runtime.Channel} | SAFETY TARGET REACHED | playback={elapsed}s");
                        EventHistory.Add("SAFETY", runtime.Channel, $"confirmed playback {elapsed}s");
                    }
                }
                else if (runtime.LastKnownLive == true)
                {
                    runtime.PlaybackConfirmedSinceUtc = null;
                }

                PublishStatus(
                    runtime,
                    runtime.LastKnownLive == true ? "LIVE" :
                    runtime.LastKnownLive == false ? "OFFLINE" : "Unknown");

                if (report.FailureCount >= 3 &&
                    (runtime.PlaybackStatus.Contains("Recover", StringComparison.OrdinalIgnoreCase) ||
                     runtime.PlaybackStatus.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                     runtime.PlaybackStatus.Contains("Stalled", StringComparison.OrdinalIgnoreCase)))
                {
                    DateTime now = DateTime.UtcNow;
                    if (!_problemNotifiedUtc.TryGetValue(runtime.Channel, out DateTime last) ||
                        now - last > TimeSpan.FromMinutes(10))
                    {
                        _problemNotifiedUtc[runtime.Channel] = now;
                        PlaybackProblem?.Invoke(
                            $"{runtime.Channel}: {runtime.PlaybackStatus} ({report.FailureCount} recovery attempts)");
                    }
                }
            }
        }
        catch
        {
        }
    }

    private bool _browserWatchdogBusy;

    private async Task BrowserWatchdogTickAsync()
    {
        if (_browserWatchdogBusy)
            return;

        _browserWatchdogBusy = true;
        try
        {
            if (!_settings.OpenStreamAutomatically)
                return;

            if (!string.Equals(_settings.Browser, "firefox", StringComparison.OrdinalIgnoreCase))
                return;

            var liveChannels = _channels.Values
                .Where(c => c.LastKnownLive == true)
                .Where(c => c.FreshStatusObservedUtc is not null)
                .Where(c => ChannelPreferenceTools.Get(_settings, c.Channel).AutoOpen)
                .Select(c => c.Channel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (liveChannels.Count == 0)
                return;

            Process[] firefoxProcesses = Process.GetProcessesByName("firefox");
            bool firefoxRunning = firefoxProcesses.Length > 0;
            foreach (var process in firefoxProcesses)
                process.Dispose();

            if (firefoxRunning)
            {
                try
                {
                    DateTime heartbeatWrite = File.Exists(AppPaths.NativeHeartbeatPath)
                        ? File.GetLastWriteTimeUtc(AppPaths.NativeHeartbeatPath) : DateTime.MinValue;
                    int staleSeconds = Math.Clamp(_settings.BridgeSelfHealSeconds, 10, 120);
                    if ((DateTime.UtcNow - heartbeatWrite).TotalSeconds > staleSeconds)
                    {
                        Logger.Log($"BRIDGE SELF-HEAL | stale>{staleSeconds}s | restarting Firefox for LIVE recovery");
                        foreach (var process in Process.GetProcessesByName("firefox"))
                        {
                            try { process.Kill(true); process.Dispose(); } catch { }
                        }
                        await Task.Delay(2500);
                        Process.Start(new ProcessStartInfo { FileName = "firefox.exe", UseShellExecute = true });
                        await Task.Delay(6000);
                    }
                }
                catch (Exception ex) { Logger.Log($"BRIDGE SELF-HEAL | FAILED | {ex.Message}"); }
                return;
            }

            Logger.Log($"BROWSER WATCHDOG | FIREFOX CLOSED | live={liveChannels.Count} | launching browser only");

            // Firefox Bridge is the sole owner of Twitch tab creation.
            // Launching Firefox with a Twitch URL here can race the extension and
            // create the same channel twice during browser startup.
            Process.Start(new ProcessStartInfo
            {
                FileName = "firefox.exe",
                UseShellExecute = true
            });

            Logger.Log("BROWSER WATCHDOG OPEN | browser=firefox | no-url | extension owns tabs");

            // Firefox needs a moment to start; then its extension reconnects
            // and restores/manages all current LIVE tabs.
            await Task.Delay(6000);
        }
        catch (Exception ex)
        {
            Logger.Log($"BROWSER WATCHDOG | OPEN FAILED | {ex.Message}");
        }
        finally
        {
            _browserWatchdogBusy = false;
        }
    }

    private void SaveBrowserBridge()
    {
        try
        {
            var candidateLive=_channels.Values.Where(x=>x.LastKnownLive==true && x.FreshStatusObservedUtc is not null).Where(x=>ChannelPreferenceTools.Get(_settings,x.Channel).AutoOpen).ToList();
            int burst=Math.Clamp(_settings.SafeModeBurstThreshold,2,20);
            bool suspicious=_settings.SafeMode && candidateLive.Count(x=>x.LiveDetectedUtc is not null && (DateTime.UtcNow-x.LiveDetectedUtc.Value).TotalSeconds<=30)>=burst;
            var safeLive=suspicious ? new List<ChannelRuntime>() : candidateLive;
            if(suspicious) { Logger.Log($"SAFE MODE | BLOCKED MASS LIVE OPEN | count={candidateLive.Count}"); EventHistory.Add("SAFE-MODE","",$"Blocked suspicious LIVE burst ({candidateLive.Count})"); }
            var bridge = new BrowserBridgeState
            {
                UpdatedUtc = DateTime.UtcNow,
                ReopenClosedLiveTabs = true,
                KeepManagedTabsMuted = true,
                NetworkRecoveryGeneration = _networkRecoveryGeneration,
                PlaybackStartupTimeoutSeconds = Math.Clamp(_settings.PlaybackStartupTimeoutSeconds, 10, 120),
                LiveChannels = safeLive
                    .Select(c => c.Channel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .ToList(),
                LiveStreamIds = safeLive.Where(c => !string.IsNullOrWhiteSpace(c.LastStreamId))
                    .ToDictionary(c => c.Channel, c => c.LastStreamId!, StringComparer.OrdinalIgnoreCase),
                CloseWhenOffline = _channels.Values
                    .ToDictionary(
                        c => c.Channel,
                        c => ChannelPreferenceTools.Get(_settings, c.Channel).CloseWhenOffline,
                        StringComparer.OrdinalIgnoreCase),
                MuteChannels = _channels.Values
                    .ToDictionary(
                        c => c.Channel,
                        c => ChannelPreferenceTools.Get(_settings, c.Channel).Mute,
                        StringComparer.OrdinalIgnoreCase),
                RecoveryDeadlinesUtc = _channels.Values
                    .Where(c => ChannelPreferenceTools.Get(_settings, c.Channel).RecoveryDeadlineUtc is not null)
                    .ToDictionary(
                        c => c.Channel,
                        c => ChannelPreferenceTools.Get(_settings, c.Channel).RecoveryDeadlineUtc!.Value,
                        StringComparer.OrdinalIgnoreCase)
            };

            string tmp = AppPaths.BrowserBridgePath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(bridge, JsonOptions.Default));
            File.Copy(tmp, AppPaths.BrowserBridgePath, true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Logger.Log($"BROWSER BRIDGE | SAVE FAILED | {ex.Message}");
        }
    }

    private void SaveRemoteMonitorSnapshot()
    {
        if (!_settings.RemoteMonitorSnapshotEnabled) return;
        try
        {
            var snapshot = new
            {
                version = "9.3.0",
                updatedUtc = DateTime.UtcNow,
                purpose = "Detection/notification handoff only; does not emulate Twitch viewing.",
                channels = _channels.Values.OrderBy(x => x.Channel).Select(x => new
                {
                    channel = x.Channel,
                    live = x.LastKnownLive,
                    streamId = x.LastStreamId,
                    detection = x.DetectionConfidence,
                    lastSeenUtc = x.LastSeenUtc
                }).ToList()
            };
            string tmp = AppPaths.RemoteSnapshotPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions.Default));
            File.Copy(tmp, AppPaths.RemoteSnapshotPath, true);
            File.Delete(tmp);
        }
        catch (Exception ex) { Logger.Log($"REMOTE SNAPSHOT | FAILED | {ex.Message}"); }
    }

    private string FormatNetworkState() =>
        _networkHealthy switch
        {
            true => "OK",
            false => "ISSUE",
            null => "UNKNOWN"
        };

    private static string FormatObservedDuration(DateTime? startUtc)
    {
        if (startUtc is null)
            return "unknown";

        TimeSpan d = DateTime.UtcNow - startUtc.Value;

        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";

        if (d.TotalMinutes >= 1)
            return $"{d.Minutes}m {d.Seconds}s";

        return $"{Math.Max(0, d.Seconds)}s";
    }

    private static Settings CloneSettings(Settings s)
    {
        return JsonSerializer.Deserialize<Settings>(
            JsonSerializer.Serialize(s),
            JsonOptions.Default) ?? new Settings();
    }

    public bool TryGetSafetyStatus(string channel, out int elapsed, out int target, out bool reached)
    {
        elapsed = 0;
        target = Math.Clamp(_settings.PlaybackSafetyTargetSeconds, 30, 1800);
        reached = false;
        string key = ChannelTools.Normalize(channel);
        if (!_channels.TryGetValue(key, out ChannelRuntime? runtime) || runtime.LastKnownLive != true)
            return false;
        reached = runtime.SafetyTargetReached;
        if (runtime.PlaybackConfirmedSinceUtc is not null)
            elapsed = Math.Max(0, (int)(DateTime.UtcNow - runtime.PlaybackConfirmedSinceUtc.Value).TotalSeconds);
        return true;
    }

    public bool ShouldAlertLiveNotPlaying(string channel,int seconds)
    {
        string key=ChannelTools.Normalize(channel); if(!_channels.TryGetValue(key,out ChannelRuntime? r)) return false;
        if(r.LastKnownLive!=true || r.LiveDetectedUtc is null || r.LiveNotPlayingAlertSent || r.PlaybackStatus.Equals("Playing",StringComparison.OrdinalIgnoreCase)) return false;
        if((DateTime.UtcNow-r.LiveDetectedUtc.Value).TotalSeconds<seconds) return false; r.LiveNotPlayingAlertSent=true; return true;
    }

    public void Dispose()
    {
        SaveState();
        try
        {
            var stopped = new BrowserBridgeState
            {
                UpdatedUtc = DateTime.UtcNow,
                AppRunning = false,
                ReopenClosedLiveTabs = false,
                KeepManagedTabsMuted = false,
                LiveChannels = []
            };
            File.WriteAllText(
                AppPaths.BrowserBridgePath,
                JsonSerializer.Serialize(stopped, JsonOptions.Default));
        }
        catch { }

        Logger.Log("EXITED");

        _checkTimer.Stop();
        _networkTimer.Stop();
        _heartbeatTimer.Stop();
        _browserWatchdogTimer.Stop();
        _browserStatusTimer.Stop();

        _checkTimer.Dispose();
        _networkTimer.Dispose();
        _heartbeatTimer.Dispose();
        _browserWatchdogTimer.Dispose();
        _browserStatusTimer.Dispose();

        _client.Dispose();
        _parallelGate.Dispose();
    }
}


internal sealed class BrowserBridgeState
{
    public bool AppRunning { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
    public bool ReopenClosedLiveTabs { get; set; } = true;
    public bool KeepManagedTabsMuted { get; set; } = true;
    public int NetworkRecoveryGeneration { get; set; }
    public int PlaybackStartupTimeoutSeconds { get; set; } = 20;
    public List<string> LiveChannels { get; set; } = [];
    public Dictionary<string, string> LiveStreamIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> CloseWhenOffline { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> MuteChannels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> RecoveryDeadlinesUtc { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class BrowserReportEnvelope
{
    public DateTime UpdatedUtc { get; set; }
    public Dictionary<string, BrowserChannelReport> Reports { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class BrowserChannelReport
{
    public string Status { get; set; } = "Unknown";
    public DateTime? LastSuccessfulPlaybackUtc { get; set; }
    public int FailureCount { get; set; }
    public int? TabId { get; set; }
}

internal sealed class NativeRequest
{
    public string? Type { get; set; }
    public string? ExtensionVersion { get; set; }
    public Dictionary<string, BrowserChannelReport>? Reports { get; set; }
}

internal static class NativeMessagingHost
{
    public static void Run()
    {
        try
        {
            AppPaths.EnsureDirectories();
            NativeLog("HOST STARTED");

            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();

            while (true)
            {
                byte[] lenBytes = new byte[4];
                if (!ReadExactly(input, lenBytes, 4))
                    break;

                int length = BitConverter.ToInt32(lenBytes, 0);
                if (length <= 0 || length > 1024 * 1024)
                    break;

                byte[] payload = new byte[length];
                if (!ReadExactly(input, payload, length))
                    break;

                string requestJson = Encoding.UTF8.GetString(payload);
                NativeLog("REQUEST RECEIVED");

                NativeRequest? nativeRequest = null;
                try
                {
                    nativeRequest = JsonSerializer.Deserialize<NativeRequest>(
                        requestJson,
                        JsonOptions.Default);
                }
                catch { }

                try
                {
                    string heartbeatTmp = AppPaths.NativeHeartbeatPath + ".tmp";
                    string extensionVersion =
                        string.IsNullOrWhiteSpace(nativeRequest?.ExtensionVersion)
                            ? "unknown"
                            : nativeRequest!.ExtensionVersion!.Trim();

                    File.WriteAllText(
                        heartbeatTmp,
                        JsonSerializer.Serialize(
                            new
                            {
                                updatedUtc = DateTime.UtcNow,
                                extensionVersion,
                                hostPid = Environment.ProcessId
                            },
                            JsonOptions.Default));
                    File.Copy(heartbeatTmp, AppPaths.NativeHeartbeatPath, true);
                    File.Delete(heartbeatTmp);
                }
                catch { }

                try
                {
                    if (nativeRequest?.Reports is not null)
                    {
                        var reportEnvelope = new BrowserReportEnvelope
                        {
                            UpdatedUtc = DateTime.UtcNow,
                            Reports = nativeRequest.Reports
                        };

                        string reportTmp = AppPaths.BrowserStatusPath + ".tmp";
                        File.WriteAllText(
                            reportTmp,
                            JsonSerializer.Serialize(reportEnvelope, JsonOptions.Default));
                        File.Copy(reportTmp, AppPaths.BrowserStatusPath, true);
                        File.Delete(reportTmp);
                    }
                }
                catch { }

                BrowserBridgeState state;
                try
                {
                    state = File.Exists(AppPaths.BrowserBridgePath)
                        ? JsonSerializer.Deserialize<BrowserBridgeState>(
                              File.ReadAllText(AppPaths.BrowserBridgePath),
                              JsonOptions.Default)
                          ?? new BrowserBridgeState { AppRunning = false }
                        : new BrowserBridgeState { AppRunning = false };
                }
                catch
                {
                    state = new BrowserBridgeState { AppRunning = false };
                }

                byte[] response = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(state, JsonOptions.Default));

                byte[] responseLength = BitConverter.GetBytes(response.Length);
                output.Write(responseLength, 0, responseLength.Length);
                output.Write(response, 0, response.Length);
                output.Flush();
            }

            NativeLog("HOST DISCONNECTED");
        }
        catch (Exception ex)
        {
            // Never write diagnostics to stdout: stdout is reserved for the native messaging protocol.
            NativeLog($"HOST ERROR | {ex.GetType().Name} | {ex.Message}");
        }
    }

    private static void NativeLog(string message)
    {
        try
        {
            string path = Path.Combine(AppPaths.AppDataFolder, "native-bridge.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buffer, offset, count - offset);
            if (n <= 0)
                return false;
            offset += n;
        }
        return true;
    }
}

internal static class ChannelTools
{
    public static string Normalize(string value)
    {
        string channel = (value ?? "").Trim();

        if (Uri.TryCreate(
                channel,
                UriKind.Absolute,
                out var uri) &&
            uri.Host.Contains(
                "twitch.tv",
                StringComparison.OrdinalIgnoreCase))
        {
            channel = uri.AbsolutePath.Trim('/');

            if (channel.Contains('/'))
                channel = channel.Split('/')[0];
        }

        return channel
            .Trim()
            .TrimStart('@')
            .ToLowerInvariant();
    }
}

internal static class SettingsStore
{
    public static Settings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                var defaults = new Settings();
                Save(defaults);
                return defaults;
            }

            Settings loaded = JsonSerializer.Deserialize<Settings>(
                                  File.ReadAllText(AppPaths.SettingsPath),
                                  JsonOptions.Default)
                              ?? new Settings();

            loaded.ChannelPreferences ??=
                new Dictionary<string, ChannelPreference>(StringComparer.OrdinalIgnoreCase);
            loaded.DiscordRoutes ??=
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string channel in loaded.Channels.Select(ChannelTools.Normalize))
                _ = ChannelPreferenceTools.Get(loaded, channel);

            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Log($"SETTINGS | LOAD FAILED | {ex.Message}");
            return new Settings();
        }
    }

    public static void Save(Settings settings)
    {
        AppPaths.EnsureDirectories();

        string tmp = AppPaths.SettingsPath + ".tmp";

        File.WriteAllText(
            tmp,
            JsonSerializer.Serialize(
                settings,
                JsonOptions.Default));

        File.Copy(
            tmp,
            AppPaths.SettingsPath,
            true);

        File.Delete(tmp);

        Logger.Log("SETTINGS | SAVED");
    }

    public static void TryMigrateLegacySettings()
    {
        if (File.Exists(AppPaths.SettingsPath))
            return;

        string legacy =
            Path.Combine(
                AppContext.BaseDirectory,
                "settings.json");

        if (!File.Exists(legacy))
            return;

        try
        {
            File.Copy(
                legacy,
                AppPaths.SettingsPath,
                false);

            Logger.Log(
                "SETTINGS | MIGRATED legacy settings.json to AppData");
        }
        catch
        {
        }
    }
}

internal static class Logger
{
    private static readonly object Sync = new();
    public static string? CurrentLogPath { get; private set; }

    public static void Initialize()
    {
        lock (Sync)
        {
            try
            {
                AppPaths.EnsureDirectories();

                CurrentLogPath = Path.Combine(
                    AppPaths.LogsFolder,
                    $"streakwatch-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.log");

                File.WriteAllText(
                    CurrentLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SESSION LOG CREATED{Environment.NewLine}");

                File.WriteAllText(
                    AppPaths.LatestLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SESSION LOG CREATED{Environment.NewLine}");

                CleanupHistory();
            }
            catch
            {
                CurrentLogPath = null;
            }
        }
    }

    public static void Log(string message)
    {
        lock (Sync)
        {
            try
            {
                AppPaths.EnsureDirectories();
                string line =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

                if (!string.IsNullOrWhiteSpace(CurrentLogPath))
                    File.AppendAllText(CurrentLogPath, line);

                File.AppendAllText(AppPaths.LatestLogPath, line);
            }
            catch
            {
            }
        }
    }

    private static void CleanupHistory()
    {
        try
        {
            var files = Directory.GetFiles(
                    AppPaths.LogsFolder,
                    "streakwatch-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            DateTime cutoff = DateTime.UtcNow.AddDays(-30);

            foreach (var file in files.Where(f => f.CreationTimeUtc < cutoff))
            {
                try { file.Delete(); } catch { }
            }

            files = Directory.GetFiles(
                    AppPaths.LogsFolder,
                    "streakwatch-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            foreach (var file in files.Skip(100))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "StreakWatch";

    public static void Enable(Action<string> log)
    {
        try
        {
            string? exe =
                Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(exe))
            {
                log("STARTUP | ENABLE FAILED | Process path unavailable");
                return;
            }

            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    RunKeyPath,
                    writable: true);

            key?.SetValue(
                ValueName,
                $"\"{exe}\"");

            log($"STARTUP | ENABLED | {exe}");
        }
        catch (Exception ex)
        {
            log($"STARTUP | ENABLE FAILED | {ex.Message}");
        }
    }

    public static void Disable(Action<string> log)
    {
        try
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    RunKeyPath,
                    writable: true);

            key?.DeleteValue(
                ValueName,
                throwOnMissingValue: false);

            log("STARTUP | DISABLED");
        }
        catch (Exception ex)
        {
            log($"STARTUP | DISABLE FAILED | {ex.Message}");
        }
    }
}

internal static class StateStore
{
    private static readonly object Sync = new();

    public static AppState Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(AppPaths.StatePath))
                    return new AppState();

                return JsonSerializer.Deserialize<AppState>(
                           File.ReadAllText(AppPaths.StatePath),
                           JsonOptions.Default)
                       ?? new AppState();
            }
            catch (Exception ex)
            {
                Logger.Log($"STATE | LOAD FAILED | {ex.Message}");
                return new AppState();
            }
        }
    }

    public static void Save(AppState state)
    {
        lock (Sync)
        {
            try
            {
                string tmp = AppPaths.StatePath + ".tmp";

                File.WriteAllText(
                    tmp,
                    JsonSerializer.Serialize(
                        state,
                        JsonOptions.Default));

                File.Copy(
                    tmp,
                    AppPaths.StatePath,
                    true);

                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                Logger.Log($"STATE | SAVE FAILED | {ex.Message}");
            }
        }
    }
}

internal sealed class TwitchStatusClient : IDisposable
{
    private readonly HttpClient _http;

    private const string ClientId =
        "kimne78kx3ncx6brgo4mv6wki5h1ko";

    public TwitchStatusClient()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");

        _http.DefaultRequestHeaders.Add(
            "Client-ID",
            ClientId);

        _http.DefaultRequestHeaders.Referrer =
            new Uri("https://www.twitch.tv/");
    }

    public async Task<ChannelStatus> GetStatusAsync(
        string channel)
    {
        const string query = """
            query GetLiveStatus($login: String!) {
              user(login: $login) {
                id
                login
                stream {
                  id
                }
              }
            }
            """;

        var body = new
        {
            query,
            variables = new
            {
                login = channel
            }
        };

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                "https://gql.twitch.tv/gql");

        request.Content =
            new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

        HttpResponseMessage response;

        try
        {
            response =
                await _http.SendAsync(request);
        }
        catch (TaskCanceledException)
        {
            throw new TwitchRequestException(
                TwitchErrorKind.NetworkTimeout,
                "Twitch request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new TwitchRequestException(
                TwitchErrorKind.NetworkOffline,
                ex.Message);
        }

        using (response)
        {
            string json =
                await response.Content.ReadAsStringAsync();

            if (response.StatusCode ==
                HttpStatusCode.TooManyRequests)
            {
                throw new TwitchRequestException(
                    TwitchErrorKind.RateLimited,
                    "HTTP 429 Too Many Requests.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TwitchRequestException(
                    TwitchErrorKind.ApiError,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            using var doc =
                JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty(
                    "errors",
                    out var errors))
            {
                throw new TwitchRequestException(
                    TwitchErrorKind.GraphQlError,
                    errors.GetRawText());
            }

            if (!doc.RootElement.TryGetProperty(
                    "data",
                    out var data) ||
                !data.TryGetProperty(
                    "user",
                    out var user) ||
                user.ValueKind ==
                    JsonValueKind.Null)
            {
                return new ChannelStatus(
                    false,
                    false,
                    null);
            }

            if (user.TryGetProperty(
                    "stream",
                    out var stream) &&
                stream.ValueKind ==
                    JsonValueKind.Object)
            {
                string? streamId = null;

                if (stream.TryGetProperty(
                        "id",
                        out var id))
                {
                    streamId = id.GetString();
                }

                return new ChannelStatus(
                    true,
                    true,
                    streamId);
            }

            // Primary user.stream occasionally reports null even while Twitch's
            // actual player is live. Before accepting OFFLINE, ask the player-token
            // resolver as an independent second signal. Streamlink and Twitch's
            // player use this path to obtain live playback access.
            bool playbackLive = await CheckPlaybackTokenLiveAsync(channel);
            if (playbackLive)
            {
                Logger.Log(
                    $"{channel} | LIVE FALLBACK CONFIRMED | primary=user.stream:null | signal=playable-hls");

                // Try the primary query once more after a short delay so we can
                // recover the real Stream ID when Twitch's status resolver is lagging.
                await Task.Delay(700);
                ChannelStatus retry = await RetryPrimaryStatusAsync(channel);
                if (retry.IsLive)
                {
                    Logger.Log(
                        $"{channel} | LIVE FALLBACK | primary recovered | stream={retry.StreamId ?? "unknown"}");
                    return retry;
                }

                // Live is still confirmed by the player signal. A null Stream ID is
                // intentional here; transitions still open the real Twitch channel,
                // and a later primary poll can populate the Stream ID.
                return new ChannelStatus(
                    true,
                    true,
                    null,
                    "HLS fallback");
            }

            return new ChannelStatus(
                true,
                false,
                null);
        }
    }

    private async Task<ChannelStatus> RetryPrimaryStatusAsync(string channel)
    {
        const string query = """
            query GetLiveStatusRetry($login: String!) {
              user(login: $login) {
                id
                login
                stream { id }
              }
            }
            """;

        var body = new { query, variables = new { login = channel } };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://gql.twitch.tv/gql");
        request.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new ChannelStatus(true, false, null);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("user", out var user) ||
                user.ValueKind == JsonValueKind.Null)
                return new ChannelStatus(false, false, null);

            if (user.TryGetProperty("stream", out var stream) &&
                stream.ValueKind == JsonValueKind.Object)
            {
                string? streamId = stream.TryGetProperty("id", out var id)
                    ? id.GetString()
                    : null;
                return new ChannelStatus(true, true, streamId, "Primary API");
            }

            return new ChannelStatus(true, false, null);
        }
        catch
        {
            return new ChannelStatus(true, false, null);
        }
    }

    private async Task<bool> CheckPlaybackTokenLiveAsync(string channel)
    {
        // IMPORTANT:
        // Twitch may issue a playback access token even for an OFFLINE channel.
        // Therefore a token by itself is NOT a LIVE signal.
        //
        // We only confirm LIVE if Twitch's actual HLS usher endpoint returns a
        // playable master playlist for this channel.
        const string query = """
            query PlaybackLiveFallback($login: String!) {
              streamPlaybackAccessToken(
                channelName: $login,
                params: {
                  platform: "web",
                  playerBackend: "mediaplayer",
                  playerType: "site"
                }
              ) {
                value
                signature
              }
            }
            """;

        var body = new
        {
            query,
            variables = new { login = channel }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://gql.twitch.tv/gql");
        request.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Logger.Log($"{channel} | LIVE FALLBACK | rate-limited");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Log(
                    $"{channel} | LIVE FALLBACK | token HTTP {(int)response.StatusCode}");
                return false;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                Logger.Log(
                    $"{channel} | LIVE FALLBACK | GraphQL error | {errors.GetRawText()}");
                return false;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("streamPlaybackAccessToken", out var token) ||
                token.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            string? tokenValue =
                token.TryGetProperty("value", out var value)
                    ? value.GetString()
                    : null;

            string? signature =
                token.TryGetProperty("signature", out var sig)
                    ? sig.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(tokenValue) ||
                string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            string usher =
                $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(channel)}.m3u8" +
                $"?sig={Uri.EscapeDataString(signature)}" +
                $"&token={Uri.EscapeDataString(tokenValue)}" +
                "&allow_source=true&allow_audio_only=true&player=twitchweb&p=1";

            using var hlsRequest = new HttpRequestMessage(HttpMethod.Get, usher);
            hlsRequest.Headers.Referrer = new Uri($"https://www.twitch.tv/{channel}");

            using HttpResponseMessage hlsResponse = await _http.SendAsync(hlsRequest);

            if (!hlsResponse.IsSuccessStatusCode)
            {
                Logger.Log(
                    $"{channel} | LIVE FALLBACK | HLS not live | HTTP {(int)hlsResponse.StatusCode}");
                return false;
            }

            string playlist = await hlsResponse.Content.ReadAsStringAsync();

            bool playable =
                playlist.Contains("#EXTM3U", StringComparison.Ordinal) &&
                playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

            if (!playable)
            {
                Logger.Log(
                    $"{channel} | LIVE FALLBACK | HLS response not a playable master playlist");
                return false;
            }

            Logger.Log(
                $"{channel} | LIVE FALLBACK CONFIRMED | signal=playable-hls");
            return true;
        }
        catch (TaskCanceledException)
        {
            Logger.Log($"{channel} | LIVE FALLBACK | timeout");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"{channel} | LIVE FALLBACK | failed | {ex.GetType().Name}");
            return false;
        }
    }

    public async Task<bool> ProbeTwitchAsync()
    {
        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.twitch.tv/");

        using var response =
            await _http.SendAsync(request);

        return response.IsSuccessStatusCode ||
               response.StatusCode ==
                   HttpStatusCode.Forbidden ||
               response.StatusCode ==
                   HttpStatusCode.TooManyRequests;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

internal static class DiscordEventKeys
{
    public const string Live = "live";
    public const string Offline = "offline";
    public const string PlaybackStarted = "playback-started";
    public const string PlaybackProblem = "playback-problem";
    public const string NetworkLost = "network-lost";
    public const string NetworkRestored = "network-restored";
    public const string RecoveryImportant = "recovery-important";
    public const string AppExit = "app-exit";

    public static readonly (string Key, string Label)[] All =
    [
        (Live, "Stream goes LIVE"),
        (Offline, "Stream goes OFFLINE"),
        (PlaybackStarted, "Playback started"),
        (PlaybackProblem, "Playback problem"),
        (NetworkLost, "Internet/Twitch connection lost"),
        (NetworkRestored, "Internet/Twitch connection restored"),
        (RecoveryImportant, "Important recovery deadline warnings"),
        (AppExit, "StreakWatch exits / stops")
    ];
}

internal sealed class DiscordWebhookSecret
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Discord room";
    public string WebhookUrl { get; set; } = "";
}

internal static class DiscordSecretStore
{
    private sealed class SecretFileModel
    {
        public List<DiscordWebhookSecret> Webhooks { get; set; } = [];
        // V9.1.0-V9.1.2 migration.
        public string? WebhookUrl { get; set; }
    }

    public static List<DiscordWebhookSecret> LoadWebhooks()
    {
        try
        {
            if (!File.Exists(AppPaths.DiscordSecretPath))
                return [];

            SecretFileModel? model = JsonSerializer.Deserialize<SecretFileModel>(
                File.ReadAllText(AppPaths.DiscordSecretPath), JsonOptions.Default);

            if (model is null)
                return [];

            var result = (model.Webhooks ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.WebhookUrl))
                .Select(x =>
                {
                    x.Id = string.IsNullOrWhiteSpace(x.Id)
                        ? Guid.NewGuid().ToString("N")
                        : x.Id.Trim();
                    x.Name = string.IsNullOrWhiteSpace(x.Name)
                        ? "Discord room"
                        : x.Name.Trim();
                    x.WebhookUrl = x.WebhookUrl.Trim();
                    return x;
                })
                .ToList();

            if (result.Count == 0 &&
                !string.IsNullOrWhiteSpace(model.WebhookUrl))
            {
                result.Add(new DiscordWebhookSecret
                {
                    Id = "legacy-default",
                    Name = "Default",
                    WebhookUrl = model.WebhookUrl.Trim()
                });

                SaveWebhooks(result);
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    public static void SaveWebhooks(IEnumerable<DiscordWebhookSecret> webhooks)
    {
        try
        {
            AppPaths.EnsureDirectories();

            var clean = webhooks
                .Where(x => !string.IsNullOrWhiteSpace(x.WebhookUrl))
                .Select(x => new DiscordWebhookSecret
                {
                    Id = string.IsNullOrWhiteSpace(x.Id)
                        ? Guid.NewGuid().ToString("N")
                        : x.Id.Trim(),
                    Name = string.IsNullOrWhiteSpace(x.Name)
                        ? "Discord room"
                        : x.Name.Trim(),
                    WebhookUrl = x.WebhookUrl.Trim()
                })
                .ToList();

            string tmp = AppPaths.DiscordSecretPath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(
                    new SecretFileModel { Webhooks = clean },
                    JsonOptions.Default));

            File.Copy(tmp, AppPaths.DiscordSecretPath, true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Logger.Log($"DISCORD | SECRET SAVE FAILED | {ex.Message}");
        }
    }
}

internal sealed class DiscordNotifier : IDisposable
{
    private sealed class QueueItem
    {
        public DateTime CreatedUtc { get; set; }
        public string WebhookId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly System.Threading.Timer _retryTimer;
    private Settings _settings;
    private readonly List<QueueItem> _queue = [];
    private readonly object _queueSync = new();

    public DiscordNotifier(Settings settings)
    {
        _settings = settings;
        LoadQueue();
        _retryTimer = new System.Threading.Timer(
            async _ => await FlushQueueAsync(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void ApplySettings(Settings settings) => _settings = settings;

    public static string FormatTimestamped(string title, string? details = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string time = now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        return string.IsNullOrWhiteSpace(details)
            ? $"{title}\n🕒 `{time}`"
            : $"{title}\n🕒 `{time}`\n{details}";
    }

    public static bool IsValidWebhook(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;
        string host = uri.Host.ToLowerInvariant();
        return (host == "discord.com" || host == "discordapp.com") &&
               uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> SendTestAsync(string webhookUrl, string roomName)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    username = "StreakWatch",
                    content = FormatTimestamped(
                        "✅ **StreakWatch Discord test successful**",
                        $"Destination: {roomName}")
                }),
                Encoding.UTF8,
                "application/json");
            using var response = await http.PostAsync(webhookUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task NotifyEventAsync(
        string eventKey,
        string title,
        string? details = null)
    {
        if (!_settings.DiscordEnabled)
            return;

        string message = FormatTimestamped(title, details);
        List<DiscordWebhookSecret> webhooks = DiscordSecretStore.LoadWebhooks();
        if (webhooks.Count == 0)
            return;

        var byId = webhooks.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        List<string> targetIds = [];
        if (_settings.DiscordRoutes is not null &&
            _settings.DiscordRoutes.TryGetValue(eventKey, out List<string>? configured))
        {
            targetIds = configured
                .Where(id => byId.ContainsKey(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Backward compatibility: one configured webhook with no routing matrix
        // continues to receive all enabled notification types.
        if (targetIds.Count == 0 &&
            webhooks.Count == 1 &&
            (_settings.DiscordRoutes is null ||
             !_settings.DiscordRoutes.ContainsKey(eventKey)))
        {
            targetIds.Add(webhooks[0].Id);
        }

        foreach (string id in targetIds)
        {
            if (!byId.TryGetValue(id, out DiscordWebhookSecret? webhook))
                continue;

            if (!await TrySendAsync(webhook, message))
                Enqueue(webhook.Id, message);
        }

        await FlushQueueAsync();
    }

    private async Task<bool> TrySendAsync(
        DiscordWebhookSecret webhook,
        string message)
    {
        if (!IsValidWebhook(webhook.WebhookUrl))
            return false;

        await _sendGate.WaitAsync();
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    username = "StreakWatch",
                    embeds = new[] { new { title = "StreakWatch", description = message.Length > 3900 ? message[..3900] : message, timestamp = DateTimeOffset.UtcNow.ToString("O") } }
                }),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync(webhook.WebhookUrl, content);
            if (response.IsSuccessStatusCode)
            {
                Logger.Log($"DISCORD | SENT | destination={webhook.Name}");
                return true;
            }

            Logger.Log(
                $"DISCORD | SEND FAILED | destination={webhook.Name} | HTTP {(int)response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"DISCORD | SEND FAILED | destination={webhook.Name} | {ex.GetType().Name}");
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void Enqueue(string webhookId, string message)
    {
        lock (_queueSync)
        {
            _queue.Add(new QueueItem
            {
                CreatedUtc = DateTime.UtcNow,
                WebhookId = webhookId,
                Message = message
            });

            if (_queue.Count > 200)
                _queue.RemoveRange(0, _queue.Count - 200);

            SaveQueueLocked();
        }

        Logger.Log("DISCORD | QUEUED FOR RETRY");
    }

    private async Task FlushQueueAsync()
    {
        if (!_settings.DiscordEnabled)
            return;

        QueueItem? item;
        lock (_queueSync)
            item = _queue.FirstOrDefault();

        if (item is null)
            return;

        List<DiscordWebhookSecret> webhooks = DiscordSecretStore.LoadWebhooks();
        DiscordWebhookSecret? webhook = webhooks.FirstOrDefault(
            x => x.Id.Equals(item.WebhookId, StringComparison.OrdinalIgnoreCase));

        if (webhook is null)
        {
            lock (_queueSync)
            {
                if (_queue.Count > 0)
                    _queue.RemoveAt(0);
                SaveQueueLocked();
            }
            return;
        }

        if (await TrySendAsync(webhook, item.Message))
        {
            lock (_queueSync)
            {
                if (_queue.Count > 0)
                    _queue.RemoveAt(0);
                SaveQueueLocked();
            }
        }
    }

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(AppPaths.DiscordQueuePath))
                return;

            List<QueueItem>? items = JsonSerializer.Deserialize<List<QueueItem>>(
                File.ReadAllText(AppPaths.DiscordQueuePath), JsonOptions.Default);

            if (items is null)
                return;

            lock (_queueSync)
            {
                _queue.Clear();
                _queue.AddRange(items
                    .Where(x =>
                        x.CreatedUtc > DateTime.UtcNow.AddDays(-2) &&
                        !string.IsNullOrWhiteSpace(x.WebhookId))
                    .TakeLast(200));
            }
        }
        catch
        {
        }
    }

    private void SaveQueueLocked()
    {
        try
        {
            File.WriteAllText(
                AppPaths.DiscordQueuePath,
                JsonSerializer.Serialize(_queue, JsonOptions.Default));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _retryTimer.Dispose();
        _sendGate.Dispose();
        _http.Dispose();
    }
}

internal sealed class Settings
{
    public int CheckEverySeconds { get; set; } = 15;
    public bool OpenStreamAutomatically { get; set; } = true;
    public string Browser { get; set; } = "firefox";
    public int BrowserCheckDelaySeconds { get; set; } = 4;
    public int BrowserRetrySeconds { get; set; } = 10;
    public bool OpenAlreadyLiveOnStartup { get; set; } = false;
    public int NetworkRetrySeconds { get; set; } = 5;
    public int HeartbeatMinutes { get; set; } = 30;
    public int MaxConcurrentChecks { get; set; } = 4;
    public bool StartWithWindows { get; set; } = false;
    public int OfflineGraceSeconds { get; set; } = 45;
    public int PlaybackStartupTimeoutSeconds { get; set; } = 20;
    public bool DiscordEnabled { get; set; } = false;
    public bool DiscordNotifyLive { get; set; } = true;
    public bool DiscordNotifyOffline { get; set; } = true;
    public bool DiscordNotifyPlaybackStarted { get; set; } = true;
    public bool DiscordNotifyPlaybackProblem { get; set; } = true;
    public bool DiscordNotifyNetworkLost { get; set; } = true;
    public bool DiscordNotifyNetworkRestored { get; set; } = true;
    public bool DiscordNotifyRecoveryImportant { get; set; } = true;
    public Dictionary<string, List<string>> DiscordRoutes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool SafeMode { get; set; } = true;
    public int SafeModeBurstThreshold { get; set; } = 4;
    public bool LiveNotPlayingAlert { get; set; } = true;
    public int LiveNotPlayingSeconds { get; set; } = 60;
    public int PlaybackSafetyTargetSeconds { get; set; } = 300;
    public int SmartLiveRecheckSeconds { get; set; } = 5;
    public int BridgeSelfHealSeconds { get; set; } = 15;
    public bool RemoteMonitorSnapshotEnabled { get; set; } = true;
    public bool UpdateCheckEnabled { get; set; } = false;
    public string UpdateManifestUrl { get; set; } = "";
    public List<string> Channels { get; set; } = [];
    public Dictionary<string, ChannelPreference> ChannelPreferences { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ChannelPreference
{
    public bool AutoOpen { get; set; } = true;
    public bool CloseWhenOffline { get; set; } = true;
    public bool Mute { get; set; } = true;
    public DateTime? RecoveryDeadlineUtc { get; set; }
    public string Priority { get; set; } = "Normal";
}

internal static class ChannelPreferenceTools
{
    public static ChannelPreference Get(Settings settings, string channel)
    {
        settings.ChannelPreferences ??=
            new Dictionary<string, ChannelPreference>(StringComparer.OrdinalIgnoreCase);

        string key = ChannelTools.Normalize(channel);
        if (!settings.ChannelPreferences.TryGetValue(key, out ChannelPreference? pref))
        {
            pref = new ChannelPreference();
            settings.ChannelPreferences[key] = pref;
        }

        return pref;
    }
}

internal sealed class ChannelRuntime
{
    public string Channel { get; set; } = "";
    public bool? LastKnownLive { get; set; }
    public string? LastStreamId { get; set; }
    public string? LastOpenedStreamId { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? FreshStatusObservedUtc { get; set; }
    public DateTime? LiveObservedSinceUtc { get; set; }
    public DateTime? OfflineCandidateSinceUtc { get; set; }
    public string PlaybackStatus { get; set; } = "Unknown";
    public string DetectionConfidence { get; set; } = "Unknown";
    public DateTime? LiveDetectedUtc { get; set; }
    public bool LiveNotPlayingAlertSent { get; set; }
    public DateTime? LastPriorityCheckUtc { get; set; }
    public DateTime? LastSuccessfulPlaybackUtc { get; set; }
    public DateTime? PlaybackConfirmedSinceUtc { get; set; }
    public bool SafetyTargetReached { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? NextAllowedCheckUtc { get; set; }
}

internal sealed class AppState
{
    public Dictionary<string, PersistedChannelState>
        Channels { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PersistedChannelState
{
    public bool? LastKnownLive { get; set; }
    public string? LastStreamId { get; set; }
    public string? LastOpenedStreamId { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LiveObservedSinceUtc { get; set; }
    public DateTime? LastSuccessfulPlaybackUtc { get; set; }
}

internal sealed class TwitchRequestException : Exception
{
    public TwitchErrorKind Kind { get; }

    public TwitchRequestException(
        TwitchErrorKind kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }
}

internal enum TwitchErrorKind
{
    NetworkOffline,
    NetworkTimeout,
    RateLimited,
    ApiError,
    GraphQlError,
    Unknown
}

internal readonly record struct ChannelStatus(
    bool Exists,
    bool IsLive,
    string? StreamId,
    string Signal = "Primary API");

internal readonly record struct ChannelUiStatus(
    string Channel,
    string Status,
    string PlaybackStatus,
    string? StreamId,
    DateTime? LastSeenLocal,
    DateTime? LastSuccessfulPlaybackLocal,
    string DetectionConfidence = "Unknown");
