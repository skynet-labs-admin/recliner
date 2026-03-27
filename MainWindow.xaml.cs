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

    // ── Live manifest watcher ────────────────────────────────────────────────
    // Polls every 2 seconds. When manifest.json changes on disk the detail
    // panel updates automatically — no Refresh click needed.
    private DispatcherTimer? _watchTimer;
    private readonly Dictionary<string, DateTime> _manifestTimes = new();

    // Tracks which instances have had a silent manifest regen queued this session
    // so we never fire more than once per instance per app launch.
    private readonly HashSet<string> _regenQueued = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        LoadWindowIcon();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _watchTimer?.Stop();
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

            // Seed watcher times so existing files don't trigger false reloads
            SeedManifestTimes();

            // Silently heal any instances whose manifest exists but has stale
            // "unknown" runtime values — happens when an old/broken setup script
            // wrote the manifest before torch was installed or due to a Python
            // SyntaxError in an earlier version of generate_manifest.py.
            AutoFixStaleManifests();

            // ── Step 3: specific, honest status ──────────────────────────────
            if (_instances.Count == 0)
            {
                SetStatus($"No subfolders found in {winParent}  ·  Each subfolder here is one instance");
            }
            else
            {
                int ready  = _instances.Count(i => i.HasManifest);
                int setup  = _instances.Count - ready;
                string msg = $"{_instances.Count} instance{(_instances.Count != 1 ? "s" : "")} found";
                if (ready > 0) msg += $"  ·  {ready} active";
                if (setup > 0) msg += $"  ·  {setup} need{(setup == 1 ? "s" : "")} Setup Environment";
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

    private void SeedManifestTimes()
    {
        foreach (var inst in _instances)
        {
            string mf = Path.Combine(inst.WindowsPath, "manifest.json");
            try
            {
                if (File.Exists(mf))
                    _manifestTimes[inst.DirectoryName] = File.GetLastWriteTimeUtc(mf);
            }
            catch { /* inaccessible — skip */ }
        }
    }

    /// <summary>
    /// Compares each manifest against the actual source files on disk.
    /// If anything diverges — for any reason, including manual edits —
    /// the manifest is silently regenerated. One invariant, no special cases.
    /// </summary>
    private void AutoFixStaleManifests()
    {
        foreach (var inst in _instances)
        {
            if (!inst.HasManifest) continue;
            if (_regenQueued.Contains(inst.DirectoryName)) continue;

            // Read ComfyUI version straight from the source file.
            // If it's null, ComfyUI isn't installed here — skip.
            string? sourceVersion = ManifestService.ReadComfyVersionFromSource(
                inst.WindowsPath);
            if (sourceVersion == null) continue;

            // Read torch version from venv site-packages (null = not installed).
            string? sourceTorch = ManifestService.ReadTorchVersionFromSource(
                inst.WindowsPath);

            // Strip build tag for comparison — manifest stores "2.6.0" display
            // value but source has "2.6.0+cu124".
            string sourceTorchBase = sourceTorch ?? "unknown";
            int plus = sourceTorchBase.IndexOf('+');
            if (plus > 0) sourceTorchBase = sourceTorchBase[..plus];

            bool comfyMismatch  = sourceVersion != inst.ComfyUIVersion;
            bool torchMismatch  = sourceTorch != null &&
                                  sourceTorchBase != inst.PyTorchVersion &&
                                  inst.PyTorchVersion == "unknown";

            if (!comfyMismatch && !torchMismatch) continue;

            _regenQueued.Add(inst.DirectoryName);

            string outPath = !string.IsNullOrEmpty(inst.OutputFolder)
                ? inst.OutputFolder
                : $"{inst.WslPath}/output";

            string script = ScriptBuilder.RegenerateManifest(inst.WslPath, outPath);
            WslService.RunSilent(script, _settings.WslDistro);
        }
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
        // ── Phase 1: detect deleted instance directories ──────────────────────
        var gone = _instances.Where(i => !Directory.Exists(i.WindowsPath)).ToList();
        foreach (var dead in gone)
        {
            _instances.Remove(dead);
            _manifestTimes.Remove(dead.DirectoryName);
            _regenQueued.Remove(dead.DirectoryName);

            if (_selected?.DirectoryName == dead.DirectoryName)
            {
                _selected = null;
                PanelDetail.Visibility = Visibility.Collapsed;
                PanelEmpty.Visibility  = Visibility.Visible;
            }
        }
        if (gone.Any())
        {
            ListInstances.ItemsSource = null;
            ListInstances.ItemsSource = _instances;
            TxtCount.Text = _instances.Count.ToString();
            SetStatus($"↻  {gone[0].DirectoryName} — removed");
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

        // ── Phase 3: detect manifest changes in existing instances ────────────
        foreach (var inst in _instances.ToList())
        {
            string mf = Path.Combine(inst.WindowsPath, "manifest.json");
            DateTime newTime;
            try
            {
                if (!File.Exists(mf)) continue;
                newTime = File.GetLastWriteTimeUtc(mf);
            }
            catch { continue; }

            if (!_manifestTimes.TryGetValue(inst.DirectoryName, out var prevTime))
            {
                _manifestTimes[inst.DirectoryName] = newTime;
                continue;
            }

            if (newTime <= prevTime) continue;

            _manifestTimes[inst.DirectoryName] = newTime;
            AutoReloadInstance(inst);
        }
    }

    private void AutoReloadInstance(ComfyInstance stale)
    {
        var fresh = ManifestService.ReloadSingle(
            stale.DirectoryName, stale.WslPath, stale.WindowsPath, _settings.WslDistro);

        int idx = _instances.IndexOf(stale);
        if (idx >= 0)
            _instances[idx] = fresh;

        // Refresh the sidebar list binding
        ListInstances.Items.Refresh();
        TxtCount.Text = _instances.Count.ToString();

        // If this is the currently displayed instance, update the detail panel live
        if (_selected?.DirectoryName == stale.DirectoryName)
        {
            _selected = fresh;
            ShowInstance(fresh);
        }

        SetStatus($"↻  {fresh.DirectoryName} — manifest updated");
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

        if (inst.HasManifest)
        {
            BadgeHasManifest.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x40, 0x30));
            BadgeHasManifest.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x60, 0x45));
            TxtManifestBadge.Text = "✓ manifest.json";
            TxtManifestBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0xb0));
        }
        else
        {
            BadgeHasManifest.Background = new SolidColorBrush(Color.FromRgb(0x40, 0x30, 0x10));
            BadgeHasManifest.BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x48, 0x18));
            // Show the exact path RECLINER is looking at — no guessing
            string expectedManifest = Path.Combine(inst.WindowsPath, "manifest.json");
            bool pathReachable = Directory.Exists(inst.WindowsPath);
            TxtManifestBadge.Text = pathReachable
                ? $"⚠ No manifest.json at {inst.WindowsPath} — run Setup Environment"
                : $"⚠ Folder unreachable: {inst.WindowsPath} — check Settings";
            TxtManifestBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xdc, 0xc6, 0x8a));
        }

        TxtGeneratedAt.Text = string.IsNullOrEmpty(inst.GeneratedAt)
            ? "" : $"Generated {FormatDate(inst.GeneratedAt)}";

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

        // Update manifest.json — the watcher will auto-refresh the panel
        ManifestService.PatchPort(_selected.WindowsPath, port);

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
        ManifestService.PatchExtraArgs(_selected.WindowsPath, args);
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
        try
        {
            string outputWslPath = string.IsNullOrEmpty(_settings.SharedOutputPath)
                ? null!
                : $"{_settings.SharedOutputPath.TrimEnd('/')}/{_selected.DirectoryName}";

            WslService.LaunchComfyUI(
                _selected.WslPath,
                _settings.WslDistro,
                _selected.Port,
                _selected.LaunchCommand,
                _settings.DefaultLaunchCommand,
                outputWslPath,
                _selected.ExtraArgs);

            SetStatus($"Launched {_selected.DirectoryName} on port {_selected.Port}");

            if (_settings.OpenBrowserOnLaunch)
            {
                int port = _selected.Port;
                Task.Delay(3000).ContinueWith(_ =>
                    Process.Start(new ProcessStartInfo(
                        $"http://localhost:{port}") { UseShellExecute = true }));
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
