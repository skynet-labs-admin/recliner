namespace Recliner;

public class CustomNode
{
    public string Name    { get; set; } = "";
    public string Version { get; set; } = "unknown";
    public bool   Enabled { get; set; } = true;

    public string StatusLabel => Enabled ? "✓" : "✗";
    public string StatusColor => Enabled ? "#4ec9b0" : "#f44747";
}

public class ComfyInstance
{
    public string DirectoryName     { get; set; } = "";
    public string WindowsPath       { get; set; } = "";
    public string WslPath           { get; set; } = "";

    public string ComfyUIVersion    { get; set; } = "—";
    public string PyTorchVersion    { get; set; } = "—";
    public string TorchVisionVersion { get; set; } = "—";
    public string TorchAudioVersion  { get; set; } = "—";
    public string CudaBuild         { get; set; } = "—";
    public int    Port              { get; set; } = 8188;
    public string OutputFolder      { get; set; } = "";
    public int    OutputFileCount   { get; set; } = -1;
    public string ExtraArgs         { get; set; } = "";

    public string? LaunchCommand    { get; set; }

    public List<CustomNode>  CustomNodes   { get; set; } = new();
    public List<string>      FailedImports { get; set; } = new();

    public string OutputFileDisplay => OutputFileCount >= 0
        ? OutputFileCount.ToString("N0") : "—";

    public string StatusSummary => ComfyUIVersion != "—"
        ? $"v{ComfyUIVersion} · port {Port}"
        : $"port {Port}";
}

public class AppSettings
{
    public string WslDistro            { get; set; } = "Ubuntu";
    public string WslParentPath        { get; set; } = "/home/skynetlab_user/ai-projects/comfyui";
    public string SharedModelsPath     { get; set; } = "/home/skynetlab_user/ai-projects/shared-models";
    public string SharedOutputPath     { get; set; } = "/home/skynetlab_user/ai-projects/shared-output";
    public string DefaultLaunchCommand { get; set; } = "python main.py";
    public string PreferredCudaTag     { get; set; } = "cu124";
    public string ComfyUIGitUrl        { get; set; } = "https://github.com/comfy-org/ComfyUI";
    public bool   OpenBrowserOnLaunch  { get; set; } = true;
    public string Theme                { get; set; } = Services.ThemeService.Default;
}
