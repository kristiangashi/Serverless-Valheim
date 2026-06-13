using System.Diagnostics;

namespace Helper;

public sealed class MainForm : Form
{
    private const string ValheimSteamAppId = "892970";

    private readonly AppConfig _config;
    private CoordinatorClient? _client;

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
    private readonly Button _btnSaveSettings = new() { Text = "Save settings", AutoSize = true };

    private readonly Label _lblStatus = new() { AutoSize = false, Height = 32, Font = new Font("Segoe UI", 13, FontStyle.Bold), Dock = DockStyle.Top };
    private readonly Label _lblDetail = new() { AutoSize = false, Height = 20, ForeColor = Color.DimGray, Dock = DockStyle.Top };

    private readonly Panel _joinerPanel = new() { Width = 430, Height = 30 };
    private readonly Label _lblJoinCode = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Location = new Point(0, 4) };
    private readonly Button _btnCopyCode = new() { Text = "Copy", AutoSize = true, Location = new Point(220, 0) };

    private readonly Panel _hostCodePanel = new() { Width = 430, Height = 32 };
    private readonly TextBox _txtJoinCode = new() { Width = 140, Location = new Point(0, 2), PlaceholderText = "join code" };
    private readonly Button _btnShareCode = new() { Text = "Share join code", AutoSize = true, Location = new Point(150, 0) };

    private readonly Button _btnHost = new() { Text = "Host this world", Width = 140, Height = 36 };
    private readonly Button _btnLaunch = new() { Text = "Launch Valheim", Width = 130, Height = 36 };
    private readonly Button _btnStop = new() { Text = "Stop hosting (save & release)", Width = 220, Height = 36 };

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
        Width = 480;
        Height = 660;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);

        BuildUi();
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
        new() { Width = 280, UseSystemPasswordChar = password };

    private void BuildUi()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(12),
        };

        var settings = new GroupBox { Text = "Settings", Width = 430, Height = 250 };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control input)
        {
            grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            input.Dock = DockStyle.Fill;
            grid.Controls.Add(input);
        }
        Row("Your name", _txtName);
        Row("Group passphrase", _txtPassphrase);
        Row("World name", _txtWorld);
        Row("Worlds folder", _txtWorldsFolder);
        Row("Coordinator URL", _txtUrl);
        grid.RowCount++;
        grid.Controls.Add(_chkAutoSave);
        grid.SetColumnSpan(_chkAutoSave, 2);
        grid.RowCount++;
        grid.Controls.Add(_btnSaveSettings);
        settings.Controls.Add(grid);

        _joinerPanel.Controls.Add(_lblJoinCode);
        _joinerPanel.Controls.Add(_btnCopyCode);
        _hostCodePanel.Controls.Add(_txtJoinCode);
        _hostCodePanel.Controls.Add(_btnShareCode);

        var statusPanel = new Panel { Width = 430, Height = 56 };
        statusPanel.Controls.Add(_lblDetail);
        statusPanel.Controls.Add(_lblStatus);

        var buttons = new FlowLayoutPanel { Width = 430, Height = 48, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        buttons.Controls.Add(_btnHost);
        buttons.Controls.Add(_btnLaunch);
        var buttons2 = new FlowLayoutPanel { Width = 430, Height = 48, FlowDirection = FlowDirection.LeftToRight };
        buttons2.Controls.Add(_btnStop);

        var logLabel = new Label { Text = "Activity", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var logHost = new Panel { Width = 430, Height = 150 };
        logHost.Controls.Add(_txtLog);

        root.Controls.Add(settings);
        root.Controls.Add(statusPanel);
        root.Controls.Add(_joinerPanel);
        root.Controls.Add(_hostCodePanel);
        root.Controls.Add(buttons);
        root.Controls.Add(buttons2);
        root.Controls.Add(logLabel);
        root.Controls.Add(logHost);
        Controls.Add(root);
    }

    private void LoadConfigIntoUi()
    {
        _txtName.Text = _config.DisplayName;
        _txtPassphrase.Text = _config.GroupPassphrase;
        _txtWorld.Text = _config.WorldName;
        _txtWorldsFolder.Text = _config.WorldsFolder;
        _txtUrl.Text = _config.CoordinatorUrl;
        _chkAutoSave.Checked = _config.AutoSaveWhileHosting;
    }

    private void SaveSettings()
    {
        _config.DisplayName = _txtName.Text.Trim();
        _config.GroupPassphrase = _txtPassphrase.Text;
        _config.WorldName = _txtWorld.Text.Trim();
        _config.WorldsFolder = _txtWorldsFolder.Text.Trim();
        _config.CoordinatorUrl = _txtUrl.Text.Trim();
        _config.AutoSaveWhileHosting = _chkAutoSave.Checked;
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

        _busy = true; UpdateButtons();
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
                Warn($"No world on the server and no local \"{_config.WorldName}\" found in {_config.WorldsFolder}. " +
                     "Create the world in Valheim first, then click Stop hosting to upload it.");
            }

            _valheimSeenRunning = false;
            _tmrHeartbeat.Start();
            if (_config.AutoSaveWhileHosting) _tmrAutoSave.Start();
            Log("You're hosting. Launch Valheim, host the world, then share your join code.");
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
            _busy = false;
            await RefreshStateAsync();
        }
    }

    private async Task StopHostingAsync(bool auto)
    {
        if (_token is null || _busy) return;
        _busy = true; UpdateButtons();
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
            _busy = false;
            await RefreshStateAsync();
        }
    }

    private async Task AutoSaveAsync()
    {
        if (_token is null || _busy || !_config.AutoSaveWhileHosting) return;
        if (!IsValheimRunning()) return; // only autosave mid-session
        if (!WorldFiles.ExistingFiles(_config.WorldsFolder, _config.WorldName).Any()) return;

        _busy = true;
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
        finally { _busy = false; }
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

            _lblDetail.Text = $"World v{state.Version}"
                + (state.Locked && state.SecondsUntilExpiry is { } s ? $"  ·  lease {s}s" : "");

            // Joiner sees the code; host gets the field to share one.
            var showJoiner = !amHost && state.Locked && !string.IsNullOrEmpty(state.JoinCode);
            _joinerPanel.Visible = showJoiner;
            if (showJoiner) { _lblJoinCode.Text = $"Join code: {state.JoinCode}"; _lblJoinCode.Tag = state.JoinCode; }
            _hostCodePanel.Visible = amHost;

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
        _btnLaunch.Enabled = amHost;
        _btnShareCode.Enabled = amHost;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
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
