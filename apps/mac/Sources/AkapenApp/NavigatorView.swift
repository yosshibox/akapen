// Photoshop-style navigator (V1.2, Windows V1.1 の写像): the document
// thumbnail with a live viewport polygon, plus a zoom row (− / percentage /
// +) under it. Click/drag inside the thumbnail recenters the view; the math
// is the shared NavigatorMath contract (same formulas as the Windows shell).

import AkapenUIContract
import AppKit
import SwiftUI

struct NavigatorView: View {
    @ObservedObject var state: AppState

    private let thumbHeight: CGFloat = 96

    var body: some View {
        VStack(spacing: 4) {
            GeometryReader { proxy in
                let box = NavigatorRect(
                    x: 0, y: 0,
                    width: Double(proxy.size.width), height: Double(proxy.size.height))
                let placement = NavigatorMath.imagePlacement(
                    box: box,
                    imageW: Double(state.imageSize.width),
                    imageH: Double(state.imageSize.height))
                ZStack {
                    Color.white
                    if let thumb = state.navThumbnail {
                        Image(nsImage: thumb)
                            .resizable()
                            .interpolation(.high)
                            .frame(width: CGFloat(placement.width), height: CGFloat(placement.height))
                            .position(
                                x: CGFloat(placement.x + placement.width / 2),
                                y: CGFloat(placement.y + placement.height / 2))
                    }
                    viewportPolygon(placement: placement)
                        .stroke(Color(red: 0.78, green: 0.26, blue: 0.20), lineWidth: 1.6)
                }
                .clipped()
                .contentShape(Rectangle())
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { value in centerView(at: value.location, placement: placement) }
                )
            }
            .frame(height: thumbHeight)
            .overlay {
                Rectangle().stroke(Color.primary.opacity(0.25), lineWidth: 1)
            }
            .accessibilityLabel("ナビゲーター")

            HStack(spacing: 4) {
                zoomButton("minus", help: "ズームアウト (↓)") { state.requestCanvas(.zoomOut) }
                Text("\(Int((state.viewZoom * 100).rounded()))%")
                    .font(.system(size: 11, weight: .medium).monospacedDigit())
                    .frame(maxWidth: .infinity)
                    .accessibilityLabel("表示倍率")
                zoomButton("plus", help: "ズームイン (↑)") { state.requestCanvas(.zoomIn) }
            }
        }
    }

    /// The canvas viewport mapped into thumbnail space. A polygon (not a
    /// rect): under rotation the viewport is a rotated quadrilateral, exactly
    /// like Photoshop's navigator.
    private func viewportPolygon(placement: NavigatorRect) -> Path {
        var path = Path()
        let cw = Double(state.canvasSize.width)
        let ch = Double(state.canvasSize.height)
        guard cw > 0, ch > 0, state.imageSize.width > 0 else { return path }
        let corners: [(Double, Double)] = [(0, 0), (cw, 0), (cw, ch), (0, ch)]
        let points = corners.map { corner -> CGPoint in
            let img = NavigatorMath.canvasToImage(
                x: corner.0, y: corner.1, canvasW: cw, canvasH: ch,
                panX: state.viewPanX, panY: state.viewPanY,
                zoom: state.viewZoom, rotationDeg: state.viewRotationDeg,
                imageW: Double(state.imageSize.width), imageH: Double(state.imageSize.height))
            let t = NavigatorMath.imageToThumb(
                placement: placement,
                imageW: Double(state.imageSize.width), imageH: Double(state.imageSize.height),
                imageX: img.x, imageY: img.y)
            return CGPoint(x: t.x, y: t.y)
        }
        path.move(to: points[0])
        for point in points.dropFirst() { path.addLine(to: point) }
        path.closeSubpath()
        return path
    }

    private func centerView(at location: CGPoint, placement: NavigatorRect) {
        guard state.imageSize.width > 0 else { return }
        let img = NavigatorMath.thumbToImage(
            placement: placement,
            imageW: Double(state.imageSize.width), imageH: Double(state.imageSize.height),
            x: Double(location.x), y: Double(location.y))
        state.requestCanvas(.centerOn(img.x, img.y))
    }

    private func zoomButton(_ symbol: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 10, weight: .semibold))
                .frame(width: 24, height: 20)
        }
        .buttonStyle(.bordered)
        .help(help)
    }
}
