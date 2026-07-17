// Photoshop-style navigator math (V1.2). A direct port of the Windows shell's
// NavigatorMath (apps/windows/AkapenProbe/Ui/UiContracts.cs) so both shells
// share one geometry contract: thumbnail letterboxing, thumbnail ↔ image
// mapping, canvas-corner → image mapping for the viewport polygon, and the
// pan that centers the view on a clicked image point.

import Foundation

public struct NavigatorRect: Equatable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

public enum NavigatorMath {
    /// The letterboxed placement of the image inside the thumbnail box.
    public static func imagePlacement(box: NavigatorRect, imageW: Double, imageH: Double) -> NavigatorRect {
        guard imageW > 0, imageH > 0, box.width > 0, box.height > 0 else { return box }
        let scale = min(box.width / imageW, box.height / imageH)
        let w = max(1, (imageW * scale).rounded())
        let h = max(1, (imageH * scale).rounded())
        return NavigatorRect(
            x: box.x + (box.width - w) / 2, y: box.y + (box.height - h) / 2,
            width: w, height: h)
    }

    /// Thumbnail point → image pixel (clamped to the image bounds).
    public static func thumbToImage(placement: NavigatorRect, imageW: Double, imageH: Double,
                                    x: Double, y: Double) -> (x: Double, y: Double) {
        let ix = (x - placement.x) / max(1, placement.width) * imageW
        let iy = (y - placement.y) / max(1, placement.height) * imageH
        return (min(max(ix, 0), imageW), min(max(iy, 0), imageH))
    }

    /// Image pixel → thumbnail point.
    public static func imageToThumb(placement: NavigatorRect, imageW: Double, imageH: Double,
                                    imageX: Double, imageY: Double) -> (x: Double, y: Double) {
        (placement.x + imageX / max(1, imageW) * placement.width,
         placement.y + imageY / max(1, imageH) * placement.height)
    }

    /// Canvas (view) point → image pixel, mirroring the canvas view transform
    /// (center = canvas/2 + pan, then unrotate and unscale around the image
    /// center).
    public static func canvasToImage(x: Double, y: Double, canvasW: Double, canvasH: Double,
                                     panX: Double, panY: Double, zoom: Double, rotationDeg: Double,
                                     imageW: Double, imageH: Double) -> (x: Double, y: Double) {
        let cx = canvasW / 2 + panX
        let cy = canvasH / 2 + panY
        let a = -rotationDeg * .pi / 180
        let dx = (x - cx) / max(0.01, zoom)
        let dy = (y - cy) / max(0.01, zoom)
        return (dx * cos(a) - dy * sin(a) + imageW / 2,
                dx * sin(a) + dy * cos(a) + imageH / 2)
    }

    /// The pan that centers the view on the given image pixel
    /// (pan = −zoom · R(θ) · (p − imageCenter)).
    public static func panToCenter(onImageX imageX: Double, imageY: Double,
                                   imageW: Double, imageH: Double,
                                   zoom: Double, rotationDeg: Double) -> (x: Double, y: Double) {
        let theta = rotationDeg * .pi / 180
        let dx = imageX - imageW / 2
        let dy = imageY - imageH / 2
        return (-zoom * (cos(theta) * dx - sin(theta) * dy),
                -zoom * (sin(theta) * dx + cos(theta) * dy))
    }
}
