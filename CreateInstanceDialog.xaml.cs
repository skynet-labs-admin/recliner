using System.Windows;
using System.Windows.Media;
using Recliner.Services;

namespace Recliner;

public enum CreateMode { Fresh, Clone }

public partial class CreateInstanceDialog : Window
{
    private readonly AppSettings _settings;
    private readonly List<ComfyInstance> _existingInstances;
    private CreateMode _mode = CreateMode.Fresh;

    // Set after successful creation
    public string? CreatedInstanceName { get; private set; }

    private static readonly (string Tag, string Label)[] CudaOptions =
    [
        ("cu128", "cu128  (CUDA 12.8 — latest)"),
        ("cu126", "cu126  (CUDA 12.6)"),
        ("cu124", "cu124  (CUDA 12.4 — RTX 30/40 series)"),
        ("cu121", "cu121  (CUDA 12.1)"),
        ("cu118", "cu118  (CUDA 11.8 — legacy)"),
        ("cpu",   "CPU    (no GPU acceleration)"),
    ];

    public CreateInstanceDialog(AppSettings settings, List<ComfyInstance> existingInstances)
    {
        InitializeComponent();
        _settings          = settings;
        _existingInstances = existingInstances;

        // Populate CUDA dropdown
        foreach (var (tag, label) in CudaOptions)
            CboCuda.Items.Add(label);
        CboCuda.SelectedIndex = Array.FindIndex(CudaOptions, x => x.Tag == settings.PreferredCudaTag);
        if (CboCuda.SelectedIndex < 0) CboCuda.SelectedIndex = 2; // default cu124

        // Populate source instance dropdown
        foreach (var inst in existingInstances)
            CboSource.Items.Add(inst.DirectoryName);
        if (CboSource.Items.Count > 0) CboSource.SelectedIndex = 0;

        TxtGitUrl.Text = settings.ComfyUIGitUrl;

        SetMode(CreateMode.Fresh);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Mode switching
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnModeNew_Click(object sender, RoutedEventArgs e)   => SetMode(CreateMode.Fresh);
    private void BtnModeClone_Click(object sender, RoutedEventArgs e) => SetMode(CreateMode.Clone);

    private void SetMode(CreateMode mode)
    {
        _mode = mode;

        var activeColor   = new SolidColorBrush(Color.FromRgb(0x1a, 0x3a, 0x5c));
        var activeBorder  = new SolidColorBrush(Color.FromRgb(0x4a, 0x9e, 0xff));
        var activeFg      = new SolidColorBrush(Color.FromRgb(0xe8, 0xe8, 0xe8));
        var inactiveFg    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var inactiveBg    = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
        var inactiveBorder= new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a));

        if (mode == CreateMode.Fresh)
        {
            BtnModeNew.Background   = activeColor;
            BtnModeNew.BorderBrush  = activeBorder;
            BtnModeNew.Foreground   = activeFg;
            BtnModeClone.Background = inactiveBg;
            BtnModeClone.BorderBrush= inactiveBorder;
            BtnModeClone.Foreground = inactiveFg;
            PanelSource.Visibility  = Visibility.Collapsed;
            PanelGitUrl.Visibility  = Visibility.Visible;
        }
        else
        {
            BtnModeClone.Background = activeColor;
            BtnModeClone.BorderBrush= activeBorder;
            BtnModeClone.Foreground = activeFg;
            BtnModeNew.Background   = inactiveBg;
            BtnModeNew.BorderBrush  = inactiveBorder;
            BtnModeNew.Foreground   = inactiveFg;
            PanelSource.Visibility  = Visibility.Visible;
            PanelGitUrl.Visibility  = Visibility.Collapsed;
        }

        UpdateInfoBox();
        Validate();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Validation + info box
    // ─────────────────────────────────────────────────────────────────────────

    private void TxtName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateInfoBox();
        Validate();
    }

    private void Validate()
    {
        string name = TxtName.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            TxtValidation.Text = "Instance name is required.";
            BtnCreate.IsEnabled = false;
            return;
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            TxtValidation.Text = "Name contains invalid characters.";
            BtnCreate.IsEnabled = false;
            return;
        }

        if (_existingInstances.Any(i => i.DirectoryName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            TxtValidation.Text = "An instance with that name already exists.";
            BtnCreate.IsEnabled = false;
            return;
        }

        if (_mode == CreateMode.Clone && CboSource.SelectedItem == null)
        {
            TxtValidation.Text = "Select a source instance to clone.";
            BtnCreate.IsEnabled = false;
            return;
        }

        TxtValidation.Text = "";
        BtnCreate.IsEnabled = true;
    }

    private void UpdateInfoBox()
    {
        string name     = TxtName.Text.Trim();
        string nameDisp = string.IsNullOrWhiteSpace(name) ? "<name>" : name;
        string parent   = _settings.WslParentPath;
        string models   = _settings.SharedModelsPath;
        string output   = _settings.SharedOutputPath;
        string cuda     = SelectedCudaTag();

        if (_mode == CreateMode.Fresh)
        {
            TxtInfoBox.Text =
                $"1. git clone ComfyUI → {parent}/{nameDisp}\n" +
                $"2. python3 -m venv venv\n" +
                $"3. pip install torch torchvision torchaudio ({cuda})\n" +
                $"4. pip install -r requirements.txt\n" +
                $"5. Create {models}/ subdirs (checkpoints, loras, vae …)\n" +
                $"6. Create output dir → {output}/{nameDisp}\n" +
                $"7. Write extra_model_paths.yaml";
        }
        else
        {
            string src = CboSource.SelectedItem?.ToString() ?? "<source>";
            TxtInfoBox.Text =
                $"1. rsync {parent}/{src}/ → {parent}/{nameDisp}/ (no venv)\n" +
                $"2. python3 -m venv venv\n" +
                $"3. pip freeze from source venv → reinstall\n" +
                $"   (falls back to fresh torch {cuda} if no source venv)\n" +
                $"4. Create output dir → {output}/{nameDisp}\n" +
                $"5. Write extra_model_paths.yaml (pointing at {models}/)";
        }
    }

    private string SelectedCudaTag()
    {
        int idx = CboCuda.SelectedIndex;
        return (idx >= 0 && idx < CudaOptions.Length) ? CudaOptions[idx].Tag : _settings.PreferredCudaTag;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Create
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        string name    = TxtName.Text.Trim();
        string cuda    = SelectedCudaTag();

        string script;
        if (_mode == CreateMode.Fresh)
        {
            script = ScriptBuilder.NewInstance(
                name,
                _settings.WslParentPath,
                _settings.SharedModelsPath,
                _settings.SharedOutputPath,
                cuda,
                TxtGitUrl.Text.Trim());
        }
        else
        {
            string source = CboSource.SelectedItem!.ToString()!;
            script = ScriptBuilder.CloneInstance(
                source, name,
                _settings.WslParentPath,
                _settings.SharedModelsPath,
                _settings.SharedOutputPath,
                cuda);
        }

        WslService.RunScript(script, _settings.WslDistro,
            $"RECLINER — {(_mode == CreateMode.Fresh ? "New" : "Clone")} {name}");

        CreatedInstanceName = name;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Called from MainWindow when cloning a specific instance.</summary>
    public void PreSelectCloneSource(string instanceName)
    {
        SetMode(CreateMode.Clone);
        CboSource.SelectedItem = instanceName;
    }
}
