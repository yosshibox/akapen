namespace AkapenProbe.Ui;

internal static class UiContractTests
{
    public static void Run()
    {
        Assert(UiCommand.Name(UiCommandId.Save) == "document.save", "command IDs remain portable strings");
        foreach (UiCommandId command in Enum.GetValues<UiCommandId>())
        {
            Assert(!string.IsNullOrWhiteSpace(UiCommand.Name(command)), $"portable ID exists for {command}");
            Assert(!string.IsNullOrWhiteSpace(UiCommand.Shortcut(command)), $"shortcut exists for {command}");
            Assert(UiCommand.Tooltip(command).Contains(UiCommand.Name(command), StringComparison.Ordinal), $"tooltip contains ID for {command}");
        }
        Assert(UiCommand.Tooltip(UiCommandId.BrushSize).StartsWith("ブラシサイズ", StringComparison.Ordinal), "brush tooltip is explicit Japanese");
        Assert(UiCommand.Tooltip(UiCommandId.Color).StartsWith("色", StringComparison.Ordinal), "color tooltip is explicit Japanese");
        Assert(UiCommand.Tooltip(UiCommandId.Pressure).StartsWith("筆圧", StringComparison.Ordinal), "pressure tooltip is explicit Japanese");
        Assert(SvgIconCatalog.Count == Enum.GetValues<UiCommandId>().Length, "every portable command has a canonical SVG icon");
        foreach (UiCommandId command in Enum.GetValues<UiCommandId>())
        {
            SvgIcon icon = SvgIconCatalog.For(command);
            Assert(icon.Id == UiCommand.Name(command), $"SVG ID matches portable command {command}");
            Assert(!string.IsNullOrWhiteSpace(icon.PathData), $"SVG path exists for {command}");
            Assert(SvgPathParser.Parse(icon.PathData).Count > 0, $"SVG path parses for {command}");
        }
        Assert(SvgIconCatalog.ViewBoxSize == 24 && SvgIconCatalog.StrokeWidth == 1.75f, "SVG geometry contract is stable");
        Assert(UiSettingsStore.ParseDockSide("left") == DockSide.Left, "left is accepted");
        Assert(UiSettingsStore.ParseDockSide("unexpected") == DockSide.Right, "unknown side falls back right");
        var right = DockLayout.DockBounds(800, 600, DockSide.Right);
        var left = DockLayout.DockBounds(800, 600, DockSide.Left);
        Assert(right.X == 696 && right.Y == 0 && right.Width == 104, "tool dock uses the optimized narrow width on the complete right side");
        var rightCanvas = DockLayout.CanvasBounds(800, 600, DockSide.Right);
        var leftCanvas = DockLayout.CanvasBounds(800, 600, DockSide.Left);
        Assert(rightCanvas.X == 0 && rightCanvas.Width == 696 && rightCanvas.Height == 576, "loaded canvas ends before the right dock and above status");
        Assert(leftCanvas == rightCanvas, "legacy dock side preference no longer changes the canonical right-side geometry");
        Assert(!rightCanvas.Contains(right.X + 10, right.Y + 10), "canvas and dock do not overlap");
        var standardButtons = DockLayout.Buttons(800, 600, DockSide.Right);
        Assert(standardButtons.Select(b => b.Command).SequenceEqual(new[] { UiCommandId.Arrow, UiCommandId.Pen, UiCommandId.Eraser }), "loaded dock keeps no-op arrow, pen, and eraser buttons");
        Assert(!standardButtons.Any(b => b.Command is UiCommandId.Open or UiCommandId.Save or UiCommandId.Undo or UiCommandId.Redo or UiCommandId.Zoom), "document, history, and zoom buttons are absent from the dock");
        Assert(DockLayout.GroupFor(UiCommandId.Open) == DockGroup.Document && DockLayout.GroupFor(UiCommandId.Save) == DockGroup.Document, "document commands share a visual group");
        Assert(DockLayout.GroupFor(UiCommandId.Pen) == DockGroup.Tools && DockLayout.GroupFor(UiCommandId.Color) == DockGroup.Style, "tool hierarchy is explicit");
        Assert(DockLayout.GroupSeparators(800, 600, DockSide.Right).Count == 2, "tool, palette, and size regions have stable horizontal separators");
        var workspace = DockLayout.Workspace(1000, 600, DockSide.Right);
        Assert(workspace.Canvas.Right <= workspace.Dock.X, "the right dock never overlays the canvas");
        Assert(workspace.Status.Y == 576 && workspace.Status.Height == 24, "status bar mirrors the Mac 24-point status row");

        var palette = DockLayout.PaletteSwatches(800, 600, DockSide.Right);
        Assert(palette.Count == 10, "MS Paint palette has exactly ten permanent colors");
        Assert(palette.Select(item => item.Bounds.Y).Distinct().Count() == 5, "palette uses two symmetric columns in the narrow right dock");
        Assert(palette.Select(item => item.Rgba).Distinct().Count() == 10, "palette colors are visually distinct values");
        Assert(DockLayout.HitTestPalette(palette[7].Bounds.X + 2, palette[7].Bounds.Y + 2, 800, 600, DockSide.Right) == 7, "one click identifies a palette color");
        Assert(AkapenPalette.Colors[0].Name == "黒" && AkapenPalette.Colors[9].Name == "白", "fixed palette order has stable endpoint names");

        UiRect fader = DockLayout.BrushFaderBounds(800, 600, DockSide.Right);
        Assert(fader.Height >= 220 && fader.Width >= 80, "size control is a clearly vertical, easy-to-target fader");
        Assert(fader.Y <= standardButtons.Max(button => button.Bounds.Bottom) + 12, "size fader sits immediately below pen and eraser");
        Assert(DockLayout.PaletteSwatches(800, 600, DockSide.Right).Min(item => item.Bounds.Y) > fader.Bottom, "color palette sits immediately below the size fader");
        Assert(BrushFader.ValueFromY(fader.Y, fader) == BrushFader.Max, "size control top means maximum size");
        Assert(BrushFader.ValueFromY(fader.Bottom - 1, fader) == BrushFader.Min, "size control bottom means minimum size");
        Assert(!BrushFader.HasDownwardTriangleCap && BrushFader.HasLargeRail, "size control has a substantial symmetric rail and no meaningless triangle cap");
        Assert(!BrushFader.HasKnob && !BrushFader.HasTickMarks && BrushFader.UsesSymmetricFanFill, "size picker is a clean symmetric fan without a knob or percentage ticks");
        UiRect fanTrack = BrushFader.TrackBounds(fader);
        Assert(BrushFader.ValueFromY(fanTrack.Y, fanTrack) == BrushFader.Max && BrushFader.ValueFromY(fanTrack.Bottom - 1, fanTrack) == BrushFader.Min, "click position maps directly across the visible fan");
        Assert(ResizeContract.RefitsImageOnEveryWindowResize, "window resize dynamically refits the image");
        Assert(SequenceNavigation.DirectionForVirtualKey(0x25) == -1, "left arrow selects the previous image");
        Assert(SequenceNavigation.DirectionForVirtualKey(0x27) == 1, "right arrow selects the next image");
        Assert(SequenceNavigation.DirectionForVirtualKey(0x41) == 0, "unrelated keys do not navigate images");
        Assert(KeyboardShortcut.Resolve(0x50, false, false, false) == UiCommandId.Pen, "P selects pen");
        Assert(KeyboardShortcut.Resolve(0x45, false, false, false) == UiCommandId.Eraser, "E selects eraser");
        Assert(KeyboardShortcut.Resolve(0x5A, true, true, false) == UiCommandId.Redo, "Ctrl+Shift+Z performs redo as specified");
        Assert(KeyboardShortcut.Resolve(0x30, true, false, false) == UiCommandId.ViewFit, "Ctrl+0 fits the image");
        Assert(KeyboardShortcut.Resolve(0x30, true, false, true) == UiCommandId.Actual, "Ctrl+Alt+0 selects 100 percent");
        Assert(ShortcutContract.WindowsUsesSharedRustKeymap, "Windows dispatches CSP shortcuts through the shared Rust keymap");
        Assert(ImagePreloadPlan.Window(new[] { "a", "b", "c", "d" }, 1, true, 2, 1).SequenceEqual(new[] { "c", "d", "a" }), "forward navigation prioritizes upcoming animation frames");
        Assert(ImagePreloadPlan.Window(new[] { "a", "b", "c", "d" }, 1, false, 2, 1).SequenceEqual(new[] { "a", "d", "c" }), "backward navigation reverses the read-ahead priority");
        Assert(ImagePreloadPlan.Window(Enumerable.Range(0, 200).Select(i => i.ToString()).ToArray(), 100, true).Count == 144, "24 fps animation cache requests three seconds before and after the current frame");
        Assert(LaunchMode.IsProduct(Array.Empty<string>()), "normal launch uses product UI");
        Assert(LaunchMode.IsProduct(new[] { @"C:\frames\001.png" }), "opening an image path still uses the product UI and tool dock");
        Assert(!LaunchMode.IsProduct(new[] { "--presentation-smoke" }), "explicit developer smoke mode does not use product UI");
        var dpi200 = DockLayout.Workspace(1600, 1200, DockSide.Right, 2.0f);
        Assert(dpi200.Dock.Width == 208 && dpi200.Dock.Height == 1152 && dpi200.Status.Height == 48, "optimized right dock and status scale at 200 percent DPI");
        Assert(DockLayout.PaletteSwatches(1600, 1200, DockSide.Right, 2.0f).Select(item => item.Bounds.Y).Distinct().Count() == 5, "palette remains two columns at 200 percent DPI");
        Assert(DockLayout.BrushFaderBounds(1600, 1200, DockSide.Right, 2.0f).Width >= 160, "compact fader scales at 200 percent DPI");

        foreach (float dpi in new[] { 1.0f, 1.25f, 1.5f, 2.0f })
        {
            WorkspaceLayout scaled = DockLayout.Workspace((int)(1000 * dpi), (int)(700 * dpi), DockSide.Right, dpi);
            Assert(scaled.Canvas.Right == scaled.Dock.X && scaled.Canvas.Bottom == scaled.Status.Y, $"canvas, dock, and status tile exactly at {dpi:P0} DPI");
            UiRect scaledFader = DockLayout.BrushFaderBounds((int)(1000 * dpi), (int)(700 * dpi), DockSide.Right, dpi);
            Assert(scaled.Dock.Contains(scaledFader.X, scaledFader.Y) && scaledFader.Right <= scaled.Dock.Right && scaledFader.Bottom <= scaled.Dock.Bottom, $"fader stays inside dock at {dpi:P0} DPI");
        }
        Assert(PaintContract.SuppressEraseBackground && PaintContract.UsesMemoryBackBuffer && PaintContract.PaintsWholeClient, "Win32 full-frame paint contract prevents erase flicker");
        Assert(PaintContract.InvalidateOnlyOnVisualStateChange, "hover and control invalidation is state-change driven");
        Assert(MessagePumpContract.BlocksWhenNoDocument, "empty workspace blocks for messages instead of busy-spinning");
        var emptyPresentation = new WorkspacePresentation(WorkspacePhase.Empty);
        Assert(emptyPresentation.ShowsEmptyState && !emptyPresentation.ShowsCanvas && !emptyPresentation.ShowsToolDock && emptyPresentation.AcceptsImageDrop, "empty mirror shows only chooser/drop UI");
        var loadingPresentation = new WorkspacePresentation(WorkspacePhase.Loading);
        Assert(loadingPresentation.ShowsLoadingState && !loadingPresentation.ShowsCanvas && !loadingPresentation.AcceptsImageDrop, "loading mirror hides canvas and rejects duplicate drops");
        var loadedPresentation = new WorkspacePresentation(WorkspacePhase.Loaded);
        Assert(loadedPresentation.ShowsCanvas && loadedPresentation.ShowsToolDock && !loadedPresentation.ShowsEmptyState, "loaded mirror reveals canvas and minimal dock");
        var empty = new UiState();
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Save, empty).IsDisabled, "save is disabled without a document");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Undo, empty).IsDisabled, "undo is disabled without a document");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Redo, empty).IsDisabled, "redo is disabled without a document");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Open, empty).IsDisabled == false, "open is enabled from empty");
        var ready = new UiState(HasDocument: true, IsDirty: true, CanUndo: true, HasPrevious: true, HasNext: true);
        Assert(!UiCommandStateResolver.Resolve(UiCommandId.Save, ready).IsDisabled, "dirty document can be saved");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Save, ready).Badge == UiBadge.Dirty, "dirty save has a badge");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Pen, ready with { ActiveTool = UiCommandId.Pen }).IsSelected, "pen is selected");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Eraser, ready with { ActiveTool = UiCommandId.Eraser }).IsSelected, "eraser is selected");
        Assert(!UiCommandStateResolver.Resolve(UiCommandId.Next, ready).IsDisabled, "next is enabled with a candidate");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Previous, ready with { HasPrevious = false }).IsDisabled, "previous is disabled without a candidate");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Next, ready with { HasNext = false }).IsDisabled, "next is disabled without a candidate");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Pressure, ready with { IsDirty = false, HasWarning = true }).Badge == UiBadge.Warning, "pressure warning is confined to the pressure command");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Pen, ready with { HasWarning = true }).Badge == UiBadge.None, "tool buttons never show the unrelated orange warning dot");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Opacity, ready).IsDisabled, "opacity stays disabled in MVP");
        Assert(UiVisualState.Resolve(isDisabled: false, isSelected: false, isHovered: true, isPressed: false, hasFocus: false) == UiVisualKind.Hover, "hover has a distinct visual state");
        Assert(UiVisualState.Resolve(isDisabled: false, isSelected: true, isHovered: true, isPressed: false, hasFocus: false) == UiVisualKind.SelectedHover, "selected hover remains visibly selected");
        Assert(UiVisualState.Resolve(isDisabled: true, isSelected: true, isHovered: true, isPressed: true, hasFocus: true) == UiVisualKind.Disabled, "disabled styling wins over transient input states");
        Assert(UiVisualState.Resolve(isDisabled: false, isSelected: false, isHovered: false, isPressed: false, hasFocus: true) == UiVisualKind.Focus, "keyboard focus has an explicit style");
        var saving = ready with { IsSaving = true };
        foreach (var command in new[] { UiCommandId.Save, UiCommandId.Open, UiCommandId.Undo, UiCommandId.Redo, UiCommandId.Pen, UiCommandId.Eraser, UiCommandId.Pan, UiCommandId.Zoom, UiCommandId.Rotate, UiCommandId.BrushSize, UiCommandId.Previous, UiCommandId.Next })
            Assert(UiCommandStateResolver.Resolve(command, saving).IsDisabled, $"{command} is disabled while saving");
        Assert(UiCommandStateResolver.Resolve(UiCommandId.Save, saving).Badge == UiBadge.Busy, "saving has a busy badge");
        string path = Path.Combine(Path.GetTempPath(), "akapen-ui-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            UiSettings defaults = UiSettingsStore.Load(path);
            Assert(UiSettingsStore.ParseBackdrop(defaults.CanvasBackdrop) == CanvasBackdrop.White, "canvas outside-image backdrop defaults to white");
            Assert(defaults.AutoSaveOnNavigate, "frame navigation auto-save defaults on");
            Assert(UiSettingsStore.ParseSaveLocation(defaults.SaveLocationMode) == SaveLocationMode.SiblingSubfolder, "save target defaults to a sibling subfolder");
            Assert(defaults.OutputFolderName == "_review", "default output folder retains the established name");
            UiSettingsStore.Save(path, new UiSettings
            {
                CanvasBackdrop = "black", AutoSaveOnNavigate = false,
                SaveLocationMode = "customFolder", OutputFolderName = "checked",
                CustomOutputPath = @"C:\review-output",
            });
            UiSettings roundTrip = UiSettingsStore.Load(path);
            Assert(UiSettingsStore.ParseBackdrop(roundTrip.CanvasBackdrop) == CanvasBackdrop.Black, "black backdrop round-trips");
            Assert(!roundTrip.AutoSaveOnNavigate, "disabled navigation auto-save round-trips");
            Assert(UiSettingsStore.ParseSaveLocation(roundTrip.SaveLocationMode) == SaveLocationMode.CustomFolder, "custom save mode round-trips");
            Assert(roundTrip.OutputFolderName == "checked" && roundTrip.CustomOutputPath == @"C:\review-output", "save destination fields round-trip");
            UiSettingsStore.SaveDockSide(path, DockSide.Left);
            Assert(UiSettingsStore.LoadDockSide(path) == DockSide.Left, "dock side round-trips through JSON");
            Assert(File.ReadAllText(path).Contains("\"ui.dockSide\"", StringComparison.Ordinal), "JSON uses the shared ui.dockSide key");
            File.WriteAllText(path, "{\"ui.dockSide\":\"broken\"}");
            Assert(UiSettingsStore.LoadDockSide(path) == DockSide.Right, "broken value falls back right");
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

internal static class ReviewOutputLayoutTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "akapen-review-layout-" + Guid.NewGuid().ToString("N"));
        string sourceFolder = Path.Combine(root, "hoge");
        string imagePath = Path.Combine(sourceFolder, "frame-001.png");
        try
        {
            string sibling = ReviewOutputLayout.ResolveSiblingReviewDirectory(imagePath, "_review");
            Assert(sibling == Path.Combine(sourceFolder, "hoge_review"), "default review folder uses the source folder name");
            Assert(ReviewOutputLayout.ResolveSiblingReviewDirectory(imagePath, "checked") == Path.Combine(sourceFolder, "checked"), "custom review folder name is preserved");

            string transient = ReviewOutputLayout.ResolveTransientDirectory(sibling);
            Assert(transient == Path.Combine(sibling, "strokes"), "transient artifacts use a separate strokes folder");
            Assert(ReviewOutputLayout.IsArtifactForStem("frame-001.strokes.png", "frame-001", "strokes", ".png"), "stroke PNG belongs to the current stem");
            Assert(ReviewOutputLayout.IsArtifactForStem("frame-001-001.strokes.json", "frame-001", "strokes", ".json"), "collision-renamed stroke JSON belongs to the current stem");
            Assert(!ReviewOutputLayout.IsArtifactForStem("frame-002.strokes.json", "frame-001", "strokes", ".json"), "a different frame is not cleaned up");

            Directory.CreateDirectory(sibling);
            string first = ReviewOutputLayout.ResolveCollisionFreeFlatPath(sibling, "frame-001.review.png");
            File.WriteAllText(first, "existing");
            string second = ReviewOutputLayout.ResolveCollisionFreeFlatPath(sibling, "frame-001.review.png");
            Assert(second == Path.Combine(sibling, "frame-001-001.review.png"), "flat PNG collision gets a stable numeric suffix");

            string transientPng = Path.Combine(transient, "frame-001.strokes.png");
            string transientJson = Path.Combine(transient, "frame-001.strokes.json");
            Directory.CreateDirectory(transient);
            File.WriteAllText(transientPng, "png");
            File.WriteAllText(transientJson, "json");
            ReviewOutputLayout.DeleteTrackedArtifacts(new[] { transientPng, transientJson }, new[] { transient });
            Assert(!File.Exists(transientPng) && !File.Exists(transientJson), "tracked transient artifacts are deleted");
            Assert(!Directory.Exists(transient), "empty transient directory is deleted after cleanup");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
