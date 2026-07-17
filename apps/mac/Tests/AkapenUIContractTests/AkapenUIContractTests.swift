import AkapenUIContract
import XCTest

final class AkapenUIContractTests: XCTestCase {
    func testEmptyLoadingAndLoadedVisibility() {
        let empty = WorkspacePresentation(phase: .empty)
        XCTAssertTrue(empty.showsEmptyState)
        XCTAssertFalse(empty.showsCanvas)
        XCTAssertFalse(empty.showsToolDock)
        XCTAssertTrue(empty.acceptsImageDrop)

        let loading = WorkspacePresentation(phase: .loading)
        XCTAssertTrue(loading.showsLoadingState)
        XCTAssertFalse(loading.showsCanvas)
        XCTAssertFalse(loading.showsToolDock)

        let loaded = WorkspacePresentation(phase: .loaded)
        XCTAssertFalse(loaded.showsEmptyState)
        XCTAssertTrue(loaded.showsCanvas)
        XCTAssertTrue(loaded.showsToolDock)
    }

    func testLoadedDockIsLightweightAndContainsNoDocumentOrZoomButtons() {
        XCTAssertEqual(AkapenUIMetrics.toolDockHeight, 96)
        XCTAssertEqual(AkapenUIMetrics.statusHeight, 24)
        XCTAssertEqual(LoadedDockContract.permanentCommands, ["tool.arrow", "tool.pen", "tool.eraser"], "V1.2: 矢印・ペン・消しゴムの3ボタン(Windows V1.1 parity)")
    }

    func testPaletteIsTenCircularSwatchesInTwoColumns() {
        // V1.2: two symmetric columns x five rows in the vertical right dock
        // (Windows V1.1 parity).
        XCTAssertEqual(PaletteContract.colorCount, 10)
        XCTAssertEqual(PaletteContract.rowCount, 5)
        XCTAssertEqual(PaletteContract.columnCount, 2)
        XCTAssertEqual(PaletteContract.shape, .circle)
        XCTAssertGreaterThanOrEqual(PaletteContract.minimumDiameter, 14)
    }

    func testBrushFaderMatchesWindowsFanContract() {
        // V1.2: the Windows BrushFader — a tall symmetric fan with a circular
        // preview above the rail and a live px readout below it.
        XCTAssertEqual(AkapenUIMetrics.sizeControlWidth, 88)
        XCTAssertGreaterThanOrEqual(AkapenUIMetrics.sizeControlHeight, 200)
        XCTAssertFalse(BrushSizeKnob.hasDownwardTriangleCap)
        XCTAssertTrue(BrushSizeKnob.hasLargeRail)
        XCTAssertFalse(BrushSizeKnob.hasKnob)
        XCTAssertFalse(BrushSizeKnob.hasTickMarks)
        XCTAssertTrue(BrushSizeKnob.usesSymmetricFanFill)

        let control = ContractRect(x: 0, y: 0, width: 88, height: 220)
        let rail = BrushSizeKnob.trackBounds(in: control)
        XCTAssertEqual(rail.y, 70, "preview circle sits above the rail")
        XCTAssertEqual(rail.bottom, 220 - 32, "px readout sits below the rail")
        XCTAssertEqual(BrushSizeKnob.value(atY: rail.y, in: rail), 50, "rail top = max size")
        XCTAssertEqual(BrushSizeKnob.value(atY: rail.bottom - 1, in: rail), 1, "rail bottom = min size")
        XCTAssertEqual(BrushSizeKnob.y(forValue: 50, in: rail), rail.y)
        XCTAssertEqual(BrushSizeKnob.y(forValue: 1, in: rail), rail.bottom - 1)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .up), 11)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .pageDown), 5)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .home), 1)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .end), 50)
    }

    func testMacStateReplacementHasNoImplicitTransitionAnimation() {
        XCTAssertFalse(FlickerContract.usesImplicitStateAnimation)
        XCTAssertFalse(FlickerContract.usesTransition)
    }
}

final class NavigatorContractTests: XCTestCase {
    func testImagePlacementLetterboxesAndKeepsAspect() {
        let box = NavigatorRect(x: 0, y: 0, width: 144, height: 96)
        let place = NavigatorMath.imagePlacement(box: box, imageW: 1920, imageH: 1080)
        // 16:9 into a 3:2 box: width-bound, letterboxed vertically.
        XCTAssertEqual(place.width, 144)
        XCTAssertEqual(place.height, 81)
        XCTAssertEqual(place.x, 0)
        XCTAssertEqual(place.y, (96 - 81) / 2, accuracy: 0.5)
        XCTAssertEqual(place.width / place.height, 1920.0 / 1080.0, accuracy: 0.05)
    }

    func testThumbCenterMapsToImageCenterAndBack() {
        let box = NavigatorRect(x: 0, y: 0, width: 144, height: 96)
        let place = NavigatorMath.imagePlacement(box: box, imageW: 1920, imageH: 1080)
        let img = NavigatorMath.thumbToImage(
            placement: place, imageW: 1920, imageH: 1080,
            x: place.x + place.width / 2, y: place.y + place.height / 2)
        XCTAssertEqual(img.x, 960, accuracy: 15)
        XCTAssertEqual(img.y, 540, accuracy: 15)
        let back = NavigatorMath.imageToThumb(
            placement: place, imageW: 1920, imageH: 1080, imageX: img.x, imageY: img.y)
        XCTAssertEqual(back.x, place.x + place.width / 2, accuracy: 1)
        XCTAssertEqual(back.y, place.y + place.height / 2, accuracy: 1)
    }

    func testCanvasCenterMapsToImageCenterAtFit() {
        let img = NavigatorMath.canvasToImage(
            x: 500, y: 300, canvasW: 1000, canvasH: 600,
            panX: 0, panY: 0, zoom: 1, rotationDeg: 0, imageW: 1920, imageH: 1080)
        XCTAssertEqual(img.x, 960, accuracy: 0.001)
        XCTAssertEqual(img.y, 540, accuracy: 0.001)
    }

    func testPanToCenterMatchesInverseTransform() {
        // Centering on an arbitrary point must place it at the canvas center,
        // including under rotation (round-trip through canvasToImage).
        let (panX, panY) = NavigatorMath.panToCenter(
            onImageX: 1500, imageY: 200, imageW: 1920, imageH: 1080,
            zoom: 2.0, rotationDeg: 30)
        let img = NavigatorMath.canvasToImage(
            x: 500, y: 300, canvasW: 1000, canvasH: 600,
            panX: panX, panY: panY, zoom: 2.0, rotationDeg: 30,
            imageW: 1920, imageH: 1080)
        XCTAssertEqual(img.x, 1500, accuracy: 0.001)
        XCTAssertEqual(img.y, 200, accuracy: 0.001)
        // Centering on the image center needs no pan.
        let center = NavigatorMath.panToCenter(
            onImageX: 960, imageY: 540, imageW: 1920, imageH: 1080,
            zoom: 2.0, rotationDeg: 0)
        XCTAssertEqual(center.x, 0, accuracy: 0.001)
        XCTAssertEqual(center.y, 0, accuracy: 0.001)
    }

    func testDockMetricsMatchWindowsContract() {
        XCTAssertEqual(AkapenUIMetrics.dockWidth, 168, "dock width mirrors Windows DockLayout.Width")
        XCTAssertEqual(PaletteContract.rowCount, 5)
        XCTAssertEqual(PaletteContract.columnCount, 2)
        XCTAssertEqual(PaletteContract.colorCount, 10)
    }
}
