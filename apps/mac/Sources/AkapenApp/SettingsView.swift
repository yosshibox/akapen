// 出力先・接尾辞の設定パネル(spec §4.7)。
//
// macOS の Settings シーンに載る単一の SwiftUI ビュー。ユーザーの選択は
// @AppStorage 経由で UserDefaults(=OS 標準の設定置き場)に保存し、実際の
// 保存ダイアログや export 呼び出しは AppState.save() 側が同じキーから読み戻す。
// アプリローカルな設定にとどめる方針(§4.7 「プロジェクトごとの上書きは将来検討」)
// なので、ここではプロジェクト直下の設定ファイルは扱わない。
//
// 無効な入力(空文字・パス区切りやドットを含む接尾辞など)は視覚的にフラグ
// (赤枠+ヘルプ文言)して意図を伝える。それでも @AppStorage は「生の文字列」を
// そのまま保持するため、読み出し側(AppState と C ABI 側 sanitize_suffix)で
// もう一度 defaulting を挟む二段構えにしてある。

import AppKit
import SwiftUI

/// 出力先モード(§4.7): 既定は入力フォルダ相対、固定絶対パスへ切替可能。
///
/// `String, RawRepresentable` にして @AppStorage に直接載せられるようにしている
/// (`AppStorage` は生の enum を書き込めないため、raw を String に絞る)。
enum AkapenOutputDirMode: String, CaseIterable, Identifiable {
    /// `<入力フォルダ>/<subfolderName>/` へ書く(§4.7 (a))。
    case besideInput
    /// あらかじめ選んだ絶対パスに一本化する(§4.7 (b))。
    case fixedAbsolute

    var id: String { rawValue }

    var label: String {
        switch self {
        case .besideInput: return "入力フォルダの中(相対)"
        case .fixedAbsolute: return "固定の絶対パス"
        }
    }
}

/// @AppStorage で共有するキー名。AppState 側も UserDefaults.standard から
/// 同じキーで読み戻すので、必ずここを唯一の出典にする。
enum AkapenSettingsKey {
    static let dockSide = "ui.dockSide"
    static let dirMode = "output.dirMode"
    static let subfolderName = "output.subfolderName"
    static let fixedDir = "output.fixedDir"
    static let flatSuffix = "output.flatSuffix"
    static let strokesSuffix = "output.strokesSuffix"
    // V1.2 (Windows V1.1.x の写像)。キー名は Windows の settings.json と 1:1。
    static let keymapPreset = "keymap.preset"
    static let pressureEnabled = "input.pressure"
}

/// キーマッププリセット(V1.2)。raw 値は Windows 側の設定値と同一で、
/// C ABI の AKAPEN_KEYMAP_* へは `code` で写像する。既定は Photoshop 準拠。
enum AkapenKeymapPreset: String, CaseIterable, Identifiable {
    case photoshop
    case clipstudio

    var id: String { rawValue }

    var label: String {
        switch self {
        case .photoshop: return "Photoshop 準拠（既定）"
        case .clipstudio: return "CLIP STUDIO PAINT 準拠"
        }
    }

    var code: Int32 {
        switch self {
        case .photoshop: return 1  // AKAPEN_KEYMAP_PHOTOSHOP
        case .clipstudio: return 0  // AKAPEN_KEYMAP_CLIPSTUDIO
        }
    }

    /// UserDefaults の生文字列から(未知の値は既定へ)。
    static func from(raw: String?) -> AkapenKeymapPreset {
        AkapenKeymapPreset(rawValue: raw ?? "") ?? .photoshop
    }
}

/// 既定値。@AppStorage の初期値と、AppState 側のフォールバックで共有する。
enum AkapenSettingsDefault {
    static let dirMode = AkapenOutputDirMode.besideInput
    static let subfolderName = "_review"
    static let fixedDir = ""
    static let flatSuffix = "review"
    static let strokesSuffix = "strokes"
}

struct SettingsView: View {
    @AppStorage(AkapenSettingsKey.dirMode) private var dirModeRaw: String =
        AkapenSettingsDefault.dirMode.rawValue
    @AppStorage(AkapenSettingsKey.subfolderName) private var subfolderName: String =
        AkapenSettingsDefault.subfolderName
    @AppStorage(AkapenSettingsKey.fixedDir) private var fixedDir: String =
        AkapenSettingsDefault.fixedDir
    @AppStorage(AkapenSettingsKey.flatSuffix) private var flatSuffix: String =
        AkapenSettingsDefault.flatSuffix
    @AppStorage(AkapenSettingsKey.strokesSuffix) private var strokesSuffix: String =
        AkapenSettingsDefault.strokesSuffix
    private var dirMode: AkapenOutputDirMode {
        AkapenOutputDirMode(rawValue: dirModeRaw) ?? AkapenSettingsDefault.dirMode
    }

    @AppStorage(AkapenSettingsKey.keymapPreset) private var keymapPresetRaw: String =
        AkapenKeymapPreset.photoshop.rawValue
    @AppStorage(AkapenSettingsKey.pressureEnabled) private var pressureEnabled: Bool = true

    var body: some View {
        // macOS HIG: 設定ウィンドウは grouped Form(System Settings 様式)。
        // 行のレイアウトは Form に任せ、独自の HStack/Spacer で崩さない。
        Form {
            Section {
                Picker("キーマップ", selection: Binding(
                    get: { AkapenKeymapPreset.from(raw: keymapPresetRaw) },
                    set: { keymapPresetRaw = $0.rawValue }
                )) {
                    ForEach(AkapenKeymapPreset.allCases) { preset in
                        Text(preset.label).tag(preset)
                    }
                }
                .pickerStyle(.radioGroup)
            } header: {
                Text("ショートカット")
            } footer: {
                Text("Photoshop 準拠: B=ブラシ、⌘⇧Z=やり直し、⌘1=100%、R / ⇧R=回転(15°)\nCLIP STUDIO 準拠: P=ペン、⌘Y=やり直し、- / ^=回転(15°)")
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                Toggle("筆圧を使う（線の太さに反映する）", isOn: $pressureEnabled)
            } header: {
                Text("ペン")
            } footer: {
                Text("オフのときは筆圧を無視し、一定の太さで描きます。")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                Picker("方式", selection: Binding(
                    get: { dirMode },
                    set: { dirModeRaw = $0.rawValue }
                )) {
                    ForEach(AkapenOutputDirMode.allCases) { mode in
                        Text(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.radioGroup)

                switch dirMode {
                case .besideInput:
                    subfolderField
                case .fixedAbsolute:
                    fixedDirField
                }
            } header: {
                Text("保存先")
            } footer: {
                outputFooter.frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                suffixField(label: "フラット PNG", text: $flatSuffix,
                            defaultValue: AkapenSettingsDefault.flatSuffix)
                suffixField(label: "ストローク (PNG + JSON)", text: $strokesSuffix,
                            defaultValue: AkapenSettingsDefault.strokesSuffix)
            } header: {
                Text("ファイル名の接尾辞")
            } footer: {
                suffixFooter.frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
            } footer: {
                Text("設定はこの Mac のユーザーごとに保存されます（OS 標準の設定置き場）。")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .formStyle(.grouped)
        .frame(width: 600, height: 620)
        .environment(\.defaultMinListRowHeight, 34)
    }

    // MARK: - 保存先の行

    private var subfolderField: some View {
        TextField("サブフォルダ名", text: $subfolderName,
                  prompt: Text(AkapenSettingsDefault.subfolderName))
    }

    private var fixedDirField: some View {
        LabeledContent("固定パス") {
            HStack(spacing: 8) {
                TextField("", text: $fixedDir, prompt: Text("/path/to/reviews"))
                    .labelsHidden()
                Button("選択…") { chooseFixedDir() }
            }
        }
    }

    @ViewBuilder private var outputFooter: some View {
        switch dirMode {
        case .besideInput:
            if isValidSubfolder(subfolderName) {
                let name = subfolderName.isEmpty ? AkapenSettingsDefault.subfolderName : subfolderName
                Text("書き出し先は <入力ファイルのあるフォルダ>/\(name)/ になります。")
            } else {
                Text("パス区切り（/ や \\）や「.」「..」は使えません。空欄のときは既定の「\(AkapenSettingsDefault.subfolderName)」を使います。")
                    .foregroundStyle(.red)
            }
        case .fixedAbsolute:
            if isValidFixedDir(fixedDir) {
                Text("全案件で共通の review 置き場として使います。")
            } else {
                Text("存在する絶対パス（/ で始まる）を指定してください。無効な指定は入力フォルダ相対の「\(AkapenSettingsDefault.subfolderName)」にフォールバックします。")
                    .foregroundStyle(.red)
            }
        }
    }

    @ViewBuilder private var suffixFooter: some View {
        if !isValidSuffix(flatSuffix) || !isValidSuffix(strokesSuffix) {
            Text("接尾辞にはパス区切り（/ \\）やドット（.）を含めない、空でない文字列を指定してください。無効な入力は既定値に戻ります。")
                .foregroundStyle(.red)
        } else {
            let flat = flatSuffix.isEmpty ? AkapenSettingsDefault.flatSuffix : flatSuffix
            let strokes = strokesSuffix.isEmpty ? AkapenSettingsDefault.strokesSuffix : strokesSuffix
            Text("例: <元ファイル名>.\(flat).png ／ <元ファイル名>.\(strokes).png / .json")
        }
    }

    // MARK: - 接尾辞の行

    private func suffixField(label: String, text: Binding<String>, defaultValue: String) -> some View {
        TextField(label, text: text, prompt: Text(defaultValue))
    }

    private func chooseFixedDir() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "選択"
        if panel.runModal() == .OK, let url = panel.url {
            fixedDir = url.path
        }
    }

}

// MARK: - 入力検証(SettingsView と AppState で共有)
//
// UI 側の可視フラグと、実際に保存する直前の defaulting で同じ判定を使う。
// C ABI 側 (`sanitize_suffix`) と同じルールで、二段構えの防衛にする。

/// 接尾辞として使える文字列か。空でなく、`/`・`\`・`.` を含まない。
func isValidSuffix(_ s: String) -> Bool {
    let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
    if trimmed.isEmpty { return false }
    if trimmed.contains("/") || trimmed.contains("\\") || trimmed.contains(".") {
        return false
    }
    return true
}

/// 入力フォルダ配下のサブフォルダ名として使える文字列か。空でなく、パス区切りを
/// 含まない(先頭のドットは `.veda/` のように普通にあり得るので許可)。
///
/// `.` / `..` は単一コンポーネントのままでも `appendingPathComponent` が
/// カレント/親ディレクトリとして解決してしまう(directory traversal)ため、
/// 明示的に拒否する。空文字を弾いた後は区切りを含まない単一の名前しか
/// 残らないので、trim 後の値そのものが `.` / `..` に完全一致するかだけを
/// 見れば十分(区切り済みなので複数コンポーネントには分かれない)。
func isValidSubfolder(_ s: String) -> Bool {
    let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
    if trimmed.isEmpty { return false }
    if trimmed.contains("/") || trimmed.contains("\\") { return false }
    if trimmed == "." || trimmed == ".." { return false }
    return true
}

/// 固定絶対パスとして使えるか。空・相対・非存在は無効(実行時にフォールバック)。
func isValidFixedDir(_ s: String) -> Bool {
    let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
    if trimmed.isEmpty { return false }
    let url = URL(fileURLWithPath: trimmed)
    guard url.path.hasPrefix("/") else { return false }
    var isDir: ObjCBool = false
    let exists = FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
    return exists && isDir.boolValue
}
