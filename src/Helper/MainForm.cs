using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Helper;

public sealed partial class MainForm : Form
{
    private const string ValheimSteamAppId = "892970";
    private const int WM_QUERYENDSESSION = 0x0011;

    // Tell Windows we're blocking shutdown/restart (and why) while a world sync is in flight.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    private readonly AppConfig _config;
    private CoordinatorClient? _client;
    private TableLayoutPanel _root = null!; // built in BuildUi, before anything reads it

    private string? _token;        // non-null while we hold the lock (i.e. we are hosting)
    private int _baseVersion;      // version to send as baseVersion on the next upload
    private bool _valheimSeenRunning;
    private bool _busy;            // guards against overlapping network/file operations
    private bool _coordinatorReachable = true;

    // Controls
    private readonly TextBox _txtName = NewText();
    private readonly TextBox _txtPassphrase = NewText(password: true);
    private readonly TextBox _txtWorld = NewText();
    private readonly TextBox _txtWorldsFolder = NewText();
    private readonly TextBox _txtUrl = NewText();
    private readonly CheckBox _chkAutoSave = new() { Text = "Auto-save every 10 min while hosting", AutoSize = true };
    private readonly CheckBox _chkAutoLaunch = new() { Text = "Launch Valheim automatically when the world is ready", AutoSize = true };
    private readonly Button _btnSaveSettings = NewButton("Save settings");

    private readonly Label _lblStatus = new()
    {
        AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 0),
    };
    private readonly Label _lblDetail = new()
    {
        AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 8),
    };

    private readonly FlowLayoutPanel _joinerPanel = NewStrip();
    private readonly Label _lblJoinCode = new()
    {
        AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Margin = new Padding(0, 6, 12, 3),
    };
    private readonly Button _btnCopyCode = NewButton("Copy");

    private readonly FlowLayoutPanel _hostCodePanel = NewStrip();
    private readonly TextBox _txtJoinCode = new() { PlaceholderText = "join code", Margin = new Padding(0, 5, 8, 3) };
    private readonly Button _btnShareCode = NewButton("Share join code");

    private readonly Button _btnHost = NewButton("Host this world");
    private readonly Button _btnLaunch = NewButton("Launch Valheim");
    private readonly Button _btnStop = NewButton("Stop hosting (save & release)");

    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 24, 18), ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9),
    };

    private readonly System.Windows.Forms.Timer _tmrState = new() { Interval = 4000 };
    private readonly System.Windows.Forms.Timer _tmrHeartbeat = new() { Interval = 30000 };
    private readonly System.Windows.Forms.Timer _tmrValheim = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _tmrAutoSave = new() { Interval = 600000 }; // 10 min

    public MainForm()
    {
        _config = AppConfig.Load();
        Text = "Valheim World Keeper";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9);

        BuildUi();
        SizeToScreen();
        LoadConfigIntoUi();
        RebuildClient();

        _btnSaveSettings.Click += (_, _) => SaveSettings();
        _btnHost.Click += async (_, _) => await HostAsync();
        _btnStop.Click += async (_, _) => await StopHostingAsync(auto: false);
        _btnLaunch.Click += (_, _) => LaunchValheim();
        _btnShareCode.Click += async (_, _) => await ShareJoinCodeAsync();
        _btnCopyCode.Click += (_, _) => { if (_lblJoinCode.Tag is string c) Clipboard.SetText(c); Log("Join code copied."); };

        _tmrState.Tick += async (_, _) => await RefreshStateAsync();
        _tmrHeartbeat.Tick += async (_, _) => await HeartbeatAsync();
        _tmrValheim.Tick += async (_, _) => await WatchValheimAsync();
        _tmrAutoSave.Tick += async (_, _) => await AutoSaveAsync();

        _tmrState.Start();
        _tmrValheim.Start();
        FormClosing += OnClosing;

        Log("Ready. Fill in your settings and click \"Host this world\" when you want to play.");
        _ = RefreshStateAsync();
    }

    // ---- UI construction ----

    private static TextBox NewText(bool password = false) =>
        new() { UseSystemPasswordChar = password };

    // Buttons size themselves from their own text, so a larger system font or display scale grows
    // them instead of clipping the caption.
    private static Button NewButton(string text) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 8, 8),
    };

    // A row of controls that wraps to the next line when the window is too narrow for it.
    private static FlowLayoutPanel NewStrip() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0),
    };

    // The whole form is built from AutoSize rows inside one scrollable table: no hard-coded panel
    // heights, so nothing can be clipped out of view at a larger display scale or system font size
    // (which is how the "Save settings" button used to disappear), and expanding the window hands
    // the extra space to the activity log rather than leaving dead space.
    private void BuildUi()
    {
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(12),
        };
        var root = _root;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control c, SizeType sizeType = SizeType.AutoSize, float size = 0f)
        {
            root.RowStyles.Add(new RowStyle(sizeType, size));
            c.Dock = DockStyle.Fill;
            root.Controls.Add(c, 0, root.RowCount);
            root.RowCount++;
        }

        var settings = new GroupBox
        {
            Text = "Settings", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8), Margin = new Padding(0, 0, 0, 4),
            // Grows with the window, but stops before the text boxes become absurdly wide on a
            // maximised 4K window.
            MaximumSize = new Size(LogicalToDeviceUnits(760), 0),
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // labels: as wide as the text needs
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // inputs: take the rest
        void Row(string label, Control input)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(
                new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) },
                0, grid.RowCount);
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(3, 4, 3, 4);
            grid.Controls.Add(input, 1, grid.RowCount);
            grid.RowCount++;
        }
        void SpanRow(Control c)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(c, 0, grid.RowCount);
            grid.SetColumnSpan(c, 2);
            grid.RowCount++;
        }
        Row("Your name", _txtName);
        Row("Group passphrase", _txtPassphrase);
        Row("World name", _txtWorld);
        Row("Worlds folder", _txtWorldsFolder);
        Row("Coordinator URL", _txtUrl);
        _chkAutoSave.Margin = new Padding(3, 8, 3, 2);
        _chkAutoLaunch.Margin = new Padding(3, 2, 3, 2);
        _btnSaveSettings.Anchor = AnchorStyles.Left;
        _btnSaveSettings.Margin = new Padding(3, 10, 3, 3);
        SpanRow(_chkAutoSave);
        SpanRow(_chkAutoLaunch);
        SpanRow(_btnSaveSettings);
        settings.Controls.Add(grid);

        _txtJoinCode.Width = LogicalToDeviceUnits(140);
        _joinerPanel.Controls.Add(_lblJoinCode);
        _joinerPanel.Controls.Add(_btnCopyCode);
        _hostCodePanel.Controls.Add(_txtJoinCode);
        _hostCodePanel.Controls.Add(_btnShareCode);

        var actions = NewStrip();
        actions.Margin = new Padding(0, 6, 0, 4);
        actions.Controls.Add(_btnHost);
        actions.Controls.Add(_btnLaunch);
        actions.Controls.Add(_btnStop);

        var logLabel = new Label { Text = "Activity", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
        _txtLog.MinimumSize = new Size(0, LogicalToDeviceUnits(80));

        AddRow(settings);
        AddRow(_lblStatus);
        AddRow(_lblDetail);
        AddRow(_joinerPanel);
        AddRow(_hostCodePanel);
        AddRow(actions);
        AddRow(logLabel);
        AddRow(_txtLog, SizeType.Percent, 100f); // soaks up whatever height is left
        Controls.Add(root);
    }

    // Open at a comfortable size that still fits the monitor the window lands on.
    private void SizeToScreen()
    {
        var wanted = LogicalToDeviceUnits(new Size(540, 760));
        var work = WorkingArea();
        ClientSize = new Size(
            Math.Min(wanted.Width, work.Width - LogicalToDeviceUnits(80)),
            Math.Min(wanted.Height, work.Height - LogicalToDeviceUnits(80)));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyMinimumSize();
    }

    // The window must not be resizable small enough to hide a control, so the floor is measured
    // from the laid-out content at the narrowest width we support — it grows by itself with the
    // display scale and the system font size instead of being a hard-coded guess. Capped to the
    // monitor so the window always fits on screen; if the content genuinely can't fit, the activity
    // log (the last row, and the only one that can shrink) gives up space first.
    private void ApplyMinimumSize()
    {
        var narrow = LogicalToDeviceUnits(430);
        var needed = SizeFromClientSize(new Size(narrow, _root.GetPreferredSize(new Size(narrow, 0)).Height));
        var work = WorkingArea();
        MinimumSize = new Size(Math.Min(needed.Width, work.Width), Math.Min(needed.Height, work.Height));
    }

    private Rectangle WorkingArea() =>
        (IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position))?.WorkingArea
        ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);

    private void LoadConfigIntoUi()
    {
        _txtName.Text = _config.DisplayName;
        _txtPassphrase.Text = _config.GroupPassphrase;
        _txtWorld.Text = _config.WorldName;
        _txtWorldsFolder.Text = _config.WorldsFolder;
        _txtUrl.Text = _config.CoordinatorUrl;
        _chkAutoSave.Checked = _config.AutoSaveWhileHosting;
        _chkAutoLaunch.Checked = _config.AutoLaunchWhenReady;
    }

    private void SaveSettings()
    {
        _config.DisplayName = _txtName.Text.Trim();
        _config.GroupPassphrase = _txtPassphrase.Text;
        _config.WorldName = _txtWorld.Text.Trim();
        _config.WorldsFolder = _txtWorldsFolder.Text.Trim();
        _config.CoordinatorUrl = _txtUrl.Text.Trim();
        _config.AutoSaveWhileHosting = _chkAutoSave.Checked;
        _config.AutoLaunchWhenReady = _chkAutoLaunch.Checked;
        _config.Save();
        RebuildClient();
        Log("Settings saved.");
        _ = RefreshStateAsync();
    }

    private void RebuildClient() =>
        _client = string.IsNullOrWhiteSpace(_config.CoordinatorUrl) ? null : new CoordinatorClient(_config.CoordinatorUrl);

    // ---- Core actions ----

    private async Task HostAsync()
    {
        if (_busy) return;
        if (_client is null) { Warn("Set the coordinator URL first."); return; }
        if (string.IsNullOrWhiteSpace(_config.DisplayName) || string.IsNullOrWhiteSpace(_config.GroupPassphrase)
            || string.IsNullOrWhiteSpace(_config.WorldName))
        {
            Warn("Fill in your name, group passphrase, and world name, then Save settings.");
            return;
        }

        SetBusy(true);
        var worldReady = true;
        try
        {
            Log("Claiming the world lock…");
            var (token, version) = await _client.ClaimAsync(_config.DisplayName, _config.GroupPassphrase);
            _token = token;
            _baseVersion = version;

            var state = await _client.GetStateAsync();
            if (state.HasWorld)
            {
                Log("Downloading the latest world…");
                var tmp = await _client.DownloadToTempAsync(_config.GroupPassphrase);
                WorldFiles.ExtractInto(tmp, _config.WorldsFolder);
                try { File.Delete(tmp); } catch { }
                Log($"World v{version} downloaded into your Valheim folder.");
            }
            else if (WorldFiles.WorldExistsLocally(_config.WorldsFolder, _config.WorldName))
            {
                Log($"No world on the server yet — your local \"{_config.WorldName}\" will be uploaded as the starting world when you finish.");
            }
            else
            {
                worldReady = false;
                Warn($"No world on the server and no local \"{_config.WorldName}\" found in {_config.WorldsFolder}. " +
                     "Create the world in Valheim first, then click Stop hosting to upload it.");
            }

            _valheimSeenRunning = false;
            _tmrHeartbeat.Start();
            if (_config.AutoSaveWhileHosting) _tmrAutoSave.Start();
            Log("You're hosting. Launch Valheim, host the world, then share your join code.");
            if (worldReady && _config.AutoLaunchWhenReady) LaunchValheim();
        }
        catch (CoordinatorException ex)
        {
            _token = null;
            Warn($"Couldn't start hosting: {ex.Message}");
        }
        catch (Exception ex)
        {
            _token = null;
            Warn($"Couldn't start hosting: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshStateAsync();
        }
    }

    private async Task StopHostingAsync(bool auto)
    {
        if (_token is null || _busy) return;
        SetBusy(true);
        try
        {
            if (WorldFiles.ExistingFiles(_config.WorldsFolder, _config.WorldName).Any())
            {
                Log(auto ? "Valheim closed — saving the world…" : "Saving the world…");
                var zip = WorldFiles.CreateZip(_config.WorldsFolder, _config.WorldName);
                try
                {
                    var newVersion = await _client!.UploadAsync(_token, zip, finish: true, _baseVersion);
                    _baseVersion = newVersion;
                    _token = null;
                    Log($"Saved as v{newVersion} and released. Someone else can host now.");
                }
                finally { try { File.Delete(zip); } catch { } }
            }
            else
            {
                await _client!.ReleaseAsync(_token);
                _token = null;
                Log("Released the lock (no local world files were found to upload).");
            }
        }
        catch (Exception ex)
        {
            Warn($"Save/upload failed: {ex.Message}. You still hold the lock — fix the issue and click Stop hosting again.");
        }
        finally
        {
            if (_token is null) { _tmrHeartbeat.Stop(); _tmrAutoSave.Stop(); _valheimSeenRunning = false; }
            SetBusy(false);
            await RefreshStateAsync();
        }
    }

    private async Task AutoSaveAsync()
    {
        if (_token is null || _busy || !_config.AutoSaveWhileHosting) return;
        if (!IsValheimRunning()) return; // only autosave mid-session
        if (!WorldFiles.ExistingFiles(_config.WorldsFolder, _config.WorldName).Any()) return;

        SetBusy(true);
        try
        {
            var zip = WorldFiles.CreateZip(_config.WorldsFolder, _config.WorldName);
            try
            {
                var newVersion = await _client!.UploadAsync(_token, zip, finish: false, _baseVersion);
                _baseVersion = newVersion;
                Log($"Auto-saved (v{newVersion}).");
            }
            finally { try { File.Delete(zip); } catch { } }
        }
        catch (Exception ex) { Log($"Auto-save skipped: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async Task ShareJoinCodeAsync()
    {
        if (_token is null) return;
        var code = _txtJoinCode.Text.Trim();
        if (string.IsNullOrEmpty(code)) { Warn("Enter the join code from Valheim first."); return; }
        try { await _client!.SetJoinCodeAsync(_token, code); Log($"Shared join code {code} with the group."); }
        catch (Exception ex) { Warn($"Couldn't share join code: {ex.Message}"); }
    }

    private void LaunchValheim()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{ValheimSteamAppId}") { UseShellExecute = true });
            Log("Launching Valheim via Steam…");
        }
        catch (Exception ex) { Warn($"Couldn't launch Valheim: {ex.Message}"); }
    }

    // ---- Background loops ----

    private async Task HeartbeatAsync()
    {
        if (_token is null) return;
        try { await _client!.HeartbeatAsync(_token); }
        catch (CoordinatorException ex)
        {
            Log($"Lost the lock: {ex.Message}");
            _token = null; _tmrHeartbeat.Stop(); _tmrAutoSave.Stop();
            await RefreshStateAsync();
        }
        catch { /* transient network blip; try again next tick */ }
    }

    private async Task WatchValheimAsync()
    {
        if (_token is null) return;
        if (IsValheimRunning()) { _valheimSeenRunning = true; return; }
        if (_valheimSeenRunning)
        {
            _valheimSeenRunning = false;
            await StopHostingAsync(auto: true);
        }
    }

    private static bool IsValheimRunning() =>
        Process.GetProcessesByName("valheim").Length > 0;

    private async Task RefreshStateAsync()
    {
        if (_client is null) { _lblStatus.Text = "Set your coordinator URL"; UpdateButtons(); return; }
        try
        {
            var state = await _client.GetStateAsync();
            if (!_coordinatorReachable) { _coordinatorReachable = true; Log("Reconnected to the coordinator."); }

            // We thought we were hosting but the server shows the world free → our lease was lost.
            if (_token is not null && !state.Locked)
            {
                _token = null; _tmrHeartbeat.Stop(); _tmrAutoSave.Stop();
                Log("Your hosting lock expired or was released.");
            }
            var amHost = _token is not null && state.Locked;

            if (amHost)
            {
                _lblStatus.Text = "🎮 You are hosting";
                _lblStatus.ForeColor = Color.SeaGreen;
            }
            else if (state.Locked)
            {
                _lblStatus.Text = $"🔒 {state.HostName ?? "Someone"} is hosting";
                _lblStatus.ForeColor = Color.DarkOrange;
            }
            else if (state.HasWorld)
            {
                _lblStatus.Text = "✅ World is free — you can host";
                _lblStatus.ForeColor = Color.SeaGreen;
            }
            else
            {
                _lblStatus.Text = "🌱 No world yet — host to upload the first one";
                _lblStatus.ForeColor = Color.DimGray;
            }

            var saved = state.LastUpdatedAt is { } when ? $"  ·  saved {when.ToLocalTime():MMM d, h:mm tt}" : "";
            _lblDetail.Text = $"World v{state.Version}{saved}"
                + (state.Locked && state.SecondsUntilExpiry is { } s ? $"  ·  lease {s}s" : "");

            // Joiner sees the code; host gets the field to share one.
            var showJoiner = !amHost && state.Locked && !string.IsNullOrEmpty(state.JoinCode);
            var wasShowing = (_joinerPanel.Visible, _hostCodePanel.Visible);
            _joinerPanel.Visible = showJoiner;
            if (showJoiner) { _lblJoinCode.Text = $"Join code: {state.JoinCode}"; _lblJoinCode.Tag = state.JoinCode; }
            _hostCodePanel.Visible = amHost;
            // These rows appear after the window's minimum height was measured, so re-measure —
            // otherwise a small window would squeeze them out of the activity log's space.
            if (wasShowing != (showJoiner, amHost)) ApplyMinimumSize();

            UpdateButtons(state);
        }
        catch (Exception ex)
        {
            if (_coordinatorReachable) { _coordinatorReachable = false; Log($"Can't reach the coordinator: {ex.Message}"); }
            _lblStatus.Text = "⚠️ Coordinator unreachable";
            _lblStatus.ForeColor = Color.Firebrick;
            UpdateButtons();
        }
    }

    private void UpdateButtons(CoordinatorState? state = null)
    {
        var amHost = _token is not null;
        _btnHost.Enabled = !_busy && _client is not null && state is { Locked: false } && !amHost;
        _btnStop.Enabled = !_busy && amHost;
        // Launch/Share are disabled mid-sync (_busy): a stale world could be opened while it's
        // being overwritten by a download, and there's no code to share until hosting is settled.
        _btnLaunch.Enabled = amHost && !_busy;
        _btnShareCode.Enabled = amHost && !_busy;
    }

    // Centralizes the "a sync is in flight" state: button enablement + telling Windows not to
    // shut down/restart mid-upload (shown to the user with a reason).
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (IsHandleCreated)
        {
            if (busy) ShutdownBlockReasonCreate(Handle, "Syncing your Valheim world to the cloud — don't shut down yet.");
            else ShutdownBlockReasonDestroy(Handle);
        }
        UpdateButtons();
    }

    protected override void WndProc(ref Message m)
    {
        // Veto a shutdown/restart while a world sync is in progress (Windows shows our reason).
        if (m.Msg == WM_QUERYENDSESSION && _busy)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Your world is still syncing to the cloud. Please wait for it to finish before closing the app.",
                "Sync in progress", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_token is not null)
        {
            var r = MessageBox.Show(
                "You're still hosting. Save the world and release the lock before quitting?",
                "Still hosting", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
            {
                e.Cancel = true; // finish the save, then close for real
                _ = SaveThenCloseAsync();
            }
            // No → leave the lock; it will expire via the server lease.
        }
    }

    private async Task SaveThenCloseAsync()
    {
        await StopHostingAsync(auto: false);
        _tmrState.Stop();
        FormClosing -= OnClosing;
        Close();
    }

    // ---- Helpers ----

    private void Log(string message) =>
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");

    private void Warn(string message)
    {
        Log(message);
        MessageBox.Show(message, "Valheim World Keeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
