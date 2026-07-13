namespace AkapenProbe.Ui;

public readonly record struct UiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public enum DockGroup { Document, History, Tools, Style, View, Sequence, System }

public readonly record struct DockButton(UiCommandId Command, UiRect Bounds, DockGroup Group);
public readonly record struct PaletteSwatch(int Index, uint Rgba, UiRect Bounds);
public readonly record struct WorkspaceLayout(UiRect Canvas, UiRect Dock, UiRect Status);
public readonly record struct EmptyStateLayout(UiRect FolderButton, UiRect DropTarget);

/// <summary>Pure geometry mirrored from the Mac loaded workspace.</summary>
public static class DockLayout
{
    public const int Width = 104;
    public const int ButtonSize = 28;
    public const int Padding = 12;
    public const int StatusHeight = 24;
    public const int SizeControlWidth = 88;
    public const int SizeControlHeight = 248;

    public static DockGroup GroupFor(UiCommandId command) => command switch
    {
        UiCommandId.Open or UiCommandId.Save => DockGroup.Document,
        UiCommandId.Undo or UiCommandId.Redo => DockGroup.History,
        UiCommandId.Arrow or UiCommandId.Pen or UiCommandId.Eraser or UiCommandId.Pan or UiCommandId.Zoom or UiCommandId.Rotate => DockGroup.Tools,
        UiCommandId.BrushSize or UiCommandId.Color or UiCommandId.Pressure or UiCommandId.Opacity => DockGroup.Style,
        UiCommandId.ViewFit or UiCommandId.Actual => DockGroup.View,
        UiCommandId.Previous or UiCommandId.Next => DockGroup.Sequence,
        UiCommandId.Settings => DockGroup.System,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public static WorkspaceLayout Workspace(int clientWidth, int clientHeight, DockSide side, float scale = 1)
    {
        int width = Math.Max(1, clientWidth);
        int statusHeight = Px(StatusHeight, scale);
        int statusY = Math.Max(1, clientHeight - statusHeight);
        int dockWidth = Math.Min(Px(Width, scale), Math.Max(1, width - 1));
        int dockX = Math.Max(1, width - dockWidth);
        return new WorkspaceLayout(
            new UiRect(0, 0, dockX, statusY),
            new UiRect(dockX, 0, width - dockX, statusY),
            new UiRect(0, statusY, width, statusHeight));
    }

    public static UiRect EmptyContentBounds(int clientWidth, int clientHeight, float scale = 1)
    {
        int status = Px(StatusHeight, scale);
        return new UiRect(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight - status));
    }

    public static EmptyStateLayout EmptyState(int clientWidth, int clientHeight, float scale = 1)
    {
        UiRect content = EmptyContentBounds(clientWidth, clientHeight, scale);
        int buttonW = Px(240, scale), buttonH = Px(40, scale);
        int dropW = Math.Min(Px(360, scale), Math.Max(Px(260, scale), content.Width - Px(48, scale)));
        int dropH = Px(128, scale);
        int titleBlockAboveButton = Px(82, scale);
        int groupHeight = titleBlockAboveButton + buttonH + Px(20, scale) + dropH;
        int top = content.Y + (content.Height - groupHeight) / 2 + titleBlockAboveButton;
        return new EmptyStateLayout(
            new UiRect(content.X + (content.Width - buttonW) / 2, top, buttonW, buttonH),
            new UiRect(content.X + (content.Width - dropW) / 2, top + buttonH + Px(20, scale), dropW, dropH));
    }

    public static UiRect DockBounds(int clientWidth, int clientHeight, DockSide side, float scale = 1) =>
        Workspace(clientWidth, clientHeight, side, scale).Dock;

    public static UiRect CanvasBounds(int clientWidth, int clientHeight, DockSide side, float scale = 1) =>
        Workspace(clientWidth, clientHeight, side, scale).Canvas;

    public static IReadOnlyList<DockButton> Buttons(int clientWidth, int clientHeight, DockSide side, float scale = 1)
    {
        UiRect dock = DockBounds(clientWidth, clientHeight, side, scale);
        int size = Px(ButtonSize, scale), gap = Px(6, scale);
        int y = dock.Y + Px(16, scale);
        int totalWidth = size * 3 + gap * 2;
        int x = dock.X + (dock.Width - totalWidth) / 2;
        return new[]
        {
            new DockButton(UiCommandId.Arrow, new UiRect(x, y, size, size), DockGroup.Tools),
            new DockButton(UiCommandId.Pen, new UiRect(x + size + gap, y, size, size), DockGroup.Tools),
            new DockButton(UiCommandId.Eraser, new UiRect(x + (size + gap) * 2, y, size, size), DockGroup.Tools),
        };
    }

    public static IReadOnlyList<PaletteSwatch> PaletteSwatches(int clientWidth, int clientHeight, DockSide side, float scale = 1)
    {
        UiRect dock = DockBounds(clientWidth, clientHeight, side, scale);
        int gap = Px(8, scale);
        int diameter = Px(24, scale);
        int totalWidth = diameter * 2 + gap;
        int startX = dock.X + (dock.Width - totalWidth) / 2;
        UiRect fader = BrushFaderBounds(clientWidth, clientHeight, side, scale);
        int startY = fader.Bottom + Px(28, scale);
        var result = new List<PaletteSwatch>(10);
        for (int i = 0; i < AkapenPalette.Colors.Count; i++)
            result.Add(new PaletteSwatch(i, AkapenPalette.Colors[i].Rgba,
                new UiRect(startX + (i % 2) * (diameter + gap), startY + (i / 2) * (diameter + gap), diameter, diameter)));
        return result;
    }

    public static int? HitTestPalette(int x, int y, int clientWidth, int clientHeight, DockSide side, float scale = 1) =>
        PaletteSwatches(clientWidth, clientHeight, side, scale).FirstOrDefault(item => item.Bounds.Contains(x, y)) is var hit && hit.Bounds.Width != 0
            ? hit.Index : null;

    public static UiRect BrushFaderBounds(int clientWidth, int clientHeight, DockSide side, float scale = 1)
    {
        UiRect dock = DockBounds(clientWidth, clientHeight, side, scale);
        int width = Px(SizeControlWidth, scale), height = Px(SizeControlHeight, scale);
        int x = dock.X + (dock.Width - width) / 2;
        int buttonBottom = Buttons(clientWidth, clientHeight, side, scale).Max(button => button.Bounds.Bottom);
        int y = buttonBottom + Px(10, scale);
        return new UiRect(x, y, width, Math.Min(height, Math.Max(Px(220, scale), dock.Bottom - y - Px(12, scale))));
    }

    /// <summary>Horizontal separator y-coordinates in the canonical right dock.</summary>
    public static IReadOnlyList<int> GroupSeparators(int clientWidth, int clientHeight, DockSide side, float scale = 1)
    {
        UiRect dock = DockBounds(clientWidth, clientHeight, side, scale);
        UiRect size = BrushFaderBounds(clientWidth, clientHeight, side, scale);
        return new[] { size.Y - Px(5, scale), size.Bottom + Px(14, scale) };
    }

    public static UiCommandId? HitTest(int x, int y, int clientWidth, int clientHeight, DockSide side, float scale = 1) =>
        Buttons(clientWidth, clientHeight, side, scale).FirstOrDefault(button => button.Bounds.Contains(x, y)) is var hit && hit.Bounds.Width != 0
            ? hit.Command : null;

    private static int Px(int dip, float scale) => Math.Max(1, (int)MathF.Round(dip * Math.Max(0.5f, scale)));
}
