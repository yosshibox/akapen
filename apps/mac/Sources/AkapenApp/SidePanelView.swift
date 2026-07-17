import AkapenKit
import AkapenUIContract
import AppKit
import SwiftUI

/// Loaded-state right dock (V1.2, Windows V1.1 の写像): navigator on top,
/// then the ink tools, the vertical brush fader and the 2×5 palette.
/// Document/history/view commands remain in native menus and shortcuts.
struct SidePanelView: View {
    @ObservedObject var state: AppState

    var body: some View {
        VStack(spacing: 10) {
            NavigatorView(state: state)
            dockDivider
            toolGroup
            dockDivider
            sizeControl
            dockDivider
            palette
            Spacer(minLength: 0)
        }
        .padding(12)
        .frame(width: CGFloat(AkapenUIMetrics.dockWidth))
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Color(nsColor: .controlBackgroundColor))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("描画ツール")
    }

    private var toolGroup: some View {
        HStack(spacing: 6) {
            // 矢印(操作なし)・ペン・消しゴム — Windows V1.1 の3ボタン構成。
            toolButton(symbol: "cursorarrow", label: "矢印", shortcut: "A",
                       selected: state.arrowMode) {
                state.arrowMode = true
            }
            toolButton(symbol: "pencil", label: "ペン", shortcut: "B / P",
                       selected: !state.arrowMode && state.tool == .pen) {
                state.arrowMode = false
                state.requestCanvas(.draw)
                state.tool = .pen
                state.applyToolState()
            }
            toolButton(symbol: "eraser", label: "消しゴム", shortcut: "E",
                       selected: !state.arrowMode && state.tool == .eraser) {
                state.arrowMode = false
                state.requestCanvas(.draw)
                state.tool = .eraser
                state.applyToolState()
            }
        }
    }

    private func toolButton(symbol: String, label: String, shortcut: String,
                            selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .medium))
                .frame(width: 32, height: 32)
        }
        .buttonStyle(CompactToolButtonStyle(selected: selected))
        .help("\(label) (\(shortcut))")
        .accessibilityLabel(label)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var palette: some View {
        let diameter: CGFloat = 24
        let columns = [GridItem(.fixed(32), spacing: 8), GridItem(.fixed(32), spacing: 8)]
        return LazyVGrid(columns: columns, spacing: 6) {
            ForEach(AkapenPalette.colors) { swatch in
                Button { state.selectColor(hex: swatch.hex) } label: {
                    Circle()
                        .fill(swatch.color)
                        .frame(width: diameter, height: diameter)
                        .overlay {
                            Circle().stroke(
                                state.selectedColorHex == swatch.hex
                                    ? Color.accentColor : Color.primary.opacity(0.22),
                                lineWidth: state.selectedColorHex == swatch.hex ? 3 : 1
                            )
                        }
                        .overlay {
                            if swatch.hex == "#FFFFFF" {
                                Circle().stroke(Color.black.opacity(0.18), lineWidth: 1)
                            }
                        }
                        .frame(width: 32, height: 30)
                }
                .buttonStyle(.plain)
                .help(swatch.name)
                .accessibilityLabel(swatch.name)
                .accessibilityAddTraits(state.selectedColorHex == swatch.hex ? .isSelected : [])
            }
        }
        .accessibilityLabel("描画色")
    }

    private var sizeControl: some View {
        VStack(spacing: 4) {
            CompactBrushSizeControl(
                value: $state.brushSize,
                color: NSColor(state.color),
                onChange: state.applyToolState
            )
            .frame(
                width: CGFloat(AkapenUIMetrics.sizeControlWidth),
                height: CGFloat(AkapenUIMetrics.sizeControlHeight)
            )
            .help("ペン先サイズ: \(Int(state.brushSize)) px ([ / ])")
            .accessibilityLabel("ペン先サイズ")
            .accessibilityValue("\(Int(state.brushSize))ピクセル")
        }
    }

    private var dockDivider: some View {
        Divider()
    }
}

private struct CompactToolButtonStyle: ButtonStyle {
    let selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(selected ? Color.accentColor : Color.primary.opacity(0.86))
            .background {
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(selected ? Color.accentColor.opacity(0.13) : Color.primary.opacity(configuration.isPressed ? 0.07 : 0.001))
            }
            .overlay {
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .stroke(selected ? Color.accentColor.opacity(0.75) : .clear, lineWidth: 1.5)
            }
            .contentShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }
}

private struct CompactBrushSizeControl: NSViewRepresentable {
    @Binding var value: Double
    let color: NSColor
    let onChange: () -> Void

    func makeNSView(context: Context) -> CompactBrushSizeNSView {
        let view = CompactBrushSizeNSView()
        view.setAccessibilityElement(true)
        view.setAccessibilityRole(.slider)
        view.setAccessibilityMinValue(NSNumber(value: BrushSizeKnob.minimum))
        view.setAccessibilityMaxValue(NSNumber(value: BrushSizeKnob.maximum))
        view.onValueChanged = { newValue in
            value = newValue
            onChange()
        }
        return view
    }

    func updateNSView(_ view: CompactBrushSizeNSView, context: Context) {
        view.value = BrushSizeKnob.clamp(value)
        view.inkColor = color.usingColorSpace(.sRGB) ?? .red
        view.needsDisplay = true
    }
}

private final class CompactBrushSizeNSView: NSView {
    var value = 14.0
    var inkColor = NSColor.systemRed
    var onValueChanged: ((Double) -> Void)?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        updateValue(event)
    }

    override func mouseDragged(with event: NSEvent) {
        updateValue(event)
    }

    override func keyDown(with event: NSEvent) {
        let key: BrushSizeKey?
        switch event.keyCode {
        case 126: key = .up
        case 125: key = .down
        case 116: key = .pageUp
        case 121: key = .pageDown
        case 115: key = .home
        case 119: key = .end
        default: key = nil
        }
        guard let key else { return super.keyDown(with: event) }
        setValue(BrushSizeKnob.adjust(value, key: key))
    }

    // Windows V1.0/V1.1 正本のフェーダー描画(NativeUiRenderer.DrawFader と
    // 同構成): 上=円形プレビュー、中央=左右対称の扇形レール+インク色の
    // 値フィル、下=px 数値の即時表示。
    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.setFill()
        bounds.fill()

        let center = bounds.midX

        // Circular preview (diameter follows the actual brush size, capped).
        let previewMax: CGFloat = 44
        let previewDiameter = min(previewMax, max(2, CGFloat(BrushSizeKnob.clamp(value))))
        let previewCenterY: CGFloat = 28
        inkColor.setFill()
        NSBezierPath(ovalIn: NSRect(
            x: center - previewDiameter / 2, y: previewCenterY - previewDiameter / 2,
            width: previewDiameter, height: previewDiameter)).fill()
        NSColor.separatorColor.setStroke()
        NSBezierPath(ovalIn: NSRect(
            x: center - previewMax / 2, y: previewCenterY - previewMax / 2,
            width: previewMax, height: previewMax)).stroke()

        // Symmetric fan rail (wide at the top = max, narrow at the bottom = min).
        let control = ContractRect(x: 0, y: 0, width: Int(bounds.width), height: Int(bounds.height))
        let rail = BrushSizeKnob.trackBounds(in: control)
        let railTop = CGFloat(rail.y)
        let railBottom = CGFloat(rail.bottom - 1)
        let topHalf: CGFloat = 16
        let bottomHalf: CGFloat = 2
        let fan = NSBezierPath()
        fan.move(to: NSPoint(x: center - topHalf, y: railTop))
        fan.line(to: NSPoint(x: center + topHalf, y: railTop))
        fan.line(to: NSPoint(x: center + bottomHalf, y: railBottom))
        fan.line(to: NSPoint(x: center - bottomHalf, y: railBottom))
        fan.close()
        NSColor.quaternaryLabelColor.setFill()
        fan.fill()
        NSColor.tertiaryLabelColor.setStroke()
        fan.stroke()

        // Value fill: from the current value down to the bottom, in ink color.
        let valueY = CGFloat(BrushSizeKnob.y(forValue: value, in: rail))
        let t = min(1, max(0, (valueY - railTop) / max(1, railBottom - railTop)))
        let valueHalf = topHalf + (bottomHalf - topHalf) * t
        let fill = NSBezierPath()
        fill.move(to: NSPoint(x: center - valueHalf, y: valueY))
        fill.line(to: NSPoint(x: center + valueHalf, y: valueY))
        fill.line(to: NSPoint(x: center + bottomHalf, y: railBottom))
        fill.line(to: NSPoint(x: center - bottomHalf, y: railBottom))
        fill.close()
        inkColor.setFill()
        fill.fill()

        // Live px readout.
        let label = "\(Int(value.rounded())) px" as NSString
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold),
            .foregroundColor: NSColor.labelColor,
        ]
        let size = label.size(withAttributes: attributes)
        label.draw(at: NSPoint(x: center - size.width / 2, y: bounds.height - 22), withAttributes: attributes)

        if window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke()
            let focus = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 5, yRadius: 5)
            focus.lineWidth = 2
            focus.stroke()
        }
    }

    override func accessibilityPerformIncrement() -> Bool {
        setValue(BrushSizeKnob.adjust(value, key: .up))
        return true
    }

    override func accessibilityPerformDecrement() -> Bool {
        setValue(BrushSizeKnob.adjust(value, key: .down))
        return true
    }

    private func updateValue(_ event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let control = ContractRect(x: 0, y: 0, width: Int(bounds.width), height: Int(bounds.height))
        let rail = BrushSizeKnob.trackBounds(in: control)
        setValue(BrushSizeKnob.value(atY: Int(point.y), in: rail))
    }

    private func setValue(_ newValue: Double) {
        value = newValue
        onValueChanged?(newValue)
        needsDisplay = true
        setAccessibilityValue("\(Int(newValue))ピクセル")
    }
}
