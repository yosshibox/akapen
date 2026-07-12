// Akapen Windows シェルの色パレット定義(spec §9 M2 / M2-D、発注者指示 2026-07-11)。
//
// MS Paint 既定パレット上段10色を1箇所に集約する。UI 側(MainWindow.xaml.cs
// の BuildColorSwatches)は色を直接ハードコードせず、この配列を回してスウォッチ
// を生成する。色を差し替えるときはこのファイルだけを書き換えればよい。
//
// mac シェルの `apps/mac/Sources/AkapenApp/PaletteColors.swift` と 1 対 1 に
// 対応させてあり、Hex 表記・並び順・既定色(赤 #ED1C24)まで揃えている。
// C ABI (`akapen_set_color`) が受け取る 0xRRGGBBAA(alpha 固定 0xFF)へのパックも
// ここで済ませているので、シェル側のコードにマジックナンバーが散らない。
// アルファは常に不透明(ストロークの不透明度はコア側の別プロパティで、mac の
// `packedRGBA` と同じ方針)。

using System.Collections.Generic;
using Windows.UI;

namespace AkapenApp;

/// <summary>
/// 1色分のメタ情報。<paramref name="Hex"/> はハイライト判定に使うので "#RRGGBB"
/// 表記に統一する。<paramref name="Rgba"/> は C ABI にそのまま渡す packed 0xRRGGBBAA。
/// </summary>
internal readonly record struct PaletteColor(string Name, string Hex, uint Rgba)
{
    /// <summary>
    /// XAML の SolidColorBrush に渡すための <see cref="Windows.UI.Color"/>。
    /// packed 0xRRGGBBAA から順序を戻して ARGB を組み立てる。
    /// </summary>
    public Color WinColor
    {
        get
        {
            byte r = (byte)((Rgba >> 24) & 0xFF);
            byte g = (byte)((Rgba >> 16) & 0xFF);
            byte b = (byte)((Rgba >> 8) & 0xFF);
            byte a = (byte)(Rgba & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }
    }
}

internal static class PaletteColors
{
    /// <summary>
    /// MS Paint 既定パレット上段の10色。並び順もこのまま(発注者確定)。
    /// mac 側 <c>AkapenPalette.colors</c> と 1 対 1。
    /// </summary>
    internal static readonly IReadOnlyList<PaletteColor> Colors = new PaletteColor[]
    {
        new("黒",         "#000000", 0x000000FFu),
        new("グレー50%",  "#7F7F7F", 0x7F7F7FFFu),
        new("暗い赤",     "#880015", 0x880015FFu),
        new("赤",         "#ED1C24", 0xED1C24FFu),
        new("オレンジ",   "#FF7F27", 0xFF7F27FFu),
        new("黄",         "#FFF200", 0xFFF200FFu),
        new("緑",         "#22B14C", 0x22B14CFFu),
        new("ターコイズ", "#00A2E8", 0x00A2E8FFu),
        new("インディゴ", "#3F48CC", 0x3F48CCFFu),
        new("紫",         "#A349A4", 0xA349A4FFu),
    };

    /// <summary>
    /// 起動直後の既定色(index 3 = 赤 #ED1C24)。mac 側 <c>PaletteColors.defaultColor</c>
    /// と同じ。
    /// </summary>
    internal static readonly PaletteColor Default = Colors[3];

    /// <summary>
    /// 既定色の packed 0xRRGGBBAA。フィールド初期化子から (Default は readonly
    /// フィールドなので同じ static class 内では静的フィールド初期化子経由でしか
    /// アクセスできない) 使うためのショートカット。
    /// </summary>
    internal const uint DefaultRgba = 0xED1C24FFu;
}
