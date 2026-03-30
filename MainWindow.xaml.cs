using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recliner.Services;

namespace Recliner;

public partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private List<ComfyInstance> _instances = [];
    private ComfyInstance? _selected;

    // ── Live watcher ─────────────────────────────────────────────────────────
    // Polls every 2 seconds for new/removed directories and process state.
    private DispatcherTimer? _watchTimer;

    // ── Logo rotating taglines ───────────────────────────────────────────────
    private static readonly string[] _taglines =
    [
        "Limit blast radius",
        "Bringing peace where conflict once thrived",
        "Not related to Steiner Recliner",
        "ComfyUI runs finer with RECLINER",
        "All comfy, no crash"
    ];
    private int _taglineIndex = 0;
    private DispatcherTimer? _taglineTimer;

    // ── Running process tracker ──────────────────────────────────────────────
    // Keyed by port — pgrep/pkill use port as the discriminator.
    // Populated on launch, cleared on stop or when watcher detects process exit.
    private readonly HashSet<int> _runningPorts = new();
    // Terminal window process per instance — keyed by DirectoryName, not port,
    // so two instances that share a port don't clobber each other's entry.
    private readonly Dictionary<string, Process> _launchProcesses = new();
    private int _watcherTickCount = 0;

    // ── WSL UNC miss counter ─────────────────────────────────────────────────
    // WSL's \\wsl$ share goes momentarily unreachable during heavy I/O or
    // after wake-from-sleep. Don't evict instances on the first miss —
    // require 3 consecutive failures before treating a folder as gone.
    private readonly Dictionary<string, int> _missCounts = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        LoadWindowIcon();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => { _watchTimer?.Stop(); _taglineTimer?.Stop(); };
    }

    private void LoadWindowIcon()
    {
        try
        {
            var sri = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/recliner.ico"));
            if (sri?.Stream != null)
                Icon = BitmapFrame.Create(sri.Stream);
        }
        catch { }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusBar();
        LoadInstances();
        StartWatcher();
        StartTaglineRotator();
    }

    private void StartTaglineRotator()
    {
        _taglineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _taglineTimer.Tick += (_, _) =>
        {
            _taglineIndex = (_taglineIndex + 1) % _taglines.Length;
            BdrLogo.ToolTip = _taglines[_taglineIndex];
        };
        _taglineTimer.Start();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Loading
    // ─────────────────────────────────────────────────────────────────────────

    private void LoadInstances()
    {
        SetStatus("Scanning…");
        try
        {
            // ── Step 1: verify the parent folder is reachable via \\wsl$ ──────
            string winParent = WslService.ToWindowsPath(
                _settings.WslParentPath, _settings.WslDistro);

            if (!Directory.Exists(winParent))
            {
                _instances = [];
                ListInstances.ItemsSource = _instances;
                TxtCount.Text = "0";
                SetStatus($"Folder not found: {winParent}  ·  Open Settings to correct the path or distro name");
                TxtPathDisplay.Text = _settings.WslParentPath;
                UpdateStatusBar();
                return;
            }

            // ── Step 2: scan ─────────────────────────────────────────────────
            _instances = ManifestService.LoadInstances(_settings);
            ListInstances.ItemsSource = _instances;
            TxtCount.Text = _instances.Count.ToString();

            // ── Step 3: specific, honest status ──────────────────────────────
            if (_instances.Count == 0)
            {
                SetStatus($"No subfolders found in {winParent}  ·  Each subfolder here is one instance");
            }
            else
            {
                string msg = $"{_instances.Count} instance{(_instances.Count != 1 ? "s" : "")} found";
                SetStatus(msg);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Scan failed: {ex.Message}");
        }

        TxtPathDisplay.Text = _settings.WslParentPath;
        UpdateStatusBar();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Live watcher
    // ─────────────────────────────────────────────────────────────────────────

    private void StartWatcher()
    {
        _watchTimer?.Stop();
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += WatcherTick;
        _watchTimer.Start();
    }

    private void WatcherTick(object? sender, EventArgs e)
    {
        // ── Phase 0: check if any running ComfyUI processes have died ─────────
        _watcherTickCount++;
        if (_watcherTickCount % 5 == 0 && _runningPorts.Count > 0)
            CheckRunningProcesses();

        // ── Phase 1: detect deleted instance directories ──────────────────────
        // Require 3 consecutive misses before evicting — the \\wsl$ share can
        // go transiently unreachable during heavy I/O or wake-from-sleep.
        const int EvictThreshold = 3;
        var confirmed = new List<ComfyInstance>();
        foreach (var inst in _instances)
        {
            if (!Directory.Exists(inst.WindowsPath))
            {
                _missCounts.TryGetValue(inst.DirectoryName, out int misses);
                _missCounts[inst.DirectoryName] = misses + 1;
                if (misses + 1 >= EvictThreshold)
                    confirmed.Add(inst);
            }
            else
            {
                _missCounts.Remove(inst.DirectoryName); // reset on successful check
            }
        }
        foreach (var dead in confirmed)
        {
            _instances.Remove(dead);
            _missCounts.Remove(dead.DirectoryName);

            if (_selected?.DirectoryName == dead.DirectoryName)
            {
                _selected = null;
                PanelDetail.Visibility = Visibility.Collapsed;
                PanelEmpty.Visibility  = Visibility.Visible;
            }
        }
        if (confirmed.Any())
        {
            ListInstances.ItemsSource = null;
            ListInstances.ItemsSource = _instances;
            TxtCount.Text = _instances.Count.ToString();
            SetStatus($"↻  {confirmed[0].DirectoryName} — removed");
        }

        // ── Phase 2: detect new subdirectories in the parent folder ───────────
        try
        {
            string winParent = WslService.ToWindowsPath(
                _settings.WslParentPath, _settings.WslDistro);
            if (Directory.Exists(winParent))
            {
                var known = _instances
                    .Select(i => i.DirectoryName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                bool hasNew = Directory
                    .GetDirectories(winParent)
                    .Select(Path.GetFileName)
                    .Any(n => n != null && !n!.StartsWith('.') && !known.Contains(n));

                if (hasNew)
                {
                    string? selName = _selected?.DirectoryName;
                    LoadInstances();
                    if (selName != null)
                    {
                        var match = _instances.FirstOrDefault(
                            i => i.DirectoryName == selName);
                        if (match != null)
                        {
                            ListInstances.SelectedItem = match;
                            ShowInstance(match);
                        }
                    }
                    return; // LoadInstances already refreshed everything
                }
            }
        }
        catch { /* UNC not reachable — skip */ }

    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Selection
    // ─────────────────────────────────────────────────────────────────────────

    private void ListInstances_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ListInstances.SelectedItem is ComfyInstance inst)
            ShowInstance(inst);
    }

    private void ShowInstance(ComfyInstance inst)
    {
        _selected = inst;

        PanelEmpty.Visibility  = Visibility.Collapsed;
        PanelDetail.Visibility = Visibility.Visible;

        // ── Header ──────────────────────────────────────────────────────────
        TxtInstanceName.Text = inst.DirectoryName;

        // ── Runtime ─────────────────────────────────────────────────────────
        TxtComfyVersion.Text = inst.ComfyUIVersion;
        TxtPort.Text         = $"{inst.Port}";
        // Strip build tag (e.g. +cu124) — CUDA Build has its own field
        string torchDisplay = inst.PyTorchVersion;
        int torchPlus = torchDisplay.IndexOf('+');
        if (torchPlus > 0) torchDisplay = torchDisplay[..torchPlus];
        TxtPyTorch.Text      = torchDisplay;
        TxtCuda.Text         = inst.CudaBuild;
        string tvDisplay = inst.TorchVisionVersion;
        int tvPlus = tvDisplay.IndexOf('+');
        if (tvPlus > 0) tvDisplay = tvDisplay[..tvPlus];
        TxtTorchVision.Text  = tvDisplay;

        string taDisplay = inst.TorchAudioVersion;
        int taPlus = taDisplay.IndexOf('+');
        if (taPlus > 0) taDisplay = taDisplay[..taPlus];
        TxtTorchAudio.Text   = taDisplay;
        TxtOutputFolder.Text  = string.IsNullOrEmpty(inst.OutputFolder) ? "—" : inst.OutputFolder;
        TxtOutputCount.Text   = inst.OutputFileDisplay;
        TxtStartupArgs.Text   = inst.ExtraArgs;

        // ── Custom Nodes ─────────────────────────────────────────────────────
        TxtNodeCount.Text = inst.CustomNodes.Count.ToString();
        if (inst.CustomNodes.Count > 0)
        {
            GridNodes.Visibility  = Visibility.Visible;
            TxtNoNodes.Visibility = Visibility.Collapsed;
            GridNodes.ItemsSource = inst.CustomNodes;
        }
        else
        {
            GridNodes.Visibility  = Visibility.Collapsed;
            TxtNoNodes.Visibility = Visibility.Visible;
        }

        // ── Failed Imports ────────────────────────────────────────────────────
        if (inst.FailedImports.Count > 0)
        {
            PanelFailed.Visibility = Visibility.Visible;
            TxtFailedCount.Text    = inst.FailedImports.Count.ToString();
            ListFailed.ItemsSource = inst.FailedImports;
        }
        else
        {
            PanelFailed.Visibility = Visibility.Collapsed;
        }

        // Set button state from cached running set immediately, then confirm async
        UpdateLaunchButton(inst);
        int checkPort = inst.Port;
        Task.Run(() => WslService.IsComfyRunning(_settings.WslDistro, checkPort))
            .ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (t.Result) _runningPorts.Add(checkPort);
                    else          _runningPorts.Remove(checkPort);
                    if (_selected?.Port == checkPort)
                        UpdateLaunchButton(_selected);
                });
            });

        SetStatus($"Showing: {inst.DirectoryName}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Port inline editor
    // ─────────────────────────────────────────────────────────────────────────

    private void TxtPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePort();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_selected != null) TxtPort.Text = _selected.Port.ToString();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TxtPort_LostFocus(object sender, RoutedEventArgs e) => SavePort();

    private void SavePort()
    {
        if (_selected == null) return;
        if (!int.TryParse(TxtPort.Text.Trim(), out int port)
            || port < 1 || port > 65535)
        {
            TxtPort.Text = _selected.Port.ToString(); // revert invalid input silently
            return;
        }
        if (port == _selected.Port) return; // no change — nothing to do

        _selected.Port = port;

        // Persist to user.yaml (ComfyUI reads this on launch)
        WslService.WritePortConfig(_selected.WindowsPath, port);

        // Refresh sidebar subtitle immediately
        ListInstances.Items.Refresh();
        SetStatus($"Port set to {port} for {_selected.DirectoryName}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Startup-args inline editor
    // ─────────────────────────────────────────────────────────────────────────

    private void TxtStartupArgs_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveStartupArgs();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_selected != null) TxtStartupArgs.Text = _selected.ExtraArgs;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TxtStartupArgs_LostFocus(object sender, RoutedEventArgs e) => SaveStartupArgs();

    private void SaveStartupArgs()
    {
        if (_selected == null) return;
        string args = TxtStartupArgs.Text.Trim();
        if (args == _selected.ExtraArgs) return;

        _selected.ExtraArgs = args;
        ManifestService.WriteExtraArgs(_selected.WindowsPath, args);
        SetStatus(args.Length > 0
            ? $"Startup args saved for {_selected.DirectoryName}"
            : $"Startup args cleared for {_selected.DirectoryName}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Header buttons
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnNewInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateInstanceDialog(_settings, _instances) { Owner = this };
        if (dlg.ShowDialog() == true)
            SetStatus($"Setup launched for '{dlg.CreatedInstanceName}' — panel will auto-update on completion.");
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            SettingsService.Save(_settings);
            ThemeService.Apply(_settings.Theme);
            UpdateStatusBar();
            LoadInstances();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Detail panel buttons
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        // ── STOP ─────────────────────────────────────────────────────────────
        if (_runningPorts.Contains(_selected.Port))
        {
            int stopPort = _selected.Port;
            WslService.StopComfyUI(_settings.WslDistro, stopPort);
            _runningPorts.Remove(stopPort);
            // Close the terminal window that was opened for this instance
            if (_launchProcesses.TryGetValue(_selected.DirectoryName, out var termProc))
            {
                try { termProc.Kill(); } catch { }
                _launchProcesses.Remove(_selected.DirectoryName);
            }
            UpdateLaunchButton(_selected);
            SetStatus($"Stopped {_selected.DirectoryName} on port {stopPort}");
            return;
        }

        // ── LAUNCH ───────────────────────────────────────────────────────────
        try
        {
            string outputWslPath = string.IsNullOrEmpty(_settings.SharedOutputPath)
                ? null!
                : $"{_settings.SharedOutputPath.TrimEnd('/')}/{_selected.DirectoryName}";

            var termProc = WslService.LaunchComfyUI(
                _selected.WslPath,
                _settings.WslDistro,
                _selected.Port,
                _selected.LaunchCommand,
                _settings.DefaultLaunchCommand,
                outputWslPath,
                _selected.ExtraArgs,
                _settings.SharedModelsPath);

            if (termProc != null)
                _launchProcesses[_selected.DirectoryName] = termProc;
            _runningPorts.Add(_selected.Port);
            UpdateLaunchButton(_selected);
            SetStatus($"Launched {_selected.DirectoryName} on port {_selected.Port}");

            if (_settings.OpenBrowserOnLaunch)
            {
                int port = _selected.Port;
                Task.Run(async () =>
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(2);
                    var deadline = DateTime.UtcNow.AddSeconds(120);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            var resp = await http.GetAsync($"http://localhost:{port}");
                            if (resp.IsSuccessStatusCode) break;
                        }
                        catch { }
                        await Task.Delay(2000);
                    }
                    Process.Start(new ProcessStartInfo(
                        $"http://localhost:{port}") { UseShellExecute = true });
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch:\n{ex.Message}", "Launch Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSetupEnv_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var result = MessageBox.Show(
            $"RECLINER will install the required AI packages and configure " +
            $"'{_selected.DirectoryName}' for use.\n\n" +
            $"This may take several minutes. The panel updates automatically when done.",
            "Setup Environment",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        string script = ScriptBuilder.SetupEnvironment(
            _selected.DirectoryName,
            _selected.WslPath,
            _settings.SharedModelsPath,
            _settings.SharedOutputPath,
            _settings.PreferredCudaTag);

        WslService.RunScript(script, _settings.WslDistro,
            $"RECLINER — Setup {_selected.DirectoryName}");

        SetStatus($"Setup running for {_selected.DirectoryName} — panel will auto-update on completion.");
    }

    private void BtnCloneInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var dlg = new CreateInstanceDialog(_settings, _instances) { Owner = this };
        dlg.PreSelectCloneSource(_selected.DirectoryName);
        if (dlg.ShowDialog() == true)
            SetStatus($"Clone launched for '{dlg.CreatedInstanceName}' — panel will auto-update on completion.");
    }

    private void BtnOpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        string wslPath = _selected.WslPath;
        string script  =
            $"cd \"{wslPath}\" && " +
            $"bash --init-file <(echo '. ~/.bashrc; source \"{wslPath}/venv/bin/activate\"')";
        WslService.RunInTerminalPublic(
            _settings.WslDistro, script,
            $"RECLINER — {_selected.DirectoryName}");
    }

    private void BtnDeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var result = MessageBox.Show(
            $"This will permanently delete the instance and cannot be undone.\n\n" +
            $"Instance: {_selected.DirectoryName}\n\n" +
            $"Shared models and generated images will NOT be deleted.\n\n" +
            $"You will be asked to confirm by typing the instance name.",
            "Delete Instance",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        string script = ScriptBuilder.NukeInstance(_selected.WslPath, _selected.DirectoryName);
        WslService.RunScript(script, _settings.WslDistro,
            $"RECLINER — DELETE {_selected.DirectoryName}");

        _selected = null;
        PanelDetail.Visibility = Visibility.Collapsed;
        PanelEmpty.Visibility  = Visibility.Visible;
        SetStatus($"Delete terminal opened for {_selected?.DirectoryName ?? "instance"} — sidebar will update automatically.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    // ── Launch / Stop button state ────────────────────────────────────────────

    private void UpdateLaunchButton(ComfyInstance inst)
    {
        bool running = _runningPorts.Contains(inst.Port);
        BtnLaunch.Content = running ? "■  Stop ComfyUI" : "▶  Launch ComfyUI";
        BtnLaunch.Style   = running
            ? (Style)FindResource("StopBtn")
            : (Style)FindResource("LaunchBtn");
    }

    /// <summary>
    /// Async check: for each tracked running port, confirm the process is still
    /// alive in WSL. Called every 5 watcher ticks (~10 seconds).
    /// </summary>
    private void CheckRunningProcesses()
    {
        foreach (int port in _runningPorts.ToList())
        {
            int capturedPort = port;
            Task.Run(() => WslService.IsComfyRunning(_settings.WslDistro, capturedPort))
                .ContinueWith(t =>
                {
                    if (t.Result) return; // still alive — nothing to do
                    Dispatcher.Invoke(() =>
                    {
                        _runningPorts.Remove(capturedPort);
                        if (_selected?.Port == capturedPort)
                            UpdateLaunchButton(_selected);
                        SetStatus($"ComfyUI on port {capturedPort} has stopped");
                    });
                });
        }
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    private void UpdateStatusBar()
    {
        TxtDistro.Text     = $"WSL: {_settings.WslDistro}";
        TxtParentPath.Text = _settings.WslParentPath;
    }

    private static string FormatDate(string iso)
    {
        if (DateTime.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
        return iso;
    }
}
