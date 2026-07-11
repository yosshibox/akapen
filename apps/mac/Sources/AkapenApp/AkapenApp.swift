// Akapen mac shell entry point (spec §7.1). A single review window; the Rust
// core does the drawing and saving.

import AppKit
import SwiftUI
import UniformTypeIdentifiers

@main
struct AkapenApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(state)
        }
        .commands {
            CommandGroup(replacing: .newItem) {} // no "New" — this is a reviewer
            CommandGroup(after: .newItem) {
                Button("Open…") { openPanel() }
                    .keyboardShortcut("o", modifiers: .command)
            }
        }
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .webP, .bmp]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url {
            state.open(url: url)
        }
    }
}
