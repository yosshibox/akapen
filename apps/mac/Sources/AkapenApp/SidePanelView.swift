import AkapenKit
import AkapenUIContract
import AppKit
import SwiftUI

/// Loaded-state tool dock. Document/history/view commands remain in native
/// menus and shortcuts; only the two ink tools and direct style controls are
/// permanent here.
struct SidePanelView: View {
    @ObservedObject var state: AppState

    var body: some View {
        HStack(spacing: 10) {
            toolGroup
            dockDivider
            palette
            dockDivider
            sizeControl
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .controlBackgroundColor))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("描画ツール")
    }

    private var toolGroup: some View {
        HStack(spacing: 6) {
            tool(.pen, symbol: "pencil.tip", label: "ペン", shortcut: "P")
            tool(.eraser, symbol: "eraser", label: "消しゴム", shortcut: "E")
        }
    }

    private func tool(_ tool: AkapenTool, symbol: String, label: String, shortcut: String) -> some View {
        Button {
            state.requestCanvas(.draw)
            state.tool = tool
            state.applyToolState()
        } label: {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .medium))
                .frame(width: 32, height: 32)
        }
        .buttonStyle(CompactToolButtonStyle(selected: state.tool == tool))
        .help("\(label) (\(shortcut))")
        .accessibilityLabel(label)
        .accessibilityAddTraits(state.tool == tool ? .isSelected : [])
    }

    private var palette: some View {
        GeometryReader { proxy in
            let spacing: CGFloat = 4
            let diameter = max(
                CGFloat(PaletteContract.minimumDiameter),
                min(24, (proxy.size.width - spacing * 9) / 10)
            )
            HStack(spacing: spacing) {
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
                            .frame(width: max(20, diameter), height: 32)
                    }
                    .buttonStyle(.plain)
                    .help(swatch.name)
                    .accessibilityLabel(swatch.name)
                    .accessibilityAddTraits(state.selectedColorHex == swatch.hex ? .isSelected : [])
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
        }
        .frame(minWidth: 176, maxWidth: .infinity, minHeight: 32, maxHeight: 40)
        .accessibilityLabel("描画色")
    }

    private var sizeControl: some View {
        HStack(spacing: 6) {
            Text("サイズ")
                .font(.caption2)
                .foregroundStyle(.secondary)
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
        Divider().frame(height: 48)
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

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.setFill()
        bounds.fill()

        let center = bounds.midX
        let cap = NSBezierPath()
        cap.move(to: NSPoint(x: center - 9, y: 4))
        cap.line(to: NSPoint(x: center + 9, y: 4))
        cap.line(to: NSPoint(x: center, y: 15))
        cap.close()
        NSColor.controlAccentColor.setFill()
        cap.fill()

        let normalized = (value - BrushSizeKnob.minimum) /
            (BrushSizeKnob.maximum - BrushSizeKnob.minimum)
        for index in 0..<5 {
            let y = CGFloat(23 + index * 7)
            let active = Double(4 - index) / 4.0 <= normalized
            (active ? inkColor : NSColor.separatorColor).setStroke()
            let tick = NSBezierPath()
            tick.lineWidth = active ? 2 : 1
            let half = CGFloat(3 + (4 - index))
            tick.move(to: NSPoint(x: center - half, y: y))
            tick.line(to: NSPoint(x: center + half, y: y))
            tick.stroke()
        }

        let label = "\(Int(value.rounded())) px" as NSString
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .medium),
            .foregroundColor: NSColor.secondaryLabelColor,
        ]
        let size = label.size(withAttributes: attributes)
        label.draw(at: NSPoint(x: center - size.width / 2, y: bounds.height - 14), withAttributes: attributes)

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
        let contract = ContractRect(x: 0, y: 0, width: Int(bounds.width), height: Int(bounds.height))
        setValue(BrushSizeKnob.value(atY: Int(point.y), in: contract))
    }

    private func setValue(_ newValue: Double) {
        value = newValue
        onValueChanged?(newValue)
        needsDisplay = true
        setAccessibilityValue("\(Int(newValue))ピクセル")
    }
}
