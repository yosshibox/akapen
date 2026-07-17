using System.Text.Json;
using System.Text.Json.Serialization;

namespace AkapenProbe.Ui;

public enum CanvasBackdrop { White, Black }
public enum SaveLocationMode { SiblingSubfolder, SourceFolder, CustomFolder }

public sealed class UiSettings
{
    [JsonPropertyName("ui.dockSide")] public string? DockSide { get; set; }
    [JsonPropertyName("ui.canvasBackdrop")] public string CanvasBackdrop { get; set; } = "white";
    [JsonPropertyName("navigation.autoSave")] public bool AutoSaveOnNavigate { get; set; } = true;
    [JsonPropertyName("save.locationMode")] public string SaveLocationMode { get; set; } = "siblingSubfolder";
    [JsonPropertyName("save.folderName")] public string OutputFolderName { get; set; } = "_review";
    [JsonPropertyName("save.customPath")] public string CustomOutputPath { get; set; } = "";
    [JsonPropertyName("keymap.preset")] public string KeymapPreset { get; set; } = "photoshop";
}

public static class UiSettingsStore
{
    public static string DefaultPath(string? localAppData = null) =>
        Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Akapen", "settings.json");

    public static DockSide ParseDockSide(string? value) =>
        string.Equals(value, "left", StringComparison.OrdinalIgnoreCase) ? DockSide.Left : DockSide.Right;

    public static CanvasBackdrop ParseBackdrop(string? value) =>
        string.Equals(value, "black", StringComparison.OrdinalIgnoreCase) ? CanvasBackdrop.Black : CanvasBackdrop.White;

    public static KeymapPresetKind ParseKeymapPreset(string? value) =>
        string.Equals(value, "clipstudio", StringComparison.OrdinalIgnoreCase)
            ? KeymapPresetKind.ClipStudio
            : KeymapPresetKind.Photoshop;

    public static string KeymapPresetName(KeymapPresetKind kind) =>
        kind == KeymapPresetKind.ClipStudio ? "clipstudio" : "photoshop";

    public static SaveLocationMode ParseSaveLocation(string? value) => value switch
    {
        "sourceFolder" => SaveLocationMode.SourceFolder,
        "customFolder" => SaveLocationMode.CustomFolder,
        _ => SaveLocationMode.SiblingSubfolder,
    };

    public static UiSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings(); }
        catch (IOException) { return new UiSettings(); }
        catch (JsonException) { return new UiSettings(); }
    }

    public static DockSide LoadDockSide(string path) => ParseDockSide(Load(path).DockSide);

    public static void Save(string path, UiSettings settings)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    public static void SaveDockSide(string path, DockSide side)
    {
        UiSettings settings = Load(path);
        settings.DockSide = side == DockSide.Left ? "left" : "right";
        Save(path, settings);
    }
}
