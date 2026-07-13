# Claude 引き継ぎ解析と次の作業案（2026-07-13）

## 調査範囲と前提

Claude の reasoning ログとして提示された内容は、現時点では
「I'll start by understanding the current state. Let me find the handoff doc and 仕様書.md.」
という開始メッセージのみだった。そのため、実際に何をしていたかは、リポジトリの未コミット差分、直近のコミット、`docs/開発日誌.md`、`apps/windows/README.md` と仕様書から復元した。

`CLAUDE.md` と `MEMORY.md` は、リポジトリ内および `/Users/yosshi` 配下を検索したが見つからなかった。別の作業ディレクトリや Claude 側の外部メモリに存在する場合、この文書の推定よりそちらを優先する。

## 直前まで Claude がしていたこと

直前の HEAD は `490ef44`（Windows M2-E: Settings pane）で、M2-E まではコミット済みだった。その後、Claude は Windows shell の M3-A「仕様 §3 のショートカットを Rust 共通 keymap に統合」に着手している。

現在の未コミット変更は次の通り。

| ファイル | 推定される作業 |
|---|---|
| `apps/windows/AkapenApp/MainWindow.xaml` | 個別 `KeyboardAccelerator` を撤去し、`RootGrid.KeyDown` を単一入口に変更 |
| `apps/windows/AkapenApp/MainWindow.xaml.cs` | WinUI のキーイベントを `akapen_resolve_key` に変換する処理、アクション dispatch、未実装スタブを追加 |
| `apps/windows/README.md` | M3-A の設計・スコープ・未実装項目を追記 |
| `crates/akapen-render/examples/cold_start_measure.rs` | プロセス単位の GPU/headless cold-start 計測 CLI を新規追加 |

実装の中心は以下の一本化である。

```text
WinUI RootGrid.KeyDown
  → ExtractCharacter / ExtractPhysical
  → akapen_resolve_key（Rust core の共通 keymap）
  → DispatchAction
  → 既存の SelectTool / Undo / Redo / StepFrame / NudgeSize
```

すでに既存 UI と接続されているアクションは、Pen、Eraser、Undo、Redo、ブラシサイズ増減、前後フレーム移動である。Line、Arrow、Rect、Ellipse、Text、ズーム、フィット、実寸、回転、色交換、スポイト、透明色は、キーを消費して StatusText に「後続 M3 で実装」と表示するスタブになっている。

## 現在確認できる状態

- Rust の `akapen_resolve_key` と C ABI / .NET generated binding はすでに存在する。
- `MainWindow.xaml` は `RootGrid KeyDown="OnRootKeyDown"` に変更済み。
- Ctrl+Z/Y、PageUp/PageDown、P/E、`[`/`]` を個別の XAML accelerator から共通 dispatch へ移行済み。
- Ctrl+S は保存処理として XAML accelerator に残している。Ctrl+, は Settings 起動専用の shell 特例として残している。
- IME composing は WinUI 3 で安価に取得できないため、`textEditing` と同じ保守的なガード値を渡している。
- `VK_OEM_PLUS` を JIS の `^` と解釈しない防御が入っている。US 配列で誤って右回転を発火させないためである。
- cold-start 例は `cargo check -p akapen-render --example cold_start_measure` を通過している。
- Windows 設定テストは `193/193 assertions passed`。

## 続きを進める推奨順序

### 1. まず M3-A 差分を検証して境界を確定する

最初に Windows 側の実装をさらに広げず、現在の差分を一つの作業単位として検証する。

```bash
cargo fmt --all -- --check
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo check -p akapen-render --example cold_start_measure
dotnet run --project apps/windows/AkapenApp.Tests -c Release
bash scripts/check-header-parity.sh
```

Windows 実機または GitHub Actions では、`dotnet build apps/windows/AkapenApp/AkapenApp.csproj -c Release` を実行し、C# 側の enum 数値直書きと XAML のイベント接続を確認する。macOS 上の WinUI build は XamlCompiler が Windows PE のため、完全な代替検証にはならない。

特に確認すべき点は、`NativeMethods.g.cs` の引数順と `akapen.h` の action/physical code が `MainWindow.xaml.cs` の数値と一致していること、そして `KeyDown` が TextBox の入力を横取りしないことである。

### 2. M3-A の開発日誌を追記し、差分をコミット可能にする

現在の変更には実装記録があるが、`docs/開発日誌.md` には M3-A の完了章がまだない。検証結果、未検証項目、意図的に残した特例（Ctrl+S / Ctrl+, / JIS `^`）を追記する。

その後、次の二つを分けて扱うのが安全である。

1. Windows M3-A keymap 統合
2. `cold_start_measure` 計測ハーネス

計測ハーネスを同じコミットに含めるかは、M3-A の受け入れ条件に「性能計測入口を追加する」を含めるかで決める。現状の仕様では性能実測自体が未完了なので、別コミットの方が履歴上は追いやすい。

### 3. M3-B では「未実装スタブ」のうち入力に直結するものから実装する

優先度は次の順を推奨する。

1. ズームイン・ズームアウト・フィット・100%表示
2. 回転左右
3. Line / Arrow / Rect / Ellipse の描画ツール
4. メイン/サブ色交換、透明色
5. スポイト
6. Text ツール

理由は、1 と 2 が既存の座標変換・描画体験を完成させ、3 がレビュー用途の基本操作を広げる一方、Text は IME・フォーカス・保存ベクター形式の設計を伴うためである。Text を先に実装すると、現在の keymap ガードだけでなく、編集状態・確定・undo 単位・JSON 表現まで同時に決める必要がある。

### 4. 実機検証を別トラックで進める

コードの完成度と品質チューニングは分けて扱う。新方針では、以下の実機検証は品質工程として重要だが、初期の Windows 開発をブロックしない。

- Windows Ink 有効の WACOM で弱→強→弱の筆圧が取得可能な範囲で画面・保存 PNG・JSON に反映される
- Windows Ink 無効の WACOM で Wintab 経路が動く
- ペン接触中の手のひらが描画を発生させない
- 4K 画像、多数ストロークで明らかな入力詰まりがないことを確認する。厳密な数値計測は最終チューニングへ回す
- Open → Draw → Save → Prev/Next → auto-save → Close を Windows 実 GUI で通す

このトラックを M3 UI 実装の完了と混ぜると、コードは進んでも製品の受け入れ条件が見えなくなる。

## 注意すべき技術的リスク

### WinUI のキーイベント

現在は `ExtractCharacter` と `ExtractPhysical` に文字コード・物理キーの対応を手書きしている。これは仕様上必要な小さな表に限定されているが、対応キーを増やす際は「文字由来で解決すべきキー」と「物理キー fallback で解決すべきキー」を混ぜないこと。特に JIS `^` と US `=` の扱いは過去に誤爆が発生した領域である。

### 数値 action code の直書き

`DispatchAction` と `ExtractPhysical` は C# 側で action / physical code をコメント付き数値として持っている。generated binding に enum を生成する仕組みがないため現状は理解できるが、将来 code を追加・変更する場合は `akapen.h`、Rust FFI、generated C#、shell の全てを同時に更新し、header parity と Rust FFI テストを通すこと。

### 未実装スタブの StatusText

スタブは M3-A の到達性確認には便利だが、製品版で「キーを押すと通知されるだけ」の状態を残してはいけない。README の M3-B/C/D 一覧と実装コードの action switch を更新し、実 UI が入った action から stub を削除する。

### cold-start 計測の意味

`cold_start_measure` は headless GPU 経路の計測であり、実 Windows の SwapChainPanel present、実ペン入力、最初の一筆目までを測っていない。結果を「アプリ起動から描画可能まで」の性能値として扱わず、まず基準値を取るための補助ハーネスとして使う。

## この引き継ぎ時点の結論

Claude は M2-E の完了後、Windows shell の M3-A に着手し、共通 keymap 統合を実装途中まで進めていた。次に行うべきことは、未実装 UI を急いで増やすことではなく、現在の M3-A 差分を Windows CI/実機相当の検証で固め、開発日誌を追記してコミット境界を明確にすることである。その後、ズーム・回転、図形ツール、色操作、Text の順に M3 を分割して進めるのが、既存の設計と仕様に最も整合する。
