// 画面右側のフローティングパネル: 縦のサイズスライダー + 色パレット
// (発注者指示 2026-07-11)。
//
// キャンバスの上に重ねて出すが、絵の視界を妨げないことを最優先にしている:
//   - 幅を小さく抑える(スライダー1本+スウォッチ1列分)
//   - 背景は半透明の素材(.ultraThinMaterial)
//   - ポインタが乗っていない間はさらに薄く退避させる
//   - 縦に長くなる場合は全体をスクロール可能にし、パネル自体が画面外へ
//     はみ出したりキャンバスを圧迫したりしないようにする
//
// 既存のツールバー(Size スライダー・ColorPicker)は仕様通りそのまま残し、この
// パネルは追加のショートカットとして共存する。

import AkapenKit
import SwiftUI

struct SidePanelView: View {
    @ObservedObject var state: AppState
    @State private var isHovering = false

    private let panelWidth: CGFloat = 76
    private let swatchSize: CGFloat = 26
    private let swatchSpacing: CGFloat = 6
    private let sliderLength: CGFloat = 150

    private let minSize: Double = 1
    private let maxSize: Double = 50

    var body: some View {
        ScrollView(showsIndicators: false) {
            VStack(spacing: 14) {
                toolToggle
                Divider().opacity(0.5)
                sizeSlider
                Divider().opacity(0.5)
                paletteList
            }
            .padding(10)
        }
        .frame(width: panelWidth)
        .frame(maxHeight: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(.ultraThinMaterial)
        )
        .opacity(isHovering ? 0.97 : 0.4)
        .animation(.easeInOut(duration: 0.2), value: isHovering)
        .onHover { isHovering = $0 }
        .accessibilityLabel("ペンのサイズと色")
    }

    // MARK: - ツール切替(ペン / 消しゴム・アイコン)

    private var toolToggle: some View {
        VStack(spacing: 8) {
            toolButton(.pen, systemName: "pencil.tip", label: "ペン")
            toolButton(.eraser, systemName: "eraser.fill", label: "消しゴム")
        }
    }

    private func toolButton(_ tool: AkapenTool, systemName: String, label: String) -> some View {
        let isSelected = state.tool == tool
        return Image(systemName: systemName)
            .font(.system(size: 16, weight: .medium))
            .frame(width: 34, height: 34) // タッチ/ペンで押しやすいターゲット
            .foregroundColor(isSelected ? Color.white : Color.primary)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(isSelected ? Color.accentColor : Color.primary.opacity(0.08))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .stroke(Color.accentColor, lineWidth: isSelected ? 2 : 0)
            )
            .contentShape(Rectangle())
            .onTapGesture {
                state.tool = tool
                state.applyToolState()
            }
            .accessibilityLabel(label)
            .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    // MARK: - サイズスライダー(縦・ウェッジ型・1〜50px)
    //
    // ボリュームつまみのような三角形(下=細い=1px、上=太い=50px)のトラック上を、
    // つまみが上下する。縦位置を線形に 1...50 へ写像し、akapen_set_size へ連動。
    // 三角形は下から現在値までを濃く充填して、量が一目で分かるようにする。

    private let wedgeTopWidth: CGFloat = 34 // 上端(=最大サイズ)の三角形の幅
    private let knobHeight: CGFloat = 10

    private var sizeSlider: some View {
        VStack(spacing: 4) {
            Text("\(Int(state.brushSize))px")
                .font(.caption2.monospacedDigit())
                .foregroundColor(.primary)
            wedgeTrack
            Image(systemName: "pencil.tip")
                .font(.caption2)
                .foregroundColor(.secondary)
        }
    }

    private var wedgeTrack: some View {
        GeometryReader { geo in
            let h = geo.size.height
            // 現在値の割合(0=下端1px … 1=上端50px)。
            let frac = (state.brushSize - minSize) / (maxSize - minSize)
            // 上端が原点(y=0)なので、下からの充填は上側 (1-frac) を空ける。
            let knobY = h * (1 - CGFloat(frac))

            ZStack {
                // トラック地の三角形(下細・上太)。
                WedgeShape()
                    .fill(Color.primary.opacity(0.12))
                // 下から現在値までの充填。
                WedgeShape()
                    .fill(Color.accentColor.opacity(0.55))
                    .mask(
                        Rectangle()
                            .frame(height: h - knobY)
                            .frame(maxHeight: .infinity, alignment: .bottom)
                    )
                // つまみ(横バー)。
                Capsule()
                    .fill(Color.primary)
                    .frame(width: wedgeTopWidth + 6, height: knobHeight)
                    .shadow(color: .black.opacity(0.35), radius: 2, y: 1)
                    .position(x: geo.size.width / 2, y: clamp(knobY, 0, h))
            }
            .contentShape(Rectangle()) // 見た目の三角形より広い当たり判定
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { v in updateSize(fromY: v.location.y, height: h) }
            )
        }
        .frame(width: wedgeTopWidth + 16, height: sliderLength)
        .accessibilityLabel("ペンの太さ")
        .accessibilityValue("\(Int(state.brushSize))ピクセル")
    }

    /// 縦位置(上=0)を 1...50 に写像する。上ほど太い。
    private func updateSize(fromY y: CGFloat, height: CGFloat) {
        guard height > 0 else { return }
        let frac = 1 - Double(clamp(y, 0, height) / height) // 上端=1, 下端=0
        let newSize = (minSize + frac * (maxSize - minSize)).rounded()
        if newSize != state.brushSize {
            state.brushSize = newSize
            state.applyToolState()
        }
    }

    private func clamp(_ v: CGFloat, _ lo: CGFloat, _ hi: CGFloat) -> CGFloat {
        min(max(v, lo), hi)
    }

    // MARK: - 色パレット(MS Paint 上段10色)

    private var paletteList: some View {
        VStack(spacing: swatchSpacing) {
            ForEach(AkapenPalette.colors) { pc in
                swatch(pc)
            }
        }
    }

    private func swatch(_ pc: PaletteColor) -> some View {
        let isSelected = state.selectedColorHex == pc.hex
        return Circle()
            .fill(pc.color)
            .frame(width: swatchSize, height: swatchSize)
            .overlay(Circle().stroke(Color.black.opacity(0.25), lineWidth: 1))
            .overlay(Circle().stroke(Color.accentColor, lineWidth: isSelected ? 3 : 0))
            .shadow(color: .black.opacity(isSelected ? 0.4 : 0), radius: isSelected ? 3 : 0)
            .padding(4) // タッチ/ペンでも押しやすいよう当たり判定を広げる
            .contentShape(Rectangle())
            .onTapGesture { state.selectColor(hex: pc.hex) }
            .accessibilityLabel(pc.name)
    }
}

/// 縦向きのウェッジ(三角形)。下辺は幅0(=最小サイズ)、上辺はフル幅(=最大サイズ)。
/// ボリュームつまみのトラックの見た目を作る。
struct WedgeShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        // 上辺: 左上→右上(フル幅)。下辺: 中央の1点(幅0)。
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}
