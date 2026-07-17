# Akapen の Mac 版と Windows 版の違い

作成日: 2026-07-17
対象: Windows V1.1.2 / Mac V1.2(開発中)

UX の正本は Windows 版(`windows-v1.1-and-mac-port-baseline-2026-07-17.md`)である。
Mac 版は同じ操作体系を macOS の流儀へ翻訳する。本書は「意図した違い」を列挙し、
それ以外の差異はバグとして扱うための基準を示す。

## 1. 操作・キー

| 項目 | Windows | Mac | 補足 |
|---|---|---|---|
| 主修飾キー | Ctrl | ⌘（Command） | コア keymap の `primary` が吸収。Ctrl+Z ↔ ⌘Z など全表が同型 |
| キーマッププリセット | Photoshop 準拠（既定）/ CLIP STUDIO 準拠 | 同じ | 解決はどちらも `akapen_resolve_key_preset` |
| キーディスパッチ | ウィンドウの WndProc（フォーカス非依存） | ウィンドウ単位の NSEvent ローカルモニタ（フォーカス非依存） | テキスト入力・IME・ブラシフェーダーのフォーカス中は奪わない |
| ズームの代替キー | なし（Ctrl+Space は使える） | ⌘= / ⌘-（⌘Space が Spotlight 予約のため） | 仕様書 §3 の mac 注記 |
| メニューのキー表記 | メニューに表記なし（ツールチップで案内） | メニュー項目にキー等価を付けない | プリセットで意味が変わるキー（⌘1、⌘Y 等）をメニュー側で固定しないため |

## 2. 画面・ウィンドウ

| 項目 | Windows | Mac | 補足 |
|---|---|---|---|
| 右ドック | 168 DIP・GDI+ 自前描画 | 168 pt・SwiftUI | 構成（ナビゲーター→ツール→扇形フェーダー→2×5 パレット）は同一 |
| メニューバー | ウィンドウ内メニュー（ファイル/編集/表示/ツール/設定/ヘルプ） | macOS のシステムメニューバーへ翻訳 | 「設定」は macOS 慣習どおりアプリメニューの「設定…（⌘,）」 |
| バージョン情報 | ヘルプ→バージョン情報（MessageBox） | Akapen メニュー→Akapen について / ヘルプ→バージョン情報 | 文言は同一様式（`Akapen Version x.y.z <OS>`） |
| 起動時サイズ | 起動引数の画像原寸ベース | 最初の画像を開いたとき画像に合わせる（画面の 85% 上限） | どちらも以後のリサイズで連続フィット |
| DPI / スケール | PerMonitorV2 で自前スケール | Retina は AppKit/SwiftUI に委譲 | |

## 3. 設定

| 項目 | Windows | Mac | 補足 |
|---|---|---|---|
| 保存場所 | `%LOCALAPPDATA%\Akapen\settings.json`（プレーン JSON） | `UserDefaults`（OS 標準の設定置き場、`defaults read` で参照可） | **キー名は 1:1**（`keymap.preset`、`input.pressure`、`output.*` …）。仕様書 §4.7「アプリローカル（OS の標準設定置き場）」の各 OS 解釈 |
| 反映タイミング | OK で保存 / キャンセルで破棄（ダイアログ） | 即時反映（macOS の設定ウィンドウ慣習） | HIG に従う意図した差。破壊的な設定項目が無いため即時反映で安全 |
| 設定画面の様式 | visual styles + Segoe UI の自前ダイアログ | SwiftUI grouped Form（System Settings 様式） | 節構成（ショートカット/ペン/保存先/接尾辞）は同一 |

## 4. 入力（ペン・タッチ）

| 項目 | Windows | Mac | 補足 |
|---|---|---|---|
| 筆圧の取得経路 | WM_POINTER（Windows Ink） | NSEvent タブレットイベント（`.tabletPoint` のみ採用） | トラックパッドの Force Touch は筆圧に使わない（仕様書 §5.4） |
| 筆圧トグル | 設定＋ツール→筆圧メニュー | 同じ | `input.pressure`、既定オン。オフ時は固定 1.0 |
| タッチ | 指1本パン / 2本ピンチ（タッチパネル） | トラックパッドのスクロール=パン / ピンチ=ズーム。直接タッチはパームリジェクション対象 | |

## 5. 配布・ライフサイクル

| 項目 | Windows | Mac | 補足 |
|---|---|---|---|
| 配布形式 | MSI（推奨）/ 自己展開 EXE | dmg（Akapen.app） | Mac は署名・公証が前提（未署名ビルドは右クリック→開く） |
| EULA の提示 | インストーラの同意画面（MSI は WixUI） | dmg に「使用許諾契約.txt」を同梱（公証版でライセンス表示を検討） | 文面は共通（`apps/windows/installer/EULA-ja.txt`） |
| 最低対応 OS | Windows 10 x64 / arm64 | macOS 13（Ventura）以降 | grouped Form 等の SwiftUI API 要件 |
| マニュアル | exe 隣の `manual\akapen-manual-ja.html`（ヘルプから起動） | アプリバンドル内リソース（ヘルプから起動） | 同一 HTML の同期コピー |

## 6. 実装上の対応関係(開発者向け)

| 概念 | Windows 実装 | Mac 実装 |
|---|---|---|
| キー→アクション | `akapen_resolve_key_preset`（共通） | 同左 |
| ナビゲーター幾何 | `Ui/UiContracts.cs NavigatorMath` | `AkapenUIContract/NavigatorContract.swift NavigatorMath`（同式・両側テスト） |
| フェーダー幾何 | `Ui/UiContracts.cs BrushFader` | `AkapenUIContract BrushSizeKnob`（トラックインセット 70/32 共通） |
| ドック幾何 | `Ui/DockLayout.cs`（Width=168） | `AkapenUIContract AkapenUIMetrics.dockWidth=168` |
| サムネイル | `akapen_thumbnail_rgba`（共通） | 同左 |

## 7. 既知の未実装差(バグではなく作業中)

- Mac: 署名・公証、.30 実機検収、WACOM 筆圧マトリクス(M-mac5)。
- Windows arm64: 実機未検証。
