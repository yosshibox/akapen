// Akapen mac シェルの色パレット定義(発注者指示 2026-07-11、同日中に10色構成へ改訂)。
//
// MS Paint 既定パレット上段10色に一本化。名前+hex をこの1箇所の配列にまとめてあり、
// 発注者が後で色を差し替えたいときはここだけを書き換えればよい(UI側にマジックナン
// バーを散らさない)。

import SwiftUI

struct PaletteColor: Identifiable, Equatable {
    /// hex を安定IDとして使う(選択中判定にも流用)。
    let id: String
    let name: String
    let hex: String

    var color: Color { Color(hex: hex) }

    init(name: String, hex: String) {
        self.id = hex
        self.name = name
        self.hex = hex
    }
}

enum AkapenPalette {
    /// MS Paint 既定パレット上段10色相当。順序もこのまま(発注者確定)。
    static let colors: [PaletteColor] = [
        PaletteColor(name: "黒", hex: "#000000"),
        PaletteColor(name: "グレー50%", hex: "#7F7F7F"),
        PaletteColor(name: "暗い赤", hex: "#880015"),
        PaletteColor(name: "赤", hex: "#ED1C24"),
        PaletteColor(name: "オレンジ", hex: "#FF7F27"),
        PaletteColor(name: "黄", hex: "#FFF200"),
        PaletteColor(name: "緑", hex: "#22B14C"),
        PaletteColor(name: "ターコイズ", hex: "#00A2E8"),
        PaletteColor(name: "インディゴ", hex: "#3F48CC"),
        PaletteColor(name: "白", hex: "#FFFFFF"),
    ]

    /// 既定選択色(赤)。
    static let defaultColor = colors[3] // #ED1C24
}

extension Color {
    /// `#RRGGBB` 形式の16進文字列をパースする。失敗時は不透明黒にフォールバックする。
    init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("#") { s.removeFirst() }
        var value: UInt64 = 0
        Scanner(string: s).scanHexInt64(&value)
        let r = Double((value & 0xFF0000) >> 16) / 255
        let g = Double((value & 0x00FF00) >> 8) / 255
        let b = Double(value & 0x0000FF) / 255
        let a = s.count == 8 ? Double(value & 0xFF) / 255 : 1
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    static func isValidHex(_ hex: String) -> Bool {
        var value = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("#") { value.removeFirst() }
        guard value.count == 6 || value.count == 8 else { return false }
        return value.allSatisfy { $0.isHexDigit }
    }
}
