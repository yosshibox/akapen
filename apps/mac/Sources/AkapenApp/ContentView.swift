import AkapenKit
import AkapenUIContract
import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject var state: AppState
    @State private var dropTargeted = false

    var body: some View {
        Group {
            switch state.workspacePhase {
            case .empty:
                emptyState
            case .loading:
                loadingState
            case .loaded:
                loadedWorkspace
            }
        }
        // 最小サイズは右ドック(ナビゲーター+ツール+フェーダー+パレット ≈ 640pt)
        // が欠けない高さを保証する。
        .frame(minWidth: 960, minHeight: 720)
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted, perform: handleDrop)
        .transaction { transaction in transaction.animation = nil }
    }

    private var loadedWorkspace: some View {
        // V1.2 (Windows V1.1 の写像): canvas left, 168pt right dock
        // (navigator / tools / fader / palette), status bar at the bottom.
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                CanvasView(state: state).background(Color(white: 0.15))
                Divider()
                SidePanelView(state: state)
            }
            Divider()
            statusBar
        }
    }

    private var emptyState: some View {
        VStack(spacing: 20) {
            VStack(spacing: 6) {
                // 起動ロゴ: アプリアイコン(朱の一筆)。「赤ペン」なので赤で統一
                // (Windows 版とも共通の図案。青いテンプレートアイコンは使わない)。
                if let logo = Bundle.module.url(forResource: "akapen-icon", withExtension: "png")
                    .flatMap({ NSImage(contentsOf: $0) }) {
                    Image(nsImage: logo)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: 64, height: 64)
                } else {
                    Image(systemName: "pencil.and.outline")
                        .font(.system(size: 32, weight: .regular))
                        .foregroundStyle(AkapenBrand.red)
                }
                Text("Akapen").font(.title2.weight(.semibold))
                Text("画像フォルダを選ぶか、画像をドロップして始めます。")
                    .font(.callout).foregroundStyle(.secondary)
            }

            Button(action: openFolderPanel) {
                Label("画像を開くフォルダを選択", systemImage: "folder")
                    .frame(minWidth: 220)
            }
            .controlSize(.large)
            .keyboardShortcut("o", modifiers: [.command, .shift])

            VStack(spacing: 8) {
                Image(systemName: "photo.on.rectangle.angled")
                    .font(.system(size: 24, weight: .regular))
                Text("画像をここへドロップ").font(.headline)
                Text("PNG・JPEG・WebP・BMP")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(width: 360, height: 128)
            .background(Color.primary.opacity(dropTargeted ? 0.07 : 0.025))
            .overlay {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .stroke(Color.primary.opacity(dropTargeted ? 0.45 : 0.2),
                            style: StrokeStyle(lineWidth: 1, dash: [6, 5]))
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("画像をここへドロップ")
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var loadingState: some View {
        VStack(spacing: 10) {
            Image(systemName: "hourglass")
                .font(.system(size: 24, weight: .regular))
                .foregroundStyle(.secondary)
            Text("画像を読み込んでいます…")
                .font(.callout).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var statusBar: some View {
        HStack {
            Text(state.statusText).font(.caption).foregroundColor(.secondary)
            Spacer()
            if state.pressureWarning {
                Label("Pressure not detected — check the tablet driver / pen pressure settings.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundColor(.orange)
            }
        }
        .padding(.horizontal, 10)
        .frame(height: CGFloat(AkapenUIMetrics.statusHeight))
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .webP, .bmp]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { state.open(url: url) }
    }

    private func openFolderPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.folder]
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "選択"
        if panel.runModal() == .OK, let url = panel.url { state.openFolder(url: url) }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        guard state.workspacePhase != .loading else { return false }
        guard let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url else { return }
            DispatchQueue.main.async { state.openDroppedURL(url) }
        }
        return true
    }
}
