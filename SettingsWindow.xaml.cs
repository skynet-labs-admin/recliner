using System.Windows;
using Recliner.Services;

namespace Recliner;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    private string _selectedTheme;

    private static readonly (string Tag, string Label)[] CudaOptions =
    [
        ("cu128", "cu128  (CUDA 12.8 — latest)"),
        ("cu126", "cu126  (CUDA 12.6)"),
        ("cu124", "cu124  (CUDA 12.4 — RTX 30/40 series)"),
        ("cu121", "cu121  (CUDA 12.1)"),
        ("cu118", "cu118  (CUDA 11.8 — legacy)"),
        ("cpu",   "CPU    (no GPU acceleration)"),
    ];

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Result = current;
        _selectedTheme = current.Theme;

        // Distros
        var distros = WslService.GetDistros();
        if (distros.Count == 0) distros.Add(current.WslDistro);
        foreach (var d in distros)
            CboDistro.Items.Add(d);
        CboDistro.SelectedItem = current.WslDistro;
        if (CboDistro.SelectedIndex < 0 && CboDistro.Items.Count > 0)
            CboDistro.SelectedIndex = 0;

        // CUDA options
        foreach (var (_, label) in CudaOptions)
            CboCuda.Items.Add(label);
        CboCuda.SelectedIndex = Array.FindIndex(CudaOptions, x => x.Tag == current.PreferredCudaTag);
        if (CboCuda.SelectedIndex < 0) CboCuda.SelectedIndex = 2;

        TxtParent.Text       = current.WslParentPath;
        TxtSharedModels.Text = current.SharedModelsPath;
        TxtSharedOutput.Text = current.SharedOutputPath;
        TxtGitUrl.Text       = current.ComfyUIGitUrl;
        TxtLaunch.Text       = current.DefaultLaunchCommand;
        ChkOpenBrowser.IsChecked = current.OpenBrowserOnLaunch;

        // Reflect current theme
        UpdateThemeCheckmarks();
    }

    // ── Theme cards ───────────────────────────────────────────────────────────

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as System.Windows.Controls.Button)?.Tag?.ToString()
                     ?? ThemeService.Default;
        _selectedTheme = tag;
        ThemeService.Apply(tag);      // live preview
        UpdateThemeCheckmarks();
    }

    private void UpdateThemeCheckmarks()
    {
        TxtThemeDefaultCheck.Visibility     = _selectedTheme == ThemeService.Default
            ? Visibility.Visible : Visibility.Collapsed;
        TxtThemeRoseGoldCheck.Visibility    = _selectedTheme == ThemeService.RoseGold
            ? Visibility.Visible : Visibility.Collapsed;
        TxtThemeCottonCandyCheck.Visibility = _selectedTheme == ThemeService.CottonCandy
            ? Visibility.Visible : Visibility.Collapsed;

        // Highlight selected card border
        var accent  = System.Windows.Application.Current.Resources["Accent"]
                      as System.Windows.Media.Brush;
        var borderMid = System.Windows.Application.Current.Resources["BorderMid"]
                        as System.Windows.Media.Brush;

        BtnThemeDefault.BorderBrush     = _selectedTheme == ThemeService.Default
            ? accent : borderMid;
        BtnThemeRoseGold.BorderBrush    = _selectedTheme == ThemeService.RoseGold
            ? accent : borderMid;
        BtnThemeCottonCandy.BorderBrush = _selectedTheme == ThemeService.CottonCandy
            ? accent : borderMid;
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "";
        string currentWslPath = tag switch
        {
            "Parent" => TxtParent.Text.Trim(),
            "Models" => TxtSharedModels.Text.Trim(),
            "Output" => TxtSharedOutput.Text.Trim(),
            _        => ""
        };

        string distro = CboDistro.SelectedItem?.ToString() ?? "Ubuntu";

        string initialDir = "";
        if (!string.IsNullOrEmpty(currentWslPath))
        {
            string candidate = WslService.ToWindowsPath(currentWslPath, distro);
            if (System.IO.Directory.Exists(candidate))
                initialDir = candidate;
        }

        if (string.IsNullOrEmpty(initialDir))
        {
            string root = WslService.ToWindowsPath("/home", distro);
            initialDir = System.IO.Directory.Exists(root) ? root : "";
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = $"Select folder — {tag} path",
            InitialDirectory = initialDir,
            Multiselect      = false
        };

        if (dlg.ShowDialog(this) == true)
        {
            string wslPath = WslService.ToWslPath(dlg.FolderName, distro);
            switch (tag)
            {
                case "Parent": TxtParent.Text       = wslPath; break;
                case "Models": TxtSharedModels.Text = wslPath; break;
                case "Output": TxtSharedOutput.Text = wslPath; break;
            }
        }
    }

    // ── Save / Cancel ─────────────────────────────────────────────────────────

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        int cudaIdx = CboCuda.SelectedIndex;
        string cudaTag = (cudaIdx >= 0 && cudaIdx < CudaOptions.Length)
            ? CudaOptions[cudaIdx].Tag
            : Result.PreferredCudaTag;

        Result = new AppSettings
        {
            WslDistro            = CboDistro.SelectedItem?.ToString() ?? Result.WslDistro,
            WslParentPath        = TxtParent.Text.Trim(),
            SharedModelsPath     = TxtSharedModels.Text.Trim(),
            SharedOutputPath     = TxtSharedOutput.Text.Trim(),
            PreferredCudaTag     = cudaTag,
            ComfyUIGitUrl        = TxtGitUrl.Text.Trim(),
            DefaultLaunchCommand = TxtLaunch.Text.Trim(),
            OpenBrowserOnLaunch  = ChkOpenBrowser.IsChecked == true,
            Theme                = _selectedTheme
        };
        // Create shared output folder if it doesn't exist yet
        if (!string.IsNullOrWhiteSpace(Result.SharedOutputPath))
            WslService.RunSilent(
                $"mkdir -p \"{Result.SharedOutputPath}\"",
                Result.WslDistro);

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // Revert any live theme preview back to original
        ThemeService.Apply(Result.Theme);
        DialogResult = false;
    }
}
