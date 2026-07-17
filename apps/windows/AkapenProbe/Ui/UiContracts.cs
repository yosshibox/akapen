namespace AkapenProbe.Ui;

/// <summary>Cross-platform command vocabulary. The string is the portable contract.</summary>
public enum UiCommandId
{
    Open, Save, Undo, Redo, Arrow, Pen, Eraser, Pan, Zoom, Rotate,
    BrushSize, Color, Pressure, Opacity, ViewFit, Actual, Previous, Next, Settings
}

public static class UiCommand
{
    public static string Name(UiCommandId command) => command switch
    {
        UiCommandId.Open => "document.open",
        UiCommandId.Save => "document.save",
        UiCommandId.Undo => "history.undo",
        UiCommandId.Redo => "history.redo",
        UiCommandId.Arrow => "tool.arrow",
        UiCommandId.Pen => "tool.pen",
        UiCommandId.Eraser => "tool.eraser",
        UiCommandId.Pan => "tool.pan",
        UiCommandId.Zoom => "tool.zoom",
        UiCommandId.Rotate => "tool.rotate",
        UiCommandId.BrushSize => "style.size",
        UiCommandId.Color => "style.color",
        UiCommandId.Pressure => "style.pressure",
        UiCommandId.Opacity => "style.opacity",
        UiCommandId.ViewFit => "view.fit",
        UiCommandId.Actual => "view.actual",
        UiCommandId.Previous => "sequence.previous",
        UiCommandId.Next => "sequence.next",
        UiCommandId.Settings => "system.settings",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    // Tooltip shortcut hints follow the Photoshop preset (the V1.1 default
    // keymap). The CLIP STUDIO preset differences (Ctrl+Y redo, -/^ rotate,
    // R = rect) are documented in settings, not per-button tooltips.
    public static string Shortcut(UiCommandId command) => command switch
    {
        UiCommandId.Open => "Ctrl+O",
        UiCommandId.Save => "Ctrl+S",
        UiCommandId.Undo => "Ctrl+Z",
        UiCommandId.Redo => "Ctrl+Shift+Z",
        UiCommandId.Arrow => "A",
        UiCommandId.Pen => "B / P",
        UiCommandId.Eraser => "E",
        UiCommandId.Pan => "Space+drag",
        UiCommandId.Zoom => "↑ / ↓ / Ctrl+Space",
        UiCommandId.Rotate => "R / Shift+R（15°）",
        UiCommandId.BrushSize => "[ / ]",
        UiCommandId.Color => "X",
        UiCommandId.Pressure => "—",
        UiCommandId.Opacity => "—",
        UiCommandId.ViewFit => "Ctrl+0",
        UiCommandId.Actual => "Ctrl+1 / Ctrl+Alt+0",
        UiCommandId.Previous => "← / Page Up",
        UiCommandId.Next => "→ / Page Down",
        UiCommandId.Settings => "Ctrl+,",
        _ => string.Empty
    };

    public static string ShortLabel(UiCommandId command) => command switch
    {
        UiCommandId.Open => "Open",
        UiCommandId.Save => "Save",
        UiCommandId.Undo => "Undo",
        UiCommandId.Redo => "Redo",
        UiCommandId.Arrow => "Arrow",
        UiCommandId.Pen => "Pen",
        UiCommandId.Eraser => "Eraser",
        UiCommandId.Pan => "Pan",
        UiCommandId.Zoom => "Zoom",
        UiCommandId.Rotate => "Rotate",
        UiCommandId.BrushSize => "Brush size",
        UiCommandId.Color => "Color",
        UiCommandId.Pressure => "Pressure curve",
        UiCommandId.Opacity => "Opacity",
        UiCommandId.ViewFit => "Fit view",
        UiCommandId.Actual => "100%",
        UiCommandId.Previous => "Previous",
        UiCommandId.Next => "Next",
        UiCommandId.Settings => "Settings",
        _ => string.Empty
    };

    public static string Tooltip(UiCommandId command) => command switch
    {
        UiCommandId.BrushSize => $"ブラシサイズ（{Name(command)}、{Shortcut(command)}）",
        UiCommandId.Color => $"色（{Name(command)}、{Shortcut(command)}）",
        UiCommandId.Pressure => $"筆圧（{Name(command)}、{Shortcut(command)}）",
        _ => $"{ShortLabel(command)}（{Name(command)}、{Shortcut(command)}）",
    };
}

/// <summary>Single source of truth for keyboard shortcuts handled by the Windows shell.</summary>
public static class KeyboardShortcut
{
    public static UiCommandId? Resolve(int virtualKey, bool ctrl, bool shift, bool alt) => (virtualKey, ctrl, shift, alt) switch
    {
        (0x4F, true, _, _) => UiCommandId.Open,
        (0x53, true, _, _) => UiCommandId.Save,
        (0x5A, true, true, _) => UiCommandId.Redo,
        (0x5A, true, false, _) => UiCommandId.Undo,
        (0x59, true, _, _) => UiCommandId.Redo,
        (0x50, false, _, _) => UiCommandId.Pen,
        (0x45, false, _, _) => UiCommandId.Eraser,
        (0x30, true, _, true) => UiCommandId.Actual,
        (0x30, true, _, false) => UiCommandId.ViewFit,
        (0xBC, true, _, _) => UiCommandId.Settings,
        _ => null,
    };
}

public static class ShortcutContract
{
    public const bool WindowsUsesSharedRustKeymap = true;
}

public static class ImagePreloadPlan
{
    public static IReadOnlyList<string> Window(IReadOnlyList<string> paths, int currentIndex, bool forward, int ahead = 72, int behind = 72)
    {
        if (paths.Count < 2 || currentIndex < 0 || currentIndex >= paths.Count) return Array.Empty<string>();
        var result = new List<string>(Math.Min(paths.Count - 1, ahead + behind));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { paths[currentIndex] };
        void AddDirection(int direction, int count)
        {
            for (int offset = 1; offset <= count; offset++)
            {
                string path = paths[(currentIndex + direction * offset % paths.Count + paths.Count) % paths.Count];
                if (seen.Add(path)) result.Add(path);
            }
        }
        AddDirection(forward ? 1 : -1, ahead);
        AddDirection(forward ? -1 : 1, behind);
        return result;
    }
}

public static class LaunchMode
{
    public static bool IsProduct(IReadOnlyList<string> args) =>
        !args.Any(arg => arg is "--presentation-smoke" or "--interactive-smoke" or "--headless-export" or "--interactive" or "-i");
}

public enum DockSide { Right, Left }

public enum PressureCurve { Normal, Soft, Hard }

/// <summary>
/// Keymap preset (V1.1). Photoshop is the product default: shortcuts that
/// exist in Adobe Photoshop's default set are copied verbatim; Akapen-only
/// features keep their spec §3 keys. ClipStudio is the original spec §3 table.
/// The numeric values are the stable AKAPEN_KEYMAP_* codes of
/// <c>akapen_resolve_key_preset</c>.
/// </summary>
public enum KeymapPresetKind { ClipStudio = 0, Photoshop = 1 }

public readonly record struct PaletteColor(string Name, uint Rgba);

/// <summary>The fixed ten-color MS Paint-style palette shared by all shells.</summary>
public static class AkapenPalette
{
    public static readonly IReadOnlyList<PaletteColor> Colors = new[]
    {
        new PaletteColor("黒", 0x000000FFu),
        new PaletteColor("灰", 0x7F7F7FFFu),
        new PaletteColor("濃赤", 0x880015FFu),
        new PaletteColor("赤", 0xED1C24FFu),
        new PaletteColor("橙", 0xFF7F27FFu),
        new PaletteColor("黄", 0xFFF200FFu),
        new PaletteColor("緑", 0x22B14CFFu),
        new PaletteColor("水色", 0x00A2E8FFu),
        new PaletteColor("青", 0x3F48CCFFu),
        new PaletteColor("白", 0xFFFFFFFFu),
    };

    public static uint NormalizeRgba(uint rgba) => rgba | 0x000000FFu;
}

public enum BrushFaderKey { Up, Down, PageUp, PageDown, Home, End }

/// <summary>Portable interaction math for the permanent vertical brush fader.</summary>
public static class BrushFader
{
    public const float Min = 1.0f;
    public const float Max = 50.0f;
    public const bool HasDownwardTriangleCap = false;
    public const bool HasLargeRail = true;
    public const bool HasKnob = false;
    public const bool HasTickMarks = false;
    public const bool UsesSymmetricFanFill = true;

    public static bool IsValid(float value) => float.IsFinite(value) && value >= Min && value <= Max;
    public static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Min;

    public static float ValueFromY(int y, UiRect bounds)
    {
        if (bounds.Height <= 1) return Min;
        float ratio = Math.Clamp((y - bounds.Y) / (float)(bounds.Height - 1), 0, 1);
        return MathF.Round(Max - ratio * (Max - Min));
    }

    public static int YFromValue(float value, UiRect bounds)
    {
        float ratio = (Max - Clamp(value)) / (Max - Min);
        return bounds.Y + (int)MathF.Round(ratio * Math.Max(0, bounds.Height - 1));
    }

    public static UiRect TrackBounds(UiRect control, float scale = 1)
    {
        int insetTop = Math.Max(1, (int)MathF.Round(70 * scale));
        int insetBottom = Math.Max(1, (int)MathF.Round(32 * scale));
        return new UiRect(control.X, control.Y + insetTop, control.Width,
            Math.Max(1, control.Height - insetTop - insetBottom));
    }

    public static float Adjust(float value, BrushFaderKey key) => key switch
    {
        BrushFaderKey.Up => Clamp(value + 1),
        BrushFaderKey.Down => Clamp(value - 1),
        BrushFaderKey.PageUp => Clamp(value + 5),
        BrushFaderKey.PageDown => Clamp(value - 5),
        BrushFaderKey.Home => Min,
        BrushFaderKey.End => Max,
        _ => Clamp(value),
    };
}

public static class ResizeContract
{
    public const bool RefitsImageOnEveryWindowResize = true;
}

/// <summary>
/// Pure coordinate math for the Photoshop-style navigator (V1.1): where the
/// image thumbnail sits inside the navigator box, and the mapping between
/// thumbnail pixels, image pixels, and the canvas view transform. Shared by
/// the renderer (viewport rectangle) and the shell (click/drag → pan).
/// </summary>
public static class NavigatorMath
{
    /// <summary>The letterboxed placement of the image inside the box.</summary>
    public static UiRect ImagePlacement(UiRect box, uint imageW, uint imageH)
    {
        if (imageW == 0 || imageH == 0 || box.Width <= 0 || box.Height <= 0) return box;
        double scale = Math.Min(box.Width / (double)imageW, box.Height / (double)imageH);
        int w = Math.Max(1, (int)Math.Round(imageW * scale));
        int h = Math.Max(1, (int)Math.Round(imageH * scale));
        return new UiRect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    public static (double X, double Y) ThumbToImage(UiRect placement, uint imageW, uint imageH, int x, int y)
    {
        double ix = (x - placement.X) / Math.Max(1.0, placement.Width) * imageW;
        double iy = (y - placement.Y) / Math.Max(1.0, placement.Height) * imageH;
        return (Math.Clamp(ix, 0, imageW), Math.Clamp(iy, 0, imageH));
    }

    public static (float X, float Y) ImageToThumb(UiRect placement, uint imageW, uint imageH, double imageX, double imageY)
    {
        return ((float)(placement.X + imageX / Math.Max(1u, imageW) * placement.Width),
                (float)(placement.Y + imageY / Math.Max(1u, imageH) * placement.Height));
    }

    /// <summary>Canvas (client) point → image pixel, mirroring the shell's view transform.</summary>
    public static (double X, double Y) CanvasToImage(double x, double y, double canvasW, double canvasH,
        float panX, float panY, float zoom, float rotationDeg, uint imageW, uint imageH)
    {
        double cx = canvasW / 2.0 + panX, cy = canvasH / 2.0 + panY;
        double a = -rotationDeg * Math.PI / 180.0;
        double dx = (x - cx) / Math.Max(0.01f, zoom), dy = (y - cy) / Math.Max(0.01f, zoom);
        return (dx * Math.Cos(a) - dy * Math.Sin(a) + imageW / 2.0,
                dx * Math.Sin(a) + dy * Math.Cos(a) + imageH / 2.0);
    }

    /// <summary>
    /// The pan that centers the view on the given image pixel (used when the
    /// user clicks/drags inside the navigator thumbnail).
    /// </summary>
    public static (float PanX, float PanY) PanToCenterOn(double imageX, double imageY,
        uint imageW, uint imageH, float zoom, float rotationDeg)
    {
        double theta = rotationDeg * Math.PI / 180.0;
        double dx = imageX - imageW / 2.0, dy = imageY - imageH / 2.0;
        return ((float)(-zoom * (Math.Cos(theta) * dx - Math.Sin(theta) * dy)),
                (float)(-zoom * (Math.Sin(theta) * dx + Math.Cos(theta) * dy)));
    }
}

/// <summary>
/// Snapshot of everything the renderer needs to paint the navigator panel:
/// the cached thumbnail (top-down 32-bit BGRA rows, GDI+ order) and the live
/// view transform for the viewport rectangle.
/// </summary>
public sealed record NavigatorState(
    byte[]? ThumbBgra, int ThumbWidth, int ThumbHeight,
    uint ImageWidth, uint ImageHeight,
    int CanvasWidth, int CanvasHeight,
    float Zoom, float PanX, float PanY, float RotationDeg);

public enum WorkspacePhase { Empty, Loading, Loaded }

public static class SequenceNavigation
{
    public static int DirectionForVirtualKey(int virtualKey) => virtualKey switch
    {
        0x25 or 0x21 => -1, // Left or Page Up
        0x27 or 0x22 => 1,  // Right or Page Down
        _ => 0,
    };
}

public readonly record struct WorkspacePresentation(WorkspacePhase Phase)
{
    public bool ShowsEmptyState => Phase == WorkspacePhase.Empty;
    public bool ShowsLoadingState => Phase == WorkspacePhase.Loading;
    public bool ShowsCanvas => Phase == WorkspacePhase.Loaded;
    public bool ShowsToolDock => Phase == WorkspacePhase.Loaded;
    public bool AcceptsImageDrop => Phase != WorkspacePhase.Loading;
}

public static class PaintContract
{
    public const bool SuppressEraseBackground = true;
    public const bool UsesMemoryBackBuffer = true;
    public const bool PaintsWholeClient = true;
    public const bool InvalidateOnlyOnVisualStateChange = true;
}

public static class MessagePumpContract
{
    public const bool BlocksWhenNoDocument = true;
}


/// <summary>Small shell state shape kept independent from Rust's keymap/action enum.</summary>
public sealed record UiState(
    UiCommandId ActiveTool = UiCommandId.Pen,
    bool HasDocument = false,
    bool IsDirty = false,
    bool CanUndo = false,
    bool CanRedo = false,
    bool IsSaving = false,
    bool IsLoading = false,
    bool IsClosing = false,
    bool HasPrevious = false,
    bool HasNext = false,
    bool HasWarning = false,
    float Zoom = 1.0f,
    float BrushSize = 6.0f,
    uint Color = 0xFF0000FFu,
    PressureCurve Pressure = PressureCurve.Normal)
{
    public bool HasViewport => HasDocument && Zoom > 0;
}

public enum UiBadge
{
    None,
    Dirty,
    Warning,
    Busy,
}

public readonly record struct UiCommandState(bool IsDisabled, bool IsSelected, UiBadge Badge);

public enum UiVisualKind { Normal, Hover, Pressed, Selected, SelectedHover, Focus, Disabled }

public static class UiVisualState
{
    public static UiVisualKind Resolve(bool isDisabled, bool isSelected, bool isHovered, bool isPressed, bool hasFocus)
    {
        if (isDisabled) return UiVisualKind.Disabled;
        if (isPressed) return UiVisualKind.Pressed;
        if (isSelected && isHovered) return UiVisualKind.SelectedHover;
        if (isSelected) return UiVisualKind.Selected;
        if (isHovered) return UiVisualKind.Hover;
        if (hasFocus) return UiVisualKind.Focus;
        return UiVisualKind.Normal;
    }
}

/// <summary>Pure command-state policy shared by the native shell and contract tests.</summary>
public static class UiCommandStateResolver
{
    public static UiCommandState Resolve(UiCommandId command, UiState state)
    {
        bool saving = state.IsSaving;
        bool noDocument = !state.HasDocument;
        bool loadingOrClosing = state.IsLoading || state.IsClosing;
        bool selected = (command is UiCommandId.Arrow or UiCommandId.Pen or UiCommandId.Eraser or UiCommandId.Pan or UiCommandId.Zoom or UiCommandId.Rotate)
            && state.ActiveTool == command;
        UiBadge badge = command == UiCommandId.Save && saving ? UiBadge.Busy
            : command == UiCommandId.Save && state.IsDirty ? UiBadge.Dirty
            : command == UiCommandId.Pressure && state.HasWarning ? UiBadge.Warning
            : UiBadge.None;

        bool disabled = command switch
        {
            UiCommandId.Open => loadingOrClosing || saving,
            UiCommandId.Save => noDocument || state.IsLoading || state.IsClosing || saving,
            UiCommandId.Undo => noDocument || !state.CanUndo || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Redo => noDocument || !state.CanRedo || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Arrow or UiCommandId.Pen or UiCommandId.Eraser or UiCommandId.Pan or UiCommandId.Zoom or UiCommandId.Rotate => noDocument || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Opacity => true,
            UiCommandId.BrushSize or UiCommandId.Color or UiCommandId.Pressure or UiCommandId.ViewFit or UiCommandId.Actual => noDocument || !state.HasViewport || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Previous => noDocument || !state.HasPrevious || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Next => noDocument || !state.HasNext || saving || state.IsLoading || state.IsClosing,
            UiCommandId.Settings => state.IsClosing,
            _ => true,
        };
        return new UiCommandState(disabled, selected, badge);
    }
}
