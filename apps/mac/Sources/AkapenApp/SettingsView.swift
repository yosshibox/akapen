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
    static let dirMode = "output.dirMode"
    static let subfolderName = "output.subfolderName"
    static let fixedDir = "output.fixedDir"
    static let flatSuffix = "output.flatSuffix"
    static let strokesSuffix = "output.strokesSuffix"
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

    var body: some View {
        Form {
            Section("出力先(§4.7)") {
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
            }

            Section("ファイル名の接尾辞(§4.3 / §4.7)") {
                suffixField(
                    label: "フラット PNG",
                    example: "<stem>.<suffix>.png",
                    text: $flatSuffix,
                    defaultValue: AkapenSettingsDefault.flatSuffix
                )
                suffixField(
                    label: "ストローク(PNG + JSON)",
                    example: "<stem>.<suffix>.png / .json",
                    text: $strokesSuffix,
                    defaultValue: AkapenSettingsDefault.strokesSuffix
                )
            }

            Section {
                Text("設定はこの Mac のユーザーごとに保存されます(OS 標準の設定置き場)。プロジェクト単位の上書きは将来検討です。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
        }
        .padding(20)
        .frame(width: 460)
    }

    // MARK: - besideInput モードのサブフォルダ名フィールド

    private var subfolderField: some View {
        let invalid = !isValidSubfolder(subfolderName)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("サブフォルダ名")
                Spacer()
                TextField(AkapenSettingsDefault.subfolderName, text: $subfolderName)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 220)
                    .overlay(
                        RoundedRectangle(cornerRadius: 4)
                            .stroke(Color.red, lineWidth: invalid ? 2 : 0)
                    )
            }
            Text(invalid
                ? "パス区切り(/ や \\)や \".\" / \"..\" は使えません。空欄のときは既定の \"\(AkapenSettingsDefault.subfolderName)\" を使います。"
                : "書き出し先は <入力ファイルのあるフォルダ>/\(subfolderName.isEmpty ? AkapenSettingsDefault.subfolderName : subfolderName)/ になります。")
                .font(.caption)
                .foregroundColor(invalid ? .red : .secondary)
        }
    }

    // MARK: - fixedAbsolute モードの絶対パスフィールド

    private var fixedDirField: some View {
        let invalid = !isValidFixedDir(fixedDir)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("固定パス")
                TextField("/path/to/reviews", text: $fixedDir)
                    .textFieldStyle(.roundedBorder)
                    .overlay(
                        RoundedRectangle(cornerRadius: 4)
                            .stroke(Color.red, lineWidth: invalid ? 2 : 0)
                    )
                Button("選択…") { chooseFixedDir() }
            }
            Text(invalid
                ? "存在する絶対パス(/ で始まる)を指定してください。無効な指定はビルド時に入力フォルダ相対の \"\(AkapenSettingsDefault.subfolderName)\" にフォールバックします。"
                : "全案件で共通の review 置き場として使います。")
                .font(.caption)
                .foregroundColor(invalid ? .red : .secondary)
        }
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

    // MARK: - サフィックスフィールド共通

    private func suffixField(
        label: String, example: String, text: Binding<String>, defaultValue: String
    ) -> some View {
        let invalid = !isValidSuffix(text.wrappedValue)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(label)
                Spacer()
                TextField(defaultValue, text: text)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 220)
                    .overlay(
                        RoundedRectangle(cornerRadius: 4)
                            .stroke(Color.red, lineWidth: invalid ? 2 : 0)
                    )
            }
            Text(invalid
                ? "パス区切り(/ \\)やドット(.)を含めない、空でない文字列を指定してください。無効な入力は既定 \"\(defaultValue)\" に戻ります。"
                : "例: \(example.replacingOccurrences(of: "<suffix>", with: text.wrappedValue.isEmpty ? defaultValue : text.wrappedValue))")
                .font(.caption)
                .foregroundColor(invalid ? .red : .secondary)
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
