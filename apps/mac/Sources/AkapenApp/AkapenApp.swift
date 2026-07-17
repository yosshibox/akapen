// Akapen mac shell entry point (spec §7.1). A single review window; the Rust
// core does the drawing and saving. V1.2 mirrors the Windows V1.1.x menu
// structure into macOS-native menus (表示 / ツール / ヘルプ).

import AkapenKit
import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Product version shown by バージョン情報. SwiftPM dev runs have no bundle
/// Info.plist, so the version constant lives in code; the M-mac5 .app bundle
/// sets the same value in Info.plist.
let akapenVersion = "1.2.0"

@main
struct AkapenApp: App {
    @StateObject private var state = AppState()
    @AppStorage(AkapenSettingsKey.pressureEnabled) private var pressureEnabled = true

    init() {
        // Dev runs (`swift run AkapenApp`) have no bundle icon; the .app
        // bundle (M-mac5) carries the same artwork as an .icns.
        if let url = Bundle.module.url(forResource: "akapen-icon", withExtension: "png"),
           let icon = NSImage(contentsOf: url) {
            NSApplication.shared.applicationIconImage = icon
        }
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(state)
        }
        .commands {
            CommandGroup(replacing: .appInfo) {
                Button("Akapen について") { showAbout() }
            }
            CommandGroup(replacing: .newItem) {} // no "New" — this is a reviewer
            CommandGroup(after: .newItem) {
                Button("開く…") { openPanel() }
                    .keyboardShortcut("o", modifiers: .command)
            }
            CommandGroup(replacing: .saveItem) {
                Button("保存") { _ = state.save() }
                    .keyboardShortcut("s", modifiers: .command)
                    .disabled(state.engine == nil)
            }
            CommandGroup(replacing: .undoRedo) {
                Button("取り消す") { state.undo() }
                    .keyboardShortcut("z", modifiers: .command)
                    .disabled(!state.canUndo)
                Button("やり直す") { state.redo() }
                    .keyboardShortcut("z", modifiers: [.command, .shift])
                    .disabled(!state.canRedo)
            }
            // 表示 (Windows 正本の「表示」メニュー写像)。キー操作はキャンバスの
            // keymap プリセットが担うため、ここでは項目のみ提供する(プリセット
            // 差のあるキーをメニュー側で固定してしまわないため)。
            CommandMenu("表示") {
                Button("前の画像") { state.step(forward: false) }
                    .disabled(state.engine == nil)
                Button("次の画像") { state.step(forward: true) }
                    .disabled(state.engine == nil)
                Divider()
                Button("ウインドウに合わせる") { state.requestCanvas(.fit) }
                    .disabled(state.engine == nil)
                Button("実寸（100%）") { state.requestCanvas(.actualSize) }
                    .disabled(state.engine == nil)
                Divider()
                Button("左に回転（15°）") { state.requestCanvas(.rotateLeft) }
                    .disabled(state.engine == nil)
                Button("右に回転（15°）") { state.requestCanvas(.rotateRight) }
                    .disabled(state.engine == nil)
            }
            // ツール (Windows 正本の「ツール」メニュー写像)。
            CommandMenu("ツール") {
                Picker("", selection: Binding(
                    get: { state.tool },
                    set: { state.tool = $0; state.applyToolState() }
                )) {
                    Text("ペン").tag(AkapenTool.pen)
                    Text("消しゴム").tag(AkapenTool.eraser)
                }
                .pickerStyle(.inline)
                .disabled(state.engine == nil)
                Divider()
                Menu("筆圧") {
                    Toggle("筆圧を使う", isOn: $pressureEnabled)
                    Divider()
                    Picker("筆圧カーブ", selection: Binding(
                        get: { state.pressureCurve },
                        set: { state.pressureCurve = $0; state.applyToolState() }
                    )) {
                        Text("標準").tag(AkapenPressureCurve.normal)
                        Text("やわらかめ").tag(AkapenPressureCurve.soft)
                        Text("かため").tag(AkapenPressureCurve.hard)
                    }
                    .pickerStyle(.inline)
                    .disabled(!pressureEnabled)
                }
            }
            CommandGroup(replacing: .help) {
                Button("Akapen ヘルプ") { openManual() }
                Divider()
                Button("バージョン情報") { showAbout() }
            }
        }

        // §4.7 + V1.2: 設定ウィンドウ。macOS 標準の Cmd+, で開く。
        Settings {
            SettingsView()
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

    /// ヘルプ → Akapen ヘルプ: opens the bundled HTML manual in the default
    /// browser (Windows V1.1.2 と同挙動). Falls back to the repository copy's
    /// URL when the resource is missing.
    private func openManual() {
        if let url = Bundle.module.url(forResource: "akapen-manual-ja", withExtension: "html") {
            NSWorkspace.shared.open(url)
        } else if let url = URL(string: "https://github.com/yosshibox/akapen/blob/main/docs/manual/akapen-manual-ja.md") {
            NSWorkspace.shared.open(url)
        }
    }

    private func showAbout() {
        let alert = NSAlert()
        alert.messageText = "Akapen"
        alert.informativeText = "Akapen Version \(akapenVersion) macOS\n(C) 2026 Yoshino Yoshikawa"
        alert.alertStyle = .informational
        if let url = Bundle.module.url(forResource: "akapen-icon", withExtension: "png"),
           let icon = NSImage(contentsOf: url) {
            alert.icon = icon
        }
        alert.runModal()
    }
}
