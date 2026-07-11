# Akapen 実装 vs ロードマップ 比較地図（2026-07-12）

作成: Codex gpt-5.6-terra による分析（仕様書.md と実装 crates/・apps/mac の突き合わせ）。
判定は、リポジトリ内の実装・テスト・開発日誌に確認できる事実だけに基づく。実機操作、性能値、液タブ検証は、コードがあっても記録がなければ「未検証」とした。

## マイルストーン別（M0-M5）

| M | 判定 | 到達条件との比較 | 根拠 |
|---|---|---|---|
| M0 コア基盤 | 部分 | Rust コア、座標変換、平滑化、筆圧付きストローク、GPU 描画、JSON スキーマはある。一方、仕様が要求するキー写像、移植元 `lib/annotate-*` のテストベクタ移植、WinUI 3 実ホストでの1ストローク＋レイテンシ実測は確認できない。 | `crates/akapen-core/src/coord.rs`、`smoothing.rs`、`tessellate.rs`、`vector.rs`、`crates/akapen-render/src/canvas.rs`、`surface.rs`、`docs/開発日誌.md` |
| M1 mac MVP | 部分 | mac SwiftUI/AppKit シェル、画像読込、ペン、消しゴム、パン、ホイール／ピンチズーム、undo/redo、3点セット保存、前後移動、GPU表示経路まである。しかし回転は内部メソッドのみでUI／キーから到達不能、主要ショートカットは未実装、実機WACOM筆圧・パームリジェクション・4K性能は未検証。筆圧実機ゲート未通過のため、仕様上は未完成。 | `apps/mac/Sources/AkapenApp/AppState.swift`、`CanvasView.swift`、`ContentView.swift`、`AkapenKit/AkapenEngine.swift`、`crates/akapen-ffi/src/lib.rs` |
| M2 Windows 写像 | 未着手 | Windows向け `SwapChainPanel` サーフェス生成コードとDX12実機ビルド／ヘッドレスGPUテストの記録はあるが、WinUI 3/.NET 8 シェル、WM_POINTER、Wintab、実アプリとしての入力・保存・M1同等機能はない。 | `crates/akapen-render/src/surface.rs`、`renderer.rs`、`docs/開発日誌.md` |
| M3 CSP互換完成 | 未着手 | コアには平滑化、圧力カーブ、図形用 `Tool` 列挙値があるが、キー表全実装、サイズプリセット、不透明度UI、メイン／サブ／透明色、スポイト、IME・配列・フォーカス回帰試験は未確認。 | `crates/akapen-core/src/brush.rs`、`smoothing.rs`、`stroke.rs`、`apps/mac/Sources/AkapenApp/CanvasView.swift` |
| M4 広形式＋動画フレーム | 未着手 | bmp は既に読込対象、JSONには任意の `timecode` フィールドがある。しかし PSD、HEIC、TIFF、ffmpeg抽出、フィルムストリップ、未処理ジャンプ、tilt活用はない。圧力カーブは soft/normal/hard の選択UIのみ。 | `crates/akapen-io/src/decode.rs`、`crates/akapen-core/src/vector.rs`、`apps/mac/Sources/AkapenApp/ContentView.swift` |
| M5 VEDA再輸入 | 未着手 | C ABI とJSONの土台はあるが、napi-rs、wasm-bindgen、Node/WASM公開物、VEDA置換、実台帳互換検証は見当たらない。 | `crates/akapen-ffi/src/lib.rs`、`crates/akapen-core/src/vector.rs`、`Cargo.toml` |

補足: `docs/開発日誌.md` には mac Metal／Windows DX12 のGPUテスト成功、SwapChainPanel経路のコンパイル確認が記録されている。ただし、同日誌自身が SwapChainPanel の実WinUI画面表示・画素目視を「後追い」としているため、M0のWindows薄い縦穴は完了扱いにできない。

## MVP §2.2 機能チェックリスト

| 項目 | 判定 | 根拠・不足 |
|---|---|---|
| 開く | 実装済 | `NSOpenPanel` とD&Dで png/jpeg/webp/bmp を開く。`AkapenApp.swift`、`ContentView.swift`、`akapen-ffi/src/lib.rs` |
| ペン | 実装済 | NSEventから正規化座標・筆圧をFFIへ送り、コアがストローク確定。`CanvasView.swift`、`akapen-core/src/engine.rs` |
| 消しゴム | 実装済 | 消しゴムツール、CPUラスタ／GPUブレンド、保存前履歴への適用。`SidePanelView.swift`、`engine.rs`、`stroke.rs` |
| パン | 実装済 | Spaceドラッグ、通常スクロール。`CanvasView.swift` |
| ズーム | 実装済 | Cmd/Ctrl＋ホイール、`magnify`。`CanvasView.swift` |
| 回転 | 部分 | 座標変換と `rotate(by:)` はあるが、呼出元のツールバー・ジェスチャー・キー処理が無い。`coord.rs`、`CanvasView.swift` |
| undo・redo | 実装済 | コア履歴とUIボタン。`engine.rs`、`AppState.swift`、`ContentView.swift` |
| ショートカット | 部分 | Cmd+O、Cmd+S、Spaceのみ。§3主要行の大半は未実装。 |
| 筆圧 | 部分 | `.tabletPoint` の `NSEvent.pressure` をコアへ配線・圧力→幅・JSON保存・固定圧警告あり。WACOM実機での幅変化確認は未記録。 |
| パームリジェクション | 部分 | コアは `Touch` を描画しない。macシェルはタッチ明示分類・ペン優先ロックなし。実機検証なし。 |
| 3点セット保存 | 実装済 | 透過PNG／flat PNG／ベクターJSONを `_review/` に衝突回避で保存。`export.rs`、`akapen-io/src/output.rs`、`akapen-ffi/src/lib.rs`、`AppState.swift` |
| 連番次前 | 実装済 | 自然順・前後移動時ダーティなら自動保存。`AppState.swift`、`akapen-io/src/sequence.rs` |
| 4K性能 | 未検証 | オフスクリーン焼込みでO(1)化の設計はあるが、4Kでの<8ms/<16ms・fps・読込・切替の実測なし。`bake.rs`、`startup.rs` |

## §3 ショートカット表の実装カバレッジ

（「実装」= 当該キーを明示的に受けるコード or SwiftUI `keyboardShortcut` が確認できるもの）

未実装: P/E/U/A/R/O/T/I/X/C/`[`/`]`/Cmd+Z/Cmd+Y・Cmd+Shift+Z/Cmd+Space/Opt+Space/Cmd+0/Cmd+Opt+0/Shift+Space回転/`-`・`^`回転/PageUp・Down次前。
実装済: Space押下中パン/Cmd＋ホイール・ピンチ/Cmd+O/Cmd+S。

## §5 ペン入力の状態

- 筆圧配線あり: `CanvasNSView.pressure(from:)` が `.tabletPoint` 時のみ `NSEvent.pressure` を採用 → `akapen_pointer` → `PointerSample.pressure`。幅反映(`PressureCurve`/`Brush::width_for`/tessellate/GPU圧力幅テスト)あり。JSONは各点 `p` 必須。
- **実機検証は保留(液タブ未入手)。「筆圧が線幅に反映される」M1リリースゲートは未通過。**
- パームリジェクションは限定的: コアは `Touch` を無視するが、macシェルにタッチ明示送出・ペン接触中のタッチ抑止・ペン優先ロックが無い。§5.2適合は部分。
- §5.4 異常検知は実装済(`pressure_stuck_warning`)。ただし**警告文言がmac版なのに "Windows Ink" を案内=不整合**。`ContentView.swift`

## M1(mac MVP)を「完成」とみなすための残作業（優先順）

| 優先 | 残作業 | 対応ファイル | 受け入れ基準 |
|---|---|---|---|
| 1 | 主要ショートカットをモード非依存で実装(コアにキー写像・IME中/配列差/フォーカス試験) | `CanvasView.swift`、`AkapenApp.swift`、新設コアキー写像モジュール | §3のMVP対象 P/E、undo/redo、パン、ズーム、フィット、回転、次前が動作。テキスト入力時に横取りしない。 |
| 2 | 回転操作をユーザー到達可能に | `CanvasView.swift`、`ContentView.swift` | UI or Shift+Space／回転キーで回転でき、描画座標と保存座標が一致。 |
| 3 | macでのタッチ分類・ペン優先ロック(パームリジェクション実証) | `CanvasView.swift`、`AppState.swift`、`engine.rs` | ペン接触中に手のひらで線が出ない。タッチのパン/ピンチは意図どおり。対応実機で記録。 |
| 4 | 保存失敗時にフレーム移動しない(データ損失防止) | `AppState.swift`、`AkapenEngine.swift`、`akapen-ffi/src/lib.rs` | 自動保存失敗時は現フレーム維持・原因表示。成功時のみ移動。 |
| 5 | 4K性能計測を実GPU表示経路まで配線し数値取得 | `startup.rs`、`canvas.rs`、`CanvasView.swift` | 4K・多数ストロークで入力→表示・fps・開く→描画可能・次前を測り §6.1 合否を残す。現状計測はヘッドレスでsurface/present未含。 |
| 6 | M1併走条件のCI(csbindgen/napi-rs 三面ビルド) | `.github/workflows/*`、`akapen-ffi/Cargo.toml` | Swift/.NET/Node 生成・ビルドが継続実行。後二者は未確認。 |
| 7 | 保存・連番・GPU表示のmac実アプリ操作試験を記録 | `apps/mac/Sources/AkapenApp/*`、検証文書 | 開く→描く→保存→次前→自動保存→成果物確認をGUIで通し、3成果物・衝突連番・元画像非改変を確認。 |

### ハードウェア待ちの保留項目：筆圧実機ゲート（液タブ未入手のため保留）

対象: `CanvasView.swift`、`brush.rs`、`engine.rs`、`vector.rs`。受け入れ基準: 弱→強→弱の1ストロークでJSON `points[].p` に有意な分散と単調増減／画面・保存PNGで細→太→細が目視／soft・normal・hardで幅プロファイルが変わる／手のひらで誤描画しない／mac WACOM・液タブを必須セルに(Windows Ink on/off・板タブ・Surface Go は M2 マトリクス)。

## 所見（リスク・未整合）

- 最大の未整合: M1絶対条件の実機筆圧ゲートが未通過(§5.0「筆圧が線幅に反映されないビルドはリリース不可」)。→ 液タブ入手まで M1 リリース不可。
- §3は「コアの純関数にキー写像を置く」と定めるが、該当モジュールと参照実装ベクタ移植が未確認。現状キー処理は Space のみ。M0・M3双方に不足。
- 回転は `rotate(by:)` があるが呼出経路が無く部分実装。
- パームリジェクションはコアのTouch無視だけでは §5.2 を満たさない。mac入力層の種別判定・ペン優先ロックが要る。
- **保存時の自動移動にデータ損失リスク**: `AppState.step` は `save()` の戻り値を確認せず次フレームを開く。
- GPU経路は低遅延設計・オフスクリーン焼込み・Metal接続まで進むが、性能実測は未完(現状 `StartupTrace` はヘッドレスで surface/present/一筆目を含まない)。
- 半透明ストロークは自己交差/キャップ重なりで alpha 累積の既知乖離(opacity=1前提)。不透明度公開の M3 以降で修正必須。
- Windows は GPU 基盤の先行検証段階。M2「薄い皮の写像」には C ABI／.NETバインディング／CI三面ビルドの整備が不足。
