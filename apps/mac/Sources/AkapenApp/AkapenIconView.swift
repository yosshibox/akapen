// Renders the canonical Akapen icon sprite (assets/icons/akapen-ui-icons.svg,
// bundled as a resource) — the same line icons the Windows dock draws. The
// arc → cubic conversion is a port of the Windows renderer's AddSvgArc.

import AkapenUIContract
import SwiftUI

enum AkapenIcons {
    static let sprite: [String: [SvgSegment]] = {
        guard let url = Bundle.module.url(forResource: "akapen-ui-icons", withExtension: "svg"),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return [:] }
        return SvgIconSprite.parseSprite(text)
    }()
}

struct AkapenIconView: View {
    let name: String
    var color: Color = .primary

    var body: some View {
        AkapenIconShape(segments: AkapenIcons.sprite[name] ?? [])
            .stroke(color, style: StrokeStyle(
                lineWidth: CGFloat(SvgIconSprite.strokeWidth), lineCap: .round, lineJoin: .round))
            .aspectRatio(1, contentMode: .fit)
    }
}

struct AkapenIconShape: Shape {
    let segments: [SvgSegment]

    func path(in rect: CGRect) -> Path {
        var path = Path()
        let scale = min(rect.width, rect.height) / CGFloat(SvgIconSprite.viewBoxSize)
        let ox = rect.minX + (rect.width - CGFloat(SvgIconSprite.viewBoxSize) * scale) / 2
        let oy = rect.minY + (rect.height - CGFloat(SvgIconSprite.viewBoxSize) * scale) / 2
        func pt(_ x: Double, _ y: Double) -> CGPoint {
            CGPoint(x: ox + CGFloat(x) * scale, y: oy + CGFloat(y) * scale)
        }
        var cx = 0.0, cy = 0.0, sx = 0.0, sy = 0.0
        for segment in segments {
            let v = segment.values.map(Double.init)
            switch segment.kind {
            case .move:
                cx = v[0]; cy = v[1]; sx = cx; sy = cy
                path.move(to: pt(cx, cy))
            case .line:
                let lx = v[0].isNaN ? cx : v[0]
                let ly = v[1].isNaN ? cy : v[1]
                path.addLine(to: pt(lx, ly))
                cx = lx; cy = ly
            case .cubic:
                path.addCurve(to: pt(v[4], v[5]), control1: pt(v[0], v[1]), control2: pt(v[2], v[3]))
                cx = v[4]; cy = v[5]
            case .arc:
                addArc(&path, from: (cx, cy), values: v, pt: pt)
                cx = v[5]; cy = v[6]
            case .close:
                path.addLine(to: pt(sx, sy))
                path.closeSubpath()
                cx = sx; cy = sy
            }
        }
        return path
    }

    /// SVG elliptical arc → cubic beziers (same algorithm as the Windows
    /// NativeUiRenderer.AddSvgArc).
    private func addArc(_ path: inout Path, from start: (Double, Double), values v: [Double],
                        pt: (Double, Double) -> CGPoint) {
        let (x1, y1) = start
        var rx = abs(v[0]), ry = abs(v[1])
        let phi = v[2] * .pi / 180
        let large = v[3] != 0, sweep = v[4] != 0
        let x2 = v[5], y2 = v[6]
        if rx == 0 || ry == 0 || (x1 == x2 && y1 == y2) {
            path.addLine(to: pt(x2, y2))
            return
        }
        let cosP = cos(phi), sinP = sin(phi)
        let dx = (x1 - x2) / 2, dy = (y1 - y2) / 2
        let xp = cosP * dx + sinP * dy, yp = -sinP * dx + cosP * dy
        let lambda = xp * xp / (rx * rx) + yp * yp / (ry * ry)
        if lambda > 1 {
            let f = lambda.squareRoot()
            rx *= f; ry *= f
        }
        let denom = rx * rx * yp * yp + ry * ry * xp * xp
        let numer = max(0, rx * rx * ry * ry - denom)
        let coeff = (large == sweep ? -1.0 : 1.0) * (denom == 0 ? 0 : (numer / denom).squareRoot())
        let cxp = coeff * rx * yp / ry, cyp = coeff * -ry * xp / rx
        let centerX = cosP * cxp - sinP * cyp + (x1 + x2) / 2
        let centerY = sinP * cxp + cosP * cyp + (y1 + y2) / 2
        func angle(_ ux: Double, _ uy: Double, _ vx: Double, _ vy: Double) -> Double {
            atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        }
        let theta = angle(1, 0, (xp - cxp) / rx, (yp - cyp) / ry)
        var delta = angle((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry)
        if !sweep && delta > 0 { delta -= .pi * 2 } else if sweep && delta < 0 { delta += .pi * 2 }
        let pieces = max(1, Int(ceil(abs(delta) / (.pi / 2))))
        let step = delta / Double(pieces)
        func ellipsePoint(_ a: Double) -> (x: Double, y: Double, dx: Double, dy: Double) {
            let ca = cos(a), sa = sin(a)
            return (centerX + cosP * rx * ca - sinP * ry * sa,
                    centerY + sinP * rx * ca + cosP * ry * sa,
                    -cosP * rx * sa - sinP * ry * ca,
                    -sinP * rx * sa + cosP * ry * ca)
        }
        for i in 0..<pieces {
            let a = theta + Double(i) * step
            let b = a + step
            let alpha = 4.0 / 3.0 * tan(step / 4)
            let p0 = ellipsePoint(a)
            let p1 = ellipsePoint(b)
            path.addCurve(
                to: pt(p1.x, p1.y),
                control1: pt(p0.x + alpha * p0.dx, p0.y + alpha * p0.dy),
                control2: pt(p1.x - alpha * p1.dx, p1.y - alpha * p1.dy))
        }
    }
}
