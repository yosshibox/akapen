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
        XCTAssertEqual(LoadedDockContract.permanentCommands, ["tool.pen", "tool.eraser"])
    }

    func testPaletteIsTenCircularSwatchesInOneRow() {
        XCTAssertEqual(PaletteContract.colorCount, 10)
        XCTAssertEqual(PaletteContract.rowCount, 1)
        XCTAssertEqual(PaletteContract.shape, .circle)
        XCTAssertGreaterThanOrEqual(PaletteContract.minimumDiameter, 14)
    }

    func testCompactSizeKnobKeepsRangePointerAndKeyboardBehavior() {
        let bounds = ContractRect(x: 0, y: 0, width: 48, height: 72)
        XCTAssertLessThanOrEqual(AkapenUIMetrics.sizeControlWidth, 52)
        XCTAssertLessThanOrEqual(AkapenUIMetrics.sizeControlHeight, 76)
        XCTAssertEqual(BrushSizeKnob.value(atY: bounds.y, in: bounds), 50)
        XCTAssertEqual(BrushSizeKnob.value(atY: bounds.bottom - 1, in: bounds), 1)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .up), 11)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .pageDown), 5)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .home), 1)
        XCTAssertEqual(BrushSizeKnob.adjust(10, key: .end), 50)
        XCTAssertTrue(BrushSizeKnob.hasDownwardTriangleCap)
        XCTAssertFalse(BrushSizeKnob.hasLargeRail)
    }

    func testMacStateReplacementHasNoImplicitTransitionAnimation() {
        XCTAssertFalse(FlickerContract.usesImplicitStateAnimation)
        XCTAssertFalse(FlickerContract.usesTransition)
    }
}
