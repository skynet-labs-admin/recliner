using Newtonsoft.Json;

namespace Recliner;

public class CustomNode
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("version")]
    public string Version { get; set; } = "unknown";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("git_hash")]
    public string? GitHash { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    // Display helpers (not serialized)
    [JsonIgnore]
    public string StatusLabel => Enabled ? "✓" : "✗";

    [JsonIgnore]
    public string StatusColor => Enabled ? "#4ec9b0" : "#f44747";
}

public class ComfyInstance
{
    // Not from JSON — set by the manifest service
    [JsonIgnore]
    public string DirectoryName { get; set; } = "";

    [JsonIgnore]
    public string WindowsPath { get; set; } = ""; // \\wsl$\Distro\...

    [JsonIgnore]
    public string WslPath { get; set; } = ""; // /home/...

    [JsonIgnore]
    public bool HasManifest { get; set; } = false;

    [JsonProperty("comfyui_version")]
    public string ComfyUIVersion { get; set; } = "unknown";

    [JsonProperty("pytorch_version")]
    public string PyTorchVersion { get; set; } = "unknown";

    [JsonProperty("torchvision_version")]
    public string TorchVisionVersion { get; set; } = "unknown";

    [JsonProperty("torchaudio_version")]
    public string TorchAudioVersion { get; set; } = "unknown";

    [JsonProperty("cuda_build")]
    public string CudaBuild { get; set; } = "unknown";

    [JsonProperty("port")]
    public int Port { get; set; } = 8188;

    [JsonProperty("output_folder")]
    public string OutputFolder { get; set; } = "";

    [JsonProperty("output_file_count")]
    public int OutputFileCount { get; set; } = -1;

    [JsonProperty("custom_nodes")]
    public List<CustomNode> CustomNodes { get; set; } = new();

    [JsonProperty("failed_imports")]
    public List<string> FailedImports { get; set; } = new();

    [JsonProperty("launch_command")]
    public string? LaunchCommand { get; set; }

    /// <summary>
    /// Extra CLI flags appended to start.sh at launch time.
    /// e.g. "--lowvram --preview-method auto"
    /// Stored in manifest.json, editable inline in the detail panel.
    /// </summary>
    [JsonProperty("extra_args")]
    public string ExtraArgs { get; set; } = "";

    [JsonProperty("generated_at")]
    public string? GeneratedAt { get; set; }

    // Display helpers
    [JsonIgnore]
    public string PortDisplay => $":{Port}";

    [JsonIgnore]
    public string CustomNodeCount => $"{CustomNodes.Count} node{(CustomNodes.Count != 1 ? "s" : "")}";

    [JsonIgnore]
    public string FailedImportCount => FailedImports.Count > 0
        ? $"{FailedImports.Count} failed"
        : "none";

    [JsonIgnore]
    public string OutputFileDisplay => OutputFileCount >= 0
        ? OutputFileCount.ToString("N0")
        : "—";

    [JsonIgnore]
    public string StatusSummary => HasManifest
        ? $"v{ComfyUIVersion} · port {Port}"
        : "Not configured — click Setup Environment";
}

public class AppSettings
{
    public string WslDistro           { get; set; } = "Ubuntu";
    public string WslParentPath       { get; set; } = "/home/skynetlab_user/ai-projects/comfyui";
    public string SharedModelsPath    { get; set; } = "/home/skynetlab_user/ai-projects/shared-models";
    public string SharedOutputPath    { get; set; } = "/home/skynetlab_user/ai-projects/shared-output";
    public string DefaultLaunchCommand { get; set; } = "python main.py";
    public string PreferredCudaTag    { get; set; } = "cu124";
    public string ComfyUIGitUrl       { get; set; } = "https://github.com/comfy-org/ComfyUI";
    public bool   OpenBrowserOnLaunch  { get; set; } = true;
    public string Theme               { get; set; } = Services.ThemeService.Default;
}
