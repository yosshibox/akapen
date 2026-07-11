// Main window: toolbar + canvas + status bar. Deliberately minimal — the review
// loop is open → draw → save → next (spec §1.4). UI strings avoid drawing-app
// jargon; the pressure warning surfaces the spec §5.4 guard instead of silently
// drawing a flat line.

import AkapenKit
import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            ZStack {
                CanvasView(state: state)
                    .background(Color(white: 0.15))
                if state.engine == nil {
                    VStack(spacing: 8) {
                        Text("Akapen").font(.largeTitle.bold())
                        Text("Open an image (⌘O) or drag one in to start marking up.")
                            .foregroundColor(.secondary)
                    }
                }
            }
            .overlay(alignment: .trailing) {
                SidePanelView(state: state)
                    .padding(.trailing, 10)
                    .padding(.vertical, 10)
            }
            Divider()
            statusBar
        }
        .frame(minWidth: 720, minHeight: 480)
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            handleDrop(providers)
        }
    }

    private var toolbar: some View {
        HStack(spacing: 12) {
            Button("Open") { openPanel() }
            Divider().frame(height: 18)

            Picker("", selection: $state.tool) {
                Text("Pen").tag(AkapenTool.pen)
                Text("Eraser").tag(AkapenTool.eraser)
            }
            .pickerStyle(.segmented)
            .frame(width: 140)
            .onChange(of: state.tool) { _ in state.applyToolState() }

            ColorPicker(
                "",
                selection: Binding(
                    get: { state.color },
                    set: { state.setArbitraryColor($0) }
                ),
                supportsOpacity: false
            )
            .labelsHidden()
            .frame(width: 44)

            HStack(spacing: 4) {
                Text("Size")
                Slider(value: $state.brushSize, in: 1...80)
                    .frame(width: 120)
                    .onChange(of: state.brushSize) { _ in state.applyToolState() }
                Text("\(Int(state.brushSize))").monospacedDigit().frame(width: 26)
            }

            Picker("", selection: $state.pressureCurve) {
                Text("Soft").tag(AkapenPressureCurve.soft)
                Text("Normal").tag(AkapenPressureCurve.normal)
                Text("Hard").tag(AkapenPressureCurve.hard)
            }
            .frame(width: 110)
            .onChange(of: state.pressureCurve) { _ in state.applyToolState() }

            Divider().frame(height: 18)
            Button("Undo") { state.undo() }.disabled(state.engine == nil)
            Button("Redo") { state.redo() }.disabled(state.engine == nil)

            Spacer()

            Button("◀") { state.step(forward: false) }.disabled(state.engine == nil)
            Button("▶") { state.step(forward: true) }.disabled(state.engine == nil)
            Button("Save") { state.save() }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(state.engine == nil)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var statusBar: some View {
        HStack {
            Text(state.statusText).font(.caption).foregroundColor(.secondary)
            Spacer()
            if state.pressureWarning {
                Label("Pressure not detected — check the tablet driver / Windows Ink.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundColor(.orange)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .webP, .bmp]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url {
            state.open(url: url)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        guard let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url else { return }
            DispatchQueue.main.async { state.open(url: url) }
        }
        return true
    }
}
