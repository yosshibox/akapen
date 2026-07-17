public enum WorkspacePhase: Equatable {
    case empty
    case loading
    case loaded
}

public struct WorkspacePresentation: Equatable {
    public let phase: WorkspacePhase

    public init(phase: WorkspacePhase) {
        self.phase = phase
    }

    public var showsEmptyState: Bool { phase == .empty }
    public var showsLoadingState: Bool { phase == .loading }
    public var showsCanvas: Bool { phase == .loaded }
    public var showsToolDock: Bool { phase == .loaded }
    public var acceptsImageDrop: Bool { phase != .loading }
}

public enum AkapenUIMetrics {
    public static let toolDockHeight = 96
    public static let statusHeight = 24
    // V1.2: the Windows V1.0/V1.1 baseline brush fader (spec: 縦長の左右対称
    // 扇形フェーダー+円形プレビュー+px 数値の即時同期). Mirrors the Windows
    // DockLayout.SizeControlWidth/Height (88 × flexible, canonical 248).
    public static let sizeControlWidth = 88
    public static let sizeControlHeight = 220
    /// V1.2 (Windows V1.1 の写像): the right dock width shared with the
    /// Windows shell's DockLayout.Width (168 DIP).
    public static let dockWidth = 168
    public static let navigatorThumbHeight = 96
}

public enum LoadedDockContract {
    /// V1.2 (Windows V1.1 parity): 矢印(操作なし)・ペン・消しゴム.
    public static let permanentCommands = ["tool.arrow", "tool.pen", "tool.eraser"]
}

public enum SwatchShape {
    case circle
}

public enum PaletteContract {
    public static let colorCount = 10
    /// V1.2: two symmetric columns × five rows in the vertical right dock
    /// (Windows V1.1 と同構成).
    public static let rowCount = 5
    public static let columnCount = 2
    public static let shape = SwatchShape.circle
    public static let minimumDiameter = 14
}

public struct ContractRect: Equatable {
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int

    public init(x: Int, y: Int, width: Int, height: Int) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var bottom: Int { y + height }
}

public enum BrushSizeKey {
    case up, down, pageUp, pageDown, home, end
}

public enum BrushSizeKnob {
    public static let minimum = 1.0
    public static let maximum = 50.0
    // V1.2: the Windows BrushFader contract — a symmetric fan fill on a large
    // rail with a circular preview and live px readout; no triangle cap, no
    // knob, no tick marks.
    public static let hasDownwardTriangleCap = false
    public static let hasLargeRail = true
    public static let hasKnob = false
    public static let hasTickMarks = false
    public static let usesSymmetricFanFill = true
    /// Rail insets inside the control (Windows BrushFader.TrackBounds):
    /// the circular preview lives above the rail, the px readout below it.
    public static let trackInsetTop = 70
    public static let trackInsetBottom = 32

    public static func clamp(_ value: Double) -> Double {
        value.isFinite ? min(maximum, max(minimum, value)) : minimum
    }

    /// The clickable rail inside the whole control (Windows TrackBounds).
    public static func trackBounds(in control: ContractRect) -> ContractRect {
        ContractRect(
            x: control.x, y: control.y + trackInsetTop, width: control.width,
            height: max(1, control.height - trackInsetTop - trackInsetBottom))
    }

    public static func value(atY y: Int, in bounds: ContractRect) -> Double {
        guard bounds.height > 1 else { return minimum }
        let ratio = min(1, max(0, Double(y - bounds.y) / Double(bounds.height - 1)))
        return (maximum - ratio * (maximum - minimum)).rounded()
    }

    /// Inverse of `value(atY:in:)` for drawing the fill.
    public static func y(forValue value: Double, in bounds: ContractRect) -> Int {
        let ratio = (maximum - clamp(value)) / (maximum - minimum)
        return bounds.y + Int((ratio * Double(max(0, bounds.height - 1))).rounded())
    }

    public static func adjust(_ value: Double, key: BrushSizeKey) -> Double {
        switch key {
        case .up: return clamp(value + 1)
        case .down: return clamp(value - 1)
        case .pageUp: return clamp(value + 5)
        case .pageDown: return clamp(value - 5)
        case .home: return minimum
        case .end: return maximum
        }
    }
}

public enum FlickerContract {
    public static let usesImplicitStateAnimation = false
    public static let usesTransition = false
}
