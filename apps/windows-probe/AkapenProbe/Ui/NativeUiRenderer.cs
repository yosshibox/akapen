using System.Runtime.InteropServices;

namespace AkapenProbe.Ui;

/// <summary>
/// Windows-only high-quality UI renderer. GDI+ supplies antialiased vector paths
/// and ClearType text; the product UI no longer uses GDI TextOut or hand-drawn
/// per-command primitives. Canvas rendering remains owned by Akapen's renderer.
/// </summary>
internal sealed class NativeUiRenderer : IDisposable
{
    private const uint WindowSurface = 0xFFF4F2EF, DockSurface = 0xFFF7F5F2, StatusSurface = 0xFFEDE9E4;
    private const uint Text = 0xFF282522, Muted = 0xFF716B66, Border = 0xFFD6CFC8;
    private const uint Accent = 0xFFC84232, AccentSoft = 0xFFFFE3DC, Hover = 0xFFEDE7E1;
    private const uint Disabled = 0xFFAAA49E, White = 0xFFFFFFFF;

    private readonly IntPtr _graphics;
    private float _scale = 1;

    static NativeUiRenderer()
    {
        var input = new GdiplusStartupInput { Version = 1 };
        GdiplusStartup(out _, ref input, IntPtr.Zero);
    }

    public NativeUiRenderer(IntPtr hdc)
    {
        Check(GdipCreateFromHDC(hdc, out _graphics));
        GdipSetSmoothingMode(_graphics, 4); // AntiAlias
        GdipSetPixelOffsetMode(_graphics, 4); // Half-pixel alignment
        GdipSetTextRenderingHint(_graphics, 5); // ClearTypeGridFit
    }

    public void Paint(int width, int height, DockSide side, UiState state, UiCommandId? hovered,
                      string statusText, bool brushFaderFocused, float scale = 1)
    {
        _scale = Math.Max(0.5f, scale);
        Fill(new UiRect(0, 0, width, height), WindowSurface);
        if (!state.HasDocument)
        {
            DrawEmptyOrLoading(width, height, false);
            DrawStatus(width, height, statusText);
            return;
        }

        WorkspaceLayout workspace = DockLayout.Workspace(width, height, side, _scale);
        Fill(workspace.Dock, DockSurface);
        DrawLine(workspace.Dock.X, workspace.Dock.Y, workspace.Dock.X, workspace.Dock.Bottom, Border, PxF(1));

        foreach (int y in DockLayout.GroupSeparators(width, height, side, _scale))
            DrawLine(workspace.Dock.X + Px(16), y, workspace.Dock.Right - Px(16), y, Border, PxF(1));

        foreach (DockButton button in DockLayout.Buttons(width, height, side, _scale))
            DrawButton(button, UiCommandStateResolver.Resolve(button.Command, state), hovered == button.Command);

        int selectedColor = -1;
        foreach (PaletteSwatch swatch in DockLayout.PaletteSwatches(width, height, side, _scale))
        {
            if (swatch.Rgba == state.Color) selectedColor = swatch.Index;
            uint argb = RgbaToArgb(swatch.Rgba);
            FillEllipse(swatch.Bounds.X, swatch.Bounds.Y, swatch.Bounds.Width, swatch.Bounds.Height, argb);
            DrawEllipse(swatch.Bounds.X, swatch.Bounds.Y, swatch.Bounds.Width, swatch.Bounds.Height,
                swatch.Index == selectedColor ? Accent : Border, PxF(swatch.Index == selectedColor ? 3 : 1));
            if (swatch.Index == selectedColor && swatch.Bounds.Width >= Px(18))
                DrawEllipse(swatch.Bounds.X + Px(3), swatch.Bounds.Y + Px(3), swatch.Bounds.Width - Px(6), swatch.Bounds.Height - Px(6), White, PxF(1));
        }

        UiRect fader = DockLayout.BrushFaderBounds(width, height, side, _scale);
        DrawFader(fader, state.BrushSize, state.Color);
        DrawStatus(width, height, statusText);
    }

    private void DrawEmptyOrLoading(int width, int height, bool loading)
    {
        UiRect content = DockLayout.EmptyContentBounds(width, height, _scale);
        if (loading)
        {
            DrawTextCentered("画像を読み込んでいます…", new UiRect(content.X, content.Y + content.Height / 2 - Px(16), content.Width, Px(32)), 14, Muted);
            return;
        }

        EmptyStateLayout empty = DockLayout.EmptyState(width, height, _scale);
        int titleY = Math.Max(Px(38), empty.FolderButton.Y - Px(82));
        DrawTextCentered("Akapen", new UiRect(content.X, titleY, content.Width, Px(34)), 24, Text, bold: true);
        DrawTextCentered("画像フォルダを選ぶか、画像をドロップして始めます。",
            new UiRect(content.X, titleY + Px(38), content.Width, Px(24)), 13, Muted);

        Fill(empty.FolderButton, White);
        DrawRect(empty.FolderButton, Border, PxF(1));
        DrawSvg(SvgIconCatalog.For(UiCommandId.Open),
            new UiRect(empty.FolderButton.X + Px(18), empty.FolderButton.Y + Px(10), Px(20), Px(20)), Text, 1.6f);
        DrawText("画像を開くフォルダを選択", empty.FolderButton.X + Px(52), empty.FolderButton.Y + Px(10), 13, Text, bold: true);

        Fill(empty.DropTarget, 0xFFFAF9F7);
        DrawRect(empty.DropTarget, Border, PxF(1));
        DrawTextCentered("画像をここへドロップ", new UiRect(empty.DropTarget.X, empty.DropTarget.Y + Px(36), empty.DropTarget.Width, Px(28)), 15, Text, bold: true);
        DrawTextCentered("PNG・JPEG・WebP・BMP", new UiRect(empty.DropTarget.X, empty.DropTarget.Y + Px(68), empty.DropTarget.Width, Px(22)), 11, Muted);
    }

    private void DrawStatus(int width, int height, string text)
    {
        int statusHeight = Px(DockLayout.StatusHeight);
        int y = Math.Max(0, height - statusHeight);
        Fill(new UiRect(0, y, width, statusHeight), StatusSurface);
        DrawLine(0, y, width, y, Border, PxF(1));
        DrawText(text, Px(10), y + Px(4), 12, Text);
    }

    private void DrawButton(DockButton button, UiCommandState state, bool hovered)
    {
        UiRect rect = button.Bounds;
        if (state.IsSelected) { Fill(rect, AccentSoft); DrawRect(rect, Accent, 2); }
        else if (hovered && !state.IsDisabled) Fill(rect, Hover);
        uint color = state.IsDisabled ? Disabled : state.IsSelected ? Accent : Text;
        int iconSize = button.Group == DockGroup.Tools ? 24 : 21;
        var iconRect = new UiRect(rect.X + (rect.Width - iconSize) / 2, rect.Y + (rect.Height - iconSize) / 2, iconSize, iconSize);
        DrawSvg(SvgIconCatalog.For(button.Command), iconRect, color, state.IsDisabled ? 1.45f : SvgIconCatalog.StrokeWidth);
        if (state.Badge != UiBadge.None)
        {
            uint badge = state.Badge == UiBadge.Warning ? 0xFFD97706 : Accent;
            FillEllipse(rect.Right - 10, rect.Y + 3, 7, 7, badge);
        }
    }

    private void DrawFader(UiRect bounds, float value, uint rgba)
    {
        int center = bounds.X + bounds.Width / 2;
        int previewMax = Px(44);
        int previewDiameter = Math.Clamp((int)MathF.Round(BrushFader.Clamp(value) * _scale), Px(2), previewMax);
        int previewCenterY = bounds.Y + Px(28);
        FillEllipse(center - previewDiameter / 2f, previewCenterY - previewDiameter / 2f,
            previewDiameter, previewDiameter, RgbaToArgb(rgba));
        DrawEllipse(center - previewMax / 2f, previewCenterY - previewMax / 2f,
            previewMax, previewMax, Border, PxF(1));
        DrawTextCentered($"{MathF.Round(value):0} px",
            new UiRect(bounds.X, bounds.Bottom - Px(25), bounds.Width, Px(20)), 12, Text, bold: true);

        UiRect rail = BrushFader.TrackBounds(bounds, _scale);
        int railTop = rail.Y;
        int railBottom = rail.Bottom - 1;
        int topHalf = Px(16), bottomHalf = Px(2);
        FillPolygon(new[]
        {
            new PointI { X = center - topHalf, Y = railTop },
            new PointI { X = center + topHalf, Y = railTop },
            new PointI { X = center + bottomHalf, Y = railBottom },
            new PointI { X = center - bottomHalf, Y = railBottom },
        }, 0xFFE7E3DF);
        DrawLine(center - topHalf, railTop, center - bottomHalf, railBottom, 0xFF948C84, PxF(1));
        DrawLine(center + topHalf, railTop, center + bottomHalf, railBottom, 0xFF948C84, PxF(1));
        DrawLine(center - topHalf, railTop, center + topHalf, railTop, 0xFF948C84, PxF(1));
        int valueY = BrushFader.YFromValue(value, rail);
        float t = Math.Clamp((valueY - railTop) / (float)Math.Max(1, railBottom - railTop), 0, 1);
        int valueHalf = (int)MathF.Round(topHalf + (bottomHalf - topHalf) * t);
        FillPolygon(new[]
        {
            new PointI { X = center - valueHalf, Y = valueY },
            new PointI { X = center + valueHalf, Y = valueY },
            new PointI { X = center + bottomHalf, Y = railBottom },
            new PointI { X = center - bottomHalf, Y = railBottom },
        }, RgbaToArgb(rgba));
    }

    private void DrawSvg(SvgIcon icon, UiRect bounds, uint color, float strokeWidth)
    {
        if (GdipCreatePath(0, out IntPtr path) != 0) return;
        try
        {
            float scale = Math.Min(bounds.Width, bounds.Height) / (float)SvgIconCatalog.ViewBoxSize;
            float ox = bounds.X + (bounds.Width - SvgIconCatalog.ViewBoxSize * scale) / 2;
            float oy = bounds.Y + (bounds.Height - SvgIconCatalog.ViewBoxSize * scale) / 2;
            float cx = 0, cy = 0, sx = 0, sy = 0;
            foreach (SvgSegment segment in icon.Segments)
            {
                var v = segment.Values;
                switch (segment.Kind)
                {
                    case SvgSegmentKind.Move:
                        GdipStartPathFigure(path); cx = sx = v[0]; cy = sy = v[1]; break;
                    case SvgSegmentKind.Line:
                        float lx = float.IsNaN(v[0]) ? cx : v[0], ly = float.IsNaN(v[1]) ? cy : v[1];
                        GdipAddPathLine(path, ox + cx * scale, oy + cy * scale, ox + lx * scale, oy + ly * scale); cx = lx; cy = ly; break;
                    case SvgSegmentKind.Cubic:
                        GdipAddPathBezier(path, ox + cx * scale, oy + cy * scale,
                            ox + v[0] * scale, oy + v[1] * scale, ox + v[2] * scale, oy + v[3] * scale,
                            ox + v[4] * scale, oy + v[5] * scale); cx = v[4]; cy = v[5]; break;
                    case SvgSegmentKind.Arc:
                        AddSvgArc(path, cx, cy, v, ox, oy, scale); cx = v[5]; cy = v[6]; break;
                    case SvgSegmentKind.Close:
                        GdipAddPathLine(path, ox + cx * scale, oy + cy * scale, ox + sx * scale, oy + sy * scale);
                        GdipClosePathFigure(path); cx = sx; cy = sy; break;
                }
            }
            IntPtr pen = CreatePen(color, Math.Max(1.25f, strokeWidth * scale));
            GdipSetPenLineCap197819(pen, 2, 2, 2); // round caps
            GdipSetPenLineJoin(pen, 2); // round joins
            GdipDrawPath(_graphics, pen, path);
            GdipDeletePen(pen);
        }
        finally { GdipDeletePath(path); }
    }

    private static void AddSvgArc(IntPtr path, float x1, float y1, IReadOnlyList<float> v, float ox, float oy, float scale)
    {
        double rx = Math.Abs(v[0]), ry = Math.Abs(v[1]), phi = v[2] * Math.PI / 180.0;
        bool large = v[3] != 0, sweep = v[4] != 0; double x2 = v[5], y2 = v[6];
        if (rx == 0 || ry == 0 || (x1 == x2 && y1 == y2)) { GdipAddPathLine(path, ox + x1 * scale, oy + y1 * scale, ox + (float)x2 * scale, oy + (float)y2 * scale); return; }
        double cos = Math.Cos(phi), sin = Math.Sin(phi), dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
        double xp = cos * dx + sin * dy, yp = -sin * dx + cos * dy;
        double lambda = xp * xp / (rx * rx) + yp * yp / (ry * ry);
        if (lambda > 1) { double factor = Math.Sqrt(lambda); rx *= factor; ry *= factor; }
        double denominator = rx * rx * yp * yp + ry * ry * xp * xp;
        double numerator = Math.Max(0, rx * rx * ry * ry - denominator);
        double coefficient = (large == sweep ? -1 : 1) * Math.Sqrt(denominator == 0 ? 0 : numerator / denominator);
        double cxp = coefficient * rx * yp / ry, cyp = coefficient * -ry * xp / rx;
        double centerX = cos * cxp - sin * cyp + (x1 + x2) / 2, centerY = sin * cxp + cos * cyp + (y1 + y2) / 2;
        double theta = Angle(1, 0, (xp - cxp) / rx, (yp - cyp) / ry);
        double delta = Angle((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry);
        if (!sweep && delta > 0) delta -= Math.PI * 2; else if (sweep && delta < 0) delta += Math.PI * 2;
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
        double step = delta / pieces;
        for (int i = 0; i < pieces; i++)
        {
            double a = theta + i * step, b = a + step, alpha = 4.0 / 3.0 * Math.Tan(step / 4.0);
            (double px0, double py0, double dx0, double dy0) = EllipsePoint(centerX, centerY, rx, ry, phi, a);
            (double px1, double py1, double dx1, double dy1) = EllipsePoint(centerX, centerY, rx, ry, phi, b);
            GdipAddPathBezier(path, ox + (float)px0 * scale, oy + (float)py0 * scale,
                ox + (float)(px0 + alpha * dx0) * scale, oy + (float)(py0 + alpha * dy0) * scale,
                ox + (float)(px1 - alpha * dx1) * scale, oy + (float)(py1 - alpha * dy1) * scale,
                ox + (float)px1 * scale, oy + (float)py1 * scale);
        }
    }

    private static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    private static (double x, double y, double dx, double dy) EllipsePoint(double cx, double cy, double rx, double ry, double phi, double a)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi), ca = Math.Cos(a), sa = Math.Sin(a);
        return (cx + c * rx * ca - s * ry * sa, cy + s * rx * ca + c * ry * sa,
            -c * rx * sa - s * ry * ca, -s * rx * sa + c * ry * ca);
    }

    private void DrawText(string text, float x, float y, float size, uint color, bool bold = false)
    {
        IntPtr family;
        if (GdipCreateFontFamilyFromName("Yu Gothic UI", IntPtr.Zero, out family) != 0)
            GdipGetGenericFontFamilySansSerif(out family);
        GdipCreateFont(family, size * _scale, bold ? 1 : 0, 2, out IntPtr font);
        IntPtr brush = CreateBrush(color);
        var rect = new RectF { X = x, Y = y, Width = 1200 * _scale, Height = size * _scale * 1.7f };
        GdipDrawString(_graphics, text, text.Length, font, ref rect, IntPtr.Zero, brush);
        GdipDeleteBrush(brush); GdipDeleteFont(font); GdipDeleteFontFamily(family);
    }

    private void DrawTextCentered(string text, UiRect bounds, float size, uint color, bool bold = false)
    {
        IntPtr family;
        if (GdipCreateFontFamilyFromName("Yu Gothic UI", IntPtr.Zero, out family) != 0)
            GdipGetGenericFontFamilySansSerif(out family);
        GdipCreateFont(family, size * _scale, bold ? 1 : 0, 2, out IntPtr font);
        IntPtr brush = CreateBrush(color);
        GdipStringFormatGetGenericDefault(out IntPtr format);
        GdipSetStringFormatAlign(format, 1); // StringAlignment.Center
        GdipSetStringFormatLineAlign(format, 1);
        var rect = new RectF { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
        GdipDrawString(_graphics, text, text.Length, font, ref rect, format, brush);
        GdipDeleteStringFormat(format);
        GdipDeleteBrush(brush); GdipDeleteFont(font); GdipDeleteFontFamily(family);
    }

    private void Fill(UiRect rect, uint color) { IntPtr b = CreateBrush(color); GdipFillRectangleI(_graphics, b, rect.X, rect.Y, rect.Width, rect.Height); GdipDeleteBrush(b); }
    private void DrawRect(UiRect rect, uint color, float width) { IntPtr p = CreatePen(color, width); GdipDrawRectangleI(_graphics, p, rect.X, rect.Y, rect.Width - 1, rect.Height - 1); GdipDeletePen(p); }
    private void DrawLine(float x1, float y1, float x2, float y2, uint color, float width) { IntPtr p = CreatePen(color, width); GdipDrawLine(_graphics, p, x1, y1, x2, y2); GdipDeletePen(p); }
    private void FillEllipse(float x, float y, float w, float h, uint color) { IntPtr b = CreateBrush(color); GdipFillEllipse(_graphics, b, x, y, w, h); GdipDeleteBrush(b); }
    private void FillTriangle(int x1, int y1, int x2, int y2, int x3, int y3, uint color)
    {
        IntPtr brush = CreateBrush(color);
        var points = new[] { new PointI { X = x1, Y = y1 }, new PointI { X = x2, Y = y2 }, new PointI { X = x3, Y = y3 } };
        GdipFillPolygonI(_graphics, brush, points, points.Length, 0);
        GdipDeleteBrush(brush);
    }
    private void FillPolygon(PointI[] points, uint color)
    {
        IntPtr brush = CreateBrush(color);
        GdipFillPolygonI(_graphics, brush, points, points.Length, 0);
        GdipDeleteBrush(brush);
    }
    private void DrawPolygon(PointI[] points, uint color, float width)
    {
        IntPtr pen = CreatePen(color, width);
        GdipDrawPolygonI(_graphics, pen, points, points.Length);
        GdipDeletePen(pen);
    }
    private void DrawEllipse(float x, float y, float w, float h, uint color, float width) { IntPtr p = CreatePen(color, width); GdipDrawEllipse(_graphics, p, x, y, w, h); GdipDeletePen(p); }
    private static UiRect Inflate(UiRect r, int amount) => new(r.X + amount, r.Y + amount, r.Width - amount * 2, r.Height - amount * 2);
    private int Px(int dip) => Math.Max(1, (int)MathF.Round(dip * _scale));
    private float PxF(float dip) => Math.Max(1, dip * _scale);
    private static uint RgbaToArgb(uint rgba) => (rgba << 24) | (rgba >> 8);
    private static IntPtr CreateBrush(uint color) { GdipCreateSolidFill(color, out IntPtr brush); return brush; }
    private static IntPtr CreatePen(uint color, float width) { GdipCreatePen1(color, width, 2, out IntPtr pen); return pen; }
    private static void Check(int status) { if (status != 0) throw new InvalidOperationException($"GDI+ UI renderer failed ({status})."); }
    public void Dispose() { if (_graphics != IntPtr.Zero) GdipDeleteGraphics(_graphics); }

    [StructLayout(LayoutKind.Sequential)] private struct GdiplusStartupInput { public uint Version; public IntPtr DebugEventCallback; public bool SuppressBackgroundThread; public bool SuppressExternalCodecs; }
    [StructLayout(LayoutKind.Sequential)] private struct RectF { public float X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipStringFormatGetGenericDefault(out IntPtr format);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatAlign(IntPtr format, int alignment);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatLineAlign(IntPtr format, int alignment);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteStringFormat(IntPtr format);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipFillRectangleI(IntPtr graphics, IntPtr brush, int x, int y, int width, int height);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawRectangleI(IntPtr graphics, IntPtr pen, int x, int y, int width, int height);
    [DllImport("gdiplus.dll")] private static extern int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);
    [DllImport("gdiplus.dll")] private static extern int GdipFillPolygonI(IntPtr graphics, IntPtr brush, [In] PointI[] points, int count, int fillMode);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawPolygonI(IntPtr graphics, IntPtr pen, [In] PointI[] points, int count);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawEllipse(IntPtr graphics, IntPtr pen, float x, float y, float width, float height);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawLine(IntPtr graphics, IntPtr pen, float x1, float y1, float x2, float y2);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint color, float width, int unit, out IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipSetPenLineCap197819(IntPtr pen, int startCap, int endCap, int dashCap);
    [DllImport("gdiplus.dll")] private static extern int GdipSetPenLineJoin(IntPtr pen, int lineJoin);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePath(int fillMode, out IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePath(IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipStartPathFigure(IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipClosePathFigure(IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathLine(IntPtr path, float x1, float y1, float x2, float y2);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathBezier(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
    [DllImport("gdiplus.dll")] private static extern int GdipGetGenericFontFamilySansSerif(out IntPtr family);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(IntPtr family);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(IntPtr font);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);
}
