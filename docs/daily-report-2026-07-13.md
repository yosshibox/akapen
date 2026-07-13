# 日報 — 2026-07-13

## 今日の完了事項

- Windows MVP向けのWinUI 3シェルをWindows開発機でReleaseビルド。
- Rust FFI (`akapen-ffi`) のReleaseビルドに成功。
- C# native bindingを再生成。
- `AkapenApp.csproj` のReleaseビルドに成功（警告0、エラー0）。
- native DLL (`akapen.dll`) をWinUIシェル出力へ配置。
- 実機検証用パッケージを作成：
  - [AkapenApp-win-x64-mvp.zip](../dist/AkapenApp-win-x64-mvp.zip)
- 起動直後終了を調査・修正。自己完結型Windows App SDKランタイムを同梱した旧zipでは
  `Microsoft.UI.Xaml.dll / 0xc000027b`（内部値`0x802b000a`）で終了していた。
- Windows App Runtimeを利用するframework-dependent publishへ切り替え、修正版zipを
  `C:\Users\yosshi\Downloads\AkapenApp-win-x64-mvp.zip`へ再配置。

## 実機検証の手順

1. zipをWindows実機へコピーして展開する。
2. 展開先の `AkapenApp.exe` を起動する。
3. PNG/JPEG/WebP/BMPを開く。
4. ペンまたはマウスで描画する。
5. `Ctrl+S` で保存し、入力画像フォルダ内の `_review` を確認する。
6. 必要に応じて、Undo/Redo、Pen/Eraser、サイズ変更、Prev/Next、Spaceドラッグ、ズーム、回転を確認する。

## 確認済み

- Rust workspaceテスト、clippy、formatチェック：成功。
- Windows設定テスト：成功（Windows固有のchmodテスト2件は対象外）。
- Windows headless probe：画像・ストローク・JSONの保存に成功。
- WinUI正式シェルのコンパイル：成功。
- 修正版framework-dependent zipを対話型Session 1で起動：プロセス継続を確認。
- 対象機にMicrosoft公式のWindows App SDK 1.8.9 x64 Runtimeを導入。
  実インストール版は`8000.879.2017.0`で、1.8要求ダイアログは解消できる状態にした。
- Windows App SDK 2.2向け再ビルド、および1.8.7相当SDKとの一致も比較したが、いずれも
  `Microsoft.UI.Xaml.dll / 0xc000027b`で終了。WinUI Runtime不足とは別の、対象Windows環境での
  WinUI初期化障害として切り分けた。

## 未確認・注意事項

- SSHのSession 0からWinUIのSwapChainPanelを起動するとWindows側の制約で確認できないため、GUIの起動・描画・保存は実機の対話型デスクトップ（RDPまたはコンソール）で確認する。
- 修正版はframework-dependentのため、対象WindowsにWindows App Runtime 1.8（x64）が必要。対象開発機には導入済み。
- 現時点では正式WinUIシェルのGUI起動を完了扱いにしない。WinUI以外のraw Win32 probeは別途
  作成済みで、Rust FFI・DX12 HWND・描画・保存経路の代替検証に使える。
- 筆圧は取得できる場合は利用し、取得できない場合は固定値フォールバックとなる。圧力値が固定された場合は警告表示で診断できる。
- `dist/AkapenApp-win-x64-mvp.zip` はビルド成果物であり、リポジトリにはコミットしない。

## 次回候補

- 実機検証結果に基づく入力・表示不具合の修正。
- Windows MVPの受け入れ確認後、Mac/VEDA移植性を壊さない範囲で品質改善。
- Raptureとの比較チューニングはMVP後に実施。

## 方針更新・Win32 MVP移行

- WinUI 3は対象機でMicrosoft公式Runtimeを導入しても起動直後終了するため、Windows MVPの実行経路から外した。WinUI 3/Windows App SDKは旧検証経路として残す。
- `apps/windows-probe`をraw Win32 MVPシェルへ昇格。`WM_POINTER`筆圧、簡易パームリジェクション、画像Open、Pen/Eraser、Undo/Redo、パン/ズーム/回転、PageUp/PageDown、非破壊3ファイル保存を実装。
- 画像の保存先は既定で入力画像と同じ場所の`_review`。blank canvasのみ一時フォルダを使用する。
- `apps/windows-probe/run.bat`をself-contained `win-x64` publishへ変更。Windows App Runtimeおよび別途.NETのインストール不要を配布要件にした。

## この更新の検証

- `dotnet build apps/windows-probe/AkapenProbe/AkapenProbe.csproj -c Release --no-restore` 成功（警告0、エラー0）。
- `dotnet publish -r win-x64 --self-contained true` 成功。self-contained `AkapenProbe.exe`を生成確認。
- `git diff --check` 成功。
- `cargo test --workspace` 成功（Rustテスト計164件、既知のignore 2件）。
- 開発用`--interactive-smoke`を追加。実際のHWND mouse down/move/upとWM_CLOSEを通して、入力から自動保存までをSession 1で検証可能にした。
- Windows Session 1で最新版`--interactive-smoke C:\\Users\\yosshi\\Downloads\\akapen-interactive-smoke`を実行し成功。`probe.review.png`（15,025 bytes）、`probe.strokes.png`（13,215 bytes）、`probe.strokes.json`（320 bytes）を生成し、JSONの`schema=veda-annot-1`、ストローク点、`points[].p`を確認。
- 配布実行ファイル名を内部名の`AkapenProbe.exe`から正式名`Akapen.exe`へ変更。
- `Akapen.dll`（管理アセンブリ）とRustの`akapen.dll`がWindows上で衝突する問題を修正。ネイティブDLLを`akapen_native.dll`へ分離し、`BadImageFormatException`を解消。
- 修正版zipをDownloadsへ再配置（36,149,608 bytes）。内容は`Akapen.exe`、`Akapen.dll`、`akapen_native.dll`。
- 修正版`Akapen.exe`でheadless exportとSession 1のinteractive-smoke（3点セット保存）を成功確認。

## 実機で残る確認

- Windows実機Session 1でself-contained publishを起動し、実際の液タブWM_POINTER圧力、描画、保存、画像切替を確認する。
- 実機で問題が出た場合は、WinUIへ戻さずraw Win32入力/描画経路を修正する。

## 追加確認

- 更新版をWindows開発機へ再同期し、`AkapenWin32Mvp-win-x64.zip`をDownloadsへ再配置（36,149,365 bytes）。
- 対話ユーザーSession 1で更新版`AkapenProbe.exe --interactive`を起動し、4秒後もプロセスが継続していることを確認（SessionId=1）。起動確認後はテストプロセスを終了した。
- 前回のpublish失敗は、確認用プロセスがpublish DLLをロックしていたことが原因。プロセス終了後の再publishは成功。
- ペン入力後に発生する合成マウスイベントの二重描画を抑制し、筆圧固定/取得不能時の警告ログを追加。
- 保存先overrideを指定した場合、画像を切り替えても同じ保存先を維持するよう修正。
- 最新版でheadless export（3ファイル生成）とSession 1対話起動（プロセス継続）を再確認。
- 操作拡張反映後の最新版でもSession 1起動継続を再確認（SessionId=1、起動後4秒）。
- `PageUp`/`PageDown`の画像切替をWindows標準の論理ファイル名順に変更し、`image2`→`image10`の順序問題を回避。
- 単指タッチをパン入力として実装し、ペン接触中はタッチを無視する簡易パームリジェクションに統一。
- `[`/`]`のブラシサイズ変更、`Ctrl+0`フィット、`Ctrl+Alt+0`の100%表示を追加。

## GPT-5.6-Sol 全体分析・ロードマップ

- Chapter 5.6限定ではなく、仕様書全体・開発日誌・引継ぎ資料・README・Windows実装・Rust core/FFI/IOをGPT-5.6-Sol（reasoning medium）にread-only調査させた。
- 分析成果を [全体ロードマップ](windows-mvp-roadmap-gpt-5.6-sol-medium-2026-07-13.md) に記録した。
- 先に作成したChapter 5.6へ偏った解釈のロードマップは破棄し、Windows Probeを正式なAkapen Windows MVPへ転換する全体方針を正本とした。
- 最初の実装ゲートは、通常起動とsmoke/headlessの分離、保存失敗状態機械、3点セット原子保存、Windows publish CI、実機Session 1受入の順とする。

## GPT-5.6-Luna Medium 実装開始 — Windows UI第一縦切り

- 3設計書を正本として、Luna MediumにWindows MVPの第一縦切りを実装させた。
- 引数なしの`Akapen.exe`はscripted smokeを実行せず、通常のraw Win32製品シェルとして起動するよう変更。`--presentation-smoke`、`--headless-export`、`--interactive-smoke`は明示モードに分離した。
- `apps/windows-probe/AkapenProbe/Ui/`に、Mac/VEDAへ写像可能な`UiCommandId`・portable command ID・`UiState`・dockレイアウト/hit-test・設定ストアを追加。
- 右側既定・左側切替可能な64px縦ドックを追加し、Open/Save/Undo/Redo/Pen/Eraser/Pan/Zoom/Rotate/Brush size/Fit/Actual/Previous/Next/Settingsを簡易グラフィカルアイコンで表示するようにした。
- `ui.dockSide`を`%LocalAppData%\\Akapen\\settings.json`へ原子的に保存し、欠落・破損・未知値は右側へフォールバックする。
- `apps/windows-probe/AkapenProbe.Tests/`にUI契約・レイアウト・設定往復テストを追加。
- 検証: .NET build成功（NU1900のネットワーク警告のみ）、UI契約テスト成功、`cargo test --workspace`成功、`git diff --check`成功。
- 残課題: ネイティブtooltip/UI Automation、設定画面、disabled/selected/busy状態、Open/Save/Nextの状態機械統合、実機受入（Windows版完成後にまとめて実施）。

## GPT-5.6-Luna Medium 実装 — 製品UI第二縦切り

- 通常製品モードのウィンドウクラス、タイトル、ログを`Akapen`へ整理し、診断用の明示smoke経路だけ`Probe`表記を維持。
- Settingsアイコンをraw Win32設定ダイアログへ変更。Right/Leftを選択し、OKで`ui.dockSide`保存・即時反映、Cancel/閉じる操作では変更しない。
- 全`UiCommandId`にportable ID、操作名、ショートカットを持つtooltip契約を追加。
- engineがnullの空canvas起動時もdock・キーボード操作でクラッシュしないよう防護。
- 独立検証: Windows .NET build成功（NU1900のネットワーク警告のみ）、UI契約テスト成功、`cargo test --workspace`成功、`git diff --check`成功。
- 次の実装対象: Save/Open/Nextの製品状態機械とdisabled/selected/busy表示、tooltipの実機表示確認、Windows publish更新。実機受入はWindows版完成後にまとめて実施する。

## GPT-5.6-Luna Medium 実装 — 保存安全性・UI状態第三縦切り

- `TrySave`を成功/失敗を返す処理へ変更し、保存中状態を保持。
- 保存失敗時にengine、現在画像、履歴、dirty状態を保持するようにした。
- Open/Previous/Next/Closeは、dirty状態なら保存成功後のみ遷移・終了するようにした。
- `UiCommandStateResolver`、`UiBadge`、状態テストを追加し、保存中・文書なし・候補なし・履歴なしのdisabled、Pen等のselected、Saveのdirty/busy、warningを純粋関数で判定するようにした。
- dock描画とクリック処理へdisabled/selected/badge状態を接続。
- 検証: .NET build成功（NU1900のネットワーク警告のみ）、UI契約テスト成功、`cargo test --workspace`成功、`git diff --check`成功。
- 残課題: 実機でのSave/Open/Close操作、undo/redo実履歴のC ABI状態取得、publish配布物への反映。実機受入はWindows版完成後にまとめて行う。

## GPT-5.6-Luna Medium 実装 — undo/redo実履歴の共通契約接続

- Rust FFIに`akapen_can_undo` / `akapen_can_redo`を追加。nullは0、利用可能時は1を返す。
- canonical C headerとMac mirror headerを更新し、header parityを維持。
- Windows C# P/Invokeと`CurrentUiState()`を実engineの履歴へ接続。dockのUndo/Redo disabled表示が実履歴に追従する。
- pointer確定、Open、Undo/Redo後のUI invalidateを追加。
- FFI null安全テストとstroke→undo→redoの履歴遷移テストを追加。
- 検証: Windows .NET build成功（NU1900のネットワーク警告のみ）、UI契約テスト成功、`cargo test --workspace`成功、`scripts/check-header-parity.sh`成功、`git diff --check`成功。
- 残課題: 実機UI受入、Mac Swift側のcanUndo/canRedo表示接続、publish配布物更新。実機受入はWindows版完成後に実施する。

## GPT-5.6-Luna Medium 実装 — Mac SwiftUI移植第一縦切り

- `AkapenEngine`へ`canUndo` / `canRedo`のSwift APIを追加し、既存C ABIへ接続。
- `AppState`がopen、stroke確定、undo/redo、save後に履歴状態を更新し、Undo/Redo UIのdisabled判定へ反映。
- Macの上部常設toolbar依存を廃止し、SF Symbols中心の縦型Tool Dockへ移行。
- 右側既定・左側切替を`@AppStorage("ui.dockSide")`で実装し、設定変更を即時反映。
- Open/Save/Undo/Redo/Pen/Eraser/Pan/Zoom/Rotate/Fit/Actual/Previous/Next/Settingsをアイコン中心で配置し、`accessibilityLabel`と`help`を付与。
- 検証: Swift build成功（sandbox-exec制限を権限付き再実行で回避）、`cargo test --workspace`成功、header parity成功、`git diff --check`成功。
- 残課題: Brush/Color/Pressure/Opacityの詳細flyout、Mac実機GUI確認、VEDA Electron側のアイコンドック接続。実機確認はWindows版完成後にまとめて行う。

## GPT-5.6-Sol 詳細設計

- 全体ロードマップを正本として、GPT-5.6-Sol（reasoning medium）にWindows MVPの詳細設計を依頼した。
- raw Win32製品シェル、smoke/headless分離、Rust core/FFI/JSON、Mac SwiftUI、VEDA Node/napi-rs、将来WASMの可搬境界まで設計対象に含めた。
- 詳細設計は [Windows MVP詳細設計](windows-mvp-detailed-design-gpt-5.6-sol-medium-2026-07-13.md) に記録した。

## GPT-5.6-Sol Mac/VEDA移植計画

- Windows MVPを優先しつつ、Mac SwiftUI/AppKitとVEDA Electron/Nodeへの移植・統合計画をGPT-5.6-Sol（reasoning medium）に作成させた。
- Rust core、C ABI、VectorDoc、3点セット、golden vectors、consumer contract testsを共通の移植境界として整理した。
- 計画は [Mac/VEDA移植・統合計画](mac-veda-porting-integration-plan-gpt-5.6-sol-medium-2026-07-13.md) に記録した。

## GPT-5.6-Sol UI詳細設計

- 全ツールを既定で右側の縦ドックに配置し、設定で左側へ変更可能とするUI要件をGPT-5.6-Sol（reasoning medium）に設計させた。
- 上部常設ツールバーを設けず、グラフィカルなアイコン、ツールチップ、キーボード操作、UI Automation/アクセシビリティを併用する方針にした。
- Windows raw Win32、Mac SwiftUI/AppKit、VEDA Electronの共通UI command/state契約を [UI詳細設計](akapen-ui-detailed-design-gpt-5.6-sol-medium-2026-07-13.md) に記録した。

## GPT-5.6-Luna Medium 実装 — VEDA Node/napi-rs adapter第一縦切り

- `bindings/node`をCI probeだけの状態から、共有C ABI上の`AkapenSession` napi-rsクラスへ拡張した。
- blank canvas／画像パス生成、width/height、canUndo/canRedo、pointer、undo/redo、tool/color/size設定、`exportToDir`を提供する。
- opaque handleはRust側で所有し、`Drop`で`akapen_free`を一度だけ呼ぶ。NUL、空文字、無効パス、非有限値、範囲外値はpanicせず明示的なエラーにする。
- export内容はJavaScriptで再実装せず既存Rust FFIへ委譲し、VEDAの3点セット／`veda-annot-1` VectorDoc契約を共有する。
- READMEとpackage metadataをVEDA adapter previewへ更新し、既存probe APIは後方互換で維持した。
- 検証: `cargo fmt --all -- --check`、`cargo test --workspace`（Node adapter 2件を含む）、`cargo clippy --workspace --all-targets -- -D warnings`、header parity、`git diff --check`成功。
- 残課題: npm prebuild/publish、VEDA本体とのE2E統合、非同期I/O、Windows publish配布物の再生成と実機受入。

## GPT-5.6-Luna Medium 実装 — Mac設定フライアウト

- MacのアイコンドックからBrush/Color/Pressure/Opacityを開くPopover flyoutを追加した。
- Brushはサイズプリセットと1〜80px Slider、Colorは10色パレット・ColorPicker・Hex入力・RGBA表示、PressureはSoft/Normal/Hardを既存Rust APIへ接続した。
- Opacityは共有UI契約だけ先に持たせ、core API未実装のため100%固定・disabled・TODO表示にした。将来API追加時の接続点を明確化した。
- portable command IDとして`style.size`、`style.color`、`style.pressure`、`style.opacity`を追加し、SF Symbols、tooltip、accessibility label/valueを付与した。
- 検証: SwiftPM build成功（モジュールキャッシュを一時領域へ指定）、Swift parse成功、`git diff --check`成功。
- 残課題: Mac実機GUI確認、Windows側の同等Brush/Color/Pressure flyout、Opacity API追加後の有効化、Windows publish配布物再生成と実機受入。

## Windows MVP配布物更新

- 最新ソースを`veda_win_ed25519`でWindows開発機`yosshi@192.168.11.34`へ同期した。
- 同期時にMac由来のAppleDouble`._*.cs`が混入してC# publishを妨げたため、Windows側で該当メタファイルを除去し、Release生成物をクリーンにして再publishした。
- self-contained `Akapen.exe`と`akapen_native.dll`を再生成し、`C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip`へ配置した（36,164,832 bytes）。Windows App Runtime/.NET別途導入不要の配布形式を維持する。
- Windows実機publish成功。Downloads上のzip存在を確認済み。実機の通常起動・液タブ筆圧・UI操作はユーザー受入で確認する。

## Windows実機フィードバック対応 — メニューバーとスタイルUIの可視化

- ユーザー実機で「描画はできるがサイズ・色変更UIが見えず、上部メニューもない」と確認されたため、原因を配布物がflyout実装前の版であることと、製品モードのメニューバー未実装に切り分けた。
- 製品モードに日本語Win32メニューバー（ファイル／編集／表示／ツール／設定）を追加し、既存のportable command経路へ接続した。
- Brush/Color/Pressureのドックアイコンに日本語tooltipを追加し、BrushサイズとPressure状態をアイコン内にも表示。既存flyoutの左右展開、外側クリック、Escape、再押下は維持した。
- `dotnet build`、UI契約テスト、`git diff --check`成功（NuGet脆弱性feed取得のNU1900警告のみ）。
- AppleDouble混入を避けてWindows実機へ再同期し、self-contained版を再publish。最新版zipを`C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip`へ配置（36,170,684 bytes）。
- ユーザーには最新版zipを展開して再確認してもらう。実機受入はgoalをblockせず、次の実装と並行して扱う。

## Windows実機フィードバック対応 — ドックを描画領域の外側へ分離

- ユーザーから「ツールパレットが一瞬見えた後に消える」「画像表示領域の完全な外側に置くべき」と報告された。
- 原因は、main HWND全体へRust render surfaceをattachした後、GPUフレームがドックを上書きしていたことだった。
- 製品モードにキャンバス専用child HWNDを追加し、右ドック時はキャンバスを左側、左ドック時はキャンバスを右側へ配置。ドック幅64pxを描画領域から予約した。
- Rustのrender attach/resize/frameはcanvas child HWNDだけに対して実行し、ドックの毎フレーム上塗り回避を主経路から削除した。
- dock side変更時はcanvas childの位置・幅・render resize・flyout closeを同一経路で更新する。入力はcanvas child座標から既存coreへ送る。
- `CanvasBounds`の左右非重複契約テストを追加し、dotnet build、UI契約テスト、git diff checkを成功確認。
- Windows実機へ再同期・self-contained publishし、最新版を`C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip`へ配置（36,172,337 bytes）。

## GPT-5.6-Luna Medium 実装 — shared golden export契約

- `testdata/golden-export-v1.json`を追加し、64x48キャンバス上の筆圧変化を含む2本のpen strokeをRust core／FFI／Nodeで共通検証するfixtureにした。
- Rust core、C ABI、Node `AkapenSession`の各テストで、`veda-annot-1`、natural size、stroke/kind、全`points[].p`、3点セットの存在を確認する。
- Mac harness READMEコメントにも同一コマンド・成果物比較の手順を記録し、画像合成やVectorDocを各UIで再実装しない方針を固定した。
- 検証: `cargo fmt --all -- --check`、`cargo test --workspace`、`cargo clippy --workspace --all-targets -- -D warnings`、header parity、`git diff --check`成功。

## VEDAリポジトリ監査・統合第一縦切り

- 指定された`/Users/yosshi/Documents/andraft-version-control`を確認し、`CLAUDE.md`／`AGENTS.md`／`docs/VEDA-詳細設計.md`、既存の`annotate`／`submission-annotate` IPCと追記型イベント実装を読了した。
- VEDAの`lib/annotations.js`に任意vector用の`veda-annot-1`境界検証を追加。schema、natural dimensions、stroke fields、座標、必須per-point pressureを検査する。
- `VERSION_ANNOTATED`／`SUBMISSION_ANNOTATED`のイベント名・保存パス・旧vector無しイベントは変更せず、不正vectorはPNG・vector・イベントを一切書かない。
- VEDA側テストを追加し、`npm test`（643件成功）、`git diff --check`成功。
- 残課題: VEDAでのAkapen native addon動的ロードE2E、Windows/Mac実機golden比較、Windows実機UI受入。

## GPT-5.6-Luna Medium 実装 — VEDA Akapen adapter seam

- VEDAにElectron非依存の`lib/akapen-adapter.js`を追加し、`AKAPEN_NODE_ADDON_PATH`または明示パス指定時だけAkapen Node addonをロードするようにした。
- addon未導入・ロード失敗時は`{available, path, error}`を返し、VEDA既存のJS注釈経路を壊さない。任意パスの推測ロードは行わない。
- `createSession`／`runPortableCommands`でportable commandを検証し、画像・VectorDoc・3点セットの生成はnative sessionの`exportToDir`へ委譲する。
- fake addonを使うテストで、未導入状態、コマンド委譲、座標・kind等の不正入力拒否を検証した。
- VEDA検証: `npm test` 646件成功、`git diff --check`成功。
- 残課題: 実際にビルドした`.node`をVEDAからrequireするCI/E2E、VEDA保存フローからのnative経路選択、Windows/Mac実機golden比較。

## GPT-5.6-Luna Medium 実装 — Node native公開名・動的ロード修正

- 実native addonを確認した結果、設計契約`AkapenSession`に対して生成物が`Session`を公開していたため、Rust napi structを`AkapenSession`へ修正した。
- `bindings/node/scripts/native-smoke.js`と`npm run smoke`を追加し、platform artifactを直接requireして筆圧変化と`review/strokes/strokes.json`の3ファイルを確認する。
- Mac arm64実機相当のローカルnative artifactで、VEDA adapter経由の出力を確認: `veda-annot-1`、64x48、pressure `[0.2,0.5,0.9]`。
- Akapen全体検証: `cargo test --workspace`、clippy、format、header parity成功。VEDA検証: `npm test` 646件成功、adapter単体3件成功、native smoke成功。
- 残課題: Windows x64でのNode artifact build/load、VEDA保存フローからのnative経路選択、Mac/Windows実機golden比較。

## GPT-5.6-Luna Medium 実装 — VEDA renderer VectorDoc正規化

- VEDA現行rendererが旧形式`w/h`とshape/text/stamp固有座標を出していたため、厳格なpressure-bearing VectorDoc validatorと衝突する問題を修正した。
- `lib/annotations.js`の`normalizeVector`とrenderer内no-build mirrorで、free/line/arrow/rect/ellipse/text/stampを`natural_w/natural_h`＋`points[{x,y,p}]`へ変換する。
- malformed transient strokeは配列順を維持して決定論的にスキップし、PNG・イベントパスは変更しない。未知フィールドは保持する。
- rendererの`buildVector()`を正規化経由へ変更し、実際の注釈保存が`SUBMISSION_ANNOTATED`へ到達できる状態にした。
- VEDA検証: focused vector tests成功、`npm test` 650件成功、`git diff --check`成功。

## CIゲート強化 — Windows x64 Node native smoke

- `.github/workflows/ci.yml`のNode binding jobをUbuntu/Windows matrixへ拡張した。
- 各環境でplatform-specific `.node`の存在を確認後、`npm run smoke`を実行し、`AkapenSession`、筆圧変化、3点セットを動的に検証する。
- staleな「probeだけ／CI scaffoldだけ」の説明を更新した。
- YAML parse、Node syntax/package JSON parse、`git diff --check`成功。Windows runnerでの実際のCI実行はpush/PR時の外部検証に残る。

## Tool Dock GUI redesign

- Windows raw Win32を基準に、単列64pxの汎用ボタン列を、7グループ・2列・88pxのコンパクトなアイコンレールへ再設計した。全18 commandが600px高でも欠落せず、選択・hover・disabled・focusの状態優先順位をUI契約として固定した。
- Brush / Color / Pressureのflyoutはキャンバス上へ重ねず、dockの内側に280pxの予約済みinspector領域を確保する。右dock・左dockの双方でcanvas child HWNDとinspector/dock/statusが非重複になるgeometry testを追加した。
- Win32は暖色系neutral surface、朱色accent、グループseparator、状態バー、値preview、shape badgeを共通visual tokenとして適用。portable command ID、tooltip、native canvas handle、入力・保存経路は変更していない。
- Mac SwiftUIも同じ2列railと固定inspectorへ写像し、popoverによるcanvas overlapを廃止。SF Symbols、help、accessibility label/value、右既定・左設定を維持した。
- 検証: Win32 UI契約テスト成功、`dotnet build`成功、Swift parse成功。SwiftPM buildは実行環境の`ModuleCache`書込制限と`sandbox-exec`拒否で完走不可。

## Windows GUI redesign package

- GPT-5.6-Sol (light) のGUI再設計を反映したself-contained win-x64版を再publishした。
- 配布物: `C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip`
- サイズ: 36,176,112 bytes。`Akapen.exe`、self-contained runtime、`akapen_native.dll`を含む。
- publishディレクトリにAppleDouble (`._*`)が混入していないことを確認済み。
- ユーザー実機でのダブルクリック起動、描画、ツールレール、inspector表示は引き続き受入確認待ち。

## GUI根本再設計 — 常設パレット／縦フェーダー／共有SVG

### 調査と問題確認

- Windows製品経路は`apps/windows-probe`のraw Win32、Macは`ContentView`＋`SidePanelView`のSwiftUI/AppKitシェルであることを再確認した。core/FFI、portable command ID、canvas child HWND、wgpu/Metal描画、3点セット保存経路は変更対象外とした。
- 前回実装は88px・2列で全18 commandを同じ重みにし、Brush/Colorを別inspectorへ隠し、Windowsはcommand別のGDI線画と`TextOut`、Macは2列rail＋flyoutを使っていた。サイズもWindows flyoutが最大80pxで、今回の1–50px要件と不一致だった。
- 既存作業ツリーには本件以前の多数の未コミット変更があるため、core/FFI等の既存差分を保持し、今回の編集をGUIシェル、共有icon資産、UI契約テスト、関連文書に限定した。

### 設計判断

- 232 DIPの役割指向インスペクターへ変更。上からファイル/履歴アクション、主ツール5個、固定10色、常設縦フェーダー、表示/シーケンス/設定を分離した。全commandを同一マトリクスには置かない。
- dockは右既定・設定で左へ即時移動。canvas child、dock、statusを非重複領域として計算し、overlay/flyoutを主スタイル操作から廃止した。
- 固定パレットは黒、灰、濃赤、赤、橙、黄、緑、水色、青、白の10色。5×2常設、1クリック反映、3px accent輪郭、独立した現在色previewで選択を示す。Windows/Macの順序と値を揃えた。
- ペン先は1–50pxの縦フェーダー。上=50/太い、下=1/細い、クリックjump、drag連続変更、矢印±1、Page Up/Down±5、Home/End最小最大、focus輪郭、数値、実径previewを定義した。値はengine未生成でもUI stateに保持する。
- `assets/icons/akapen-ui-icons.svg`をcanonical資産として新設。全18 portable commandに24×24、stroke 1.75、round cap/join、`currentColor`、文字なしの独自pathを割り当てた。Windowsは埋込みSVGをcommand IDで読み、絶対M/L/H/V/C/A/Zのpath parserを介して現在DPIでベクター描画する。解析結果はcatalogでcacheする。
- Win32製品コードから旧command別GDI図形、flyout、`TextOut`宣言/呼出しを削除した。`NativeUiRenderer`にGDI+ antialiasとClearType GridFit、`Yu Gothic UI`優先fallbackを隔離し、D3D/canvasとは独立にした。将来Direct2D/DirectWriteへ置換してもportable command、hit-test、canvas、core/FFI、保存経路を変えない境界とした。
- `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`、`GetDpiForWindow`、`WM_DPICHANGED`を接続し、96 DPIの契約寸法からdock、status、button、swatch、fader、fontを再計算する。
- Macは同じ情報階層、10色、1–50px縦Slider、現在値/preview、右/左dockへ変更。CanvasView、AppKit入力、Metal描画、保存処理は変更していない。Macは今回SF Symbols写像を維持し、canonical SVGの直接生成は後続課題とした。

### 主な変更ファイル

- `assets/icons/akapen-ui-icons.svg`、`assets/icons/README.md`: canonical SVG spriteと仕様。
- `apps/windows-probe/AkapenProbe/Ui/{DockLayout,UiContracts,SvgIconCatalog,SvgPathParser,NativeUiRenderer}.cs`: layout/state、固定palette、fader操作、SVG読込/解析、高品質native描画。
- `apps/windows-probe/AkapenProbe/Ui/UiContracts.Tests.cs`: 10色、左右非重複、1–50px、click/keyboard mapping、200% DPI、全command SVGの契約テスト。
- `apps/windows-probe/AkapenProbe/Program.cs`、`AkapenProbe.csproj`: UI描画/入力/DPI接続、SVG埋込み。portable command IDと既存engine dispatchは維持。
- `apps/mac/Sources/AkapenApp/SidePanelView.swift`、`PaletteColors.swift`: 同一情報階層の固定インスペクターとpalette統一。
- `apps/windows-probe/README.md`、`docs/akapen-ui-detailed-design-gpt-5.6-sol-medium-2026-07-13.md`: 改訂GUI契約と文字描画移行境界。

### 検証結果

- Win32 UI契約テスト: `dotnet run --project apps/windows-probe/AkapenProbe.Tests/AkapenProbe.Tests.csproj --no-restore` 成功。NuGet vulnerability feedへ接続できない`NU1900`警告のみ。
- raw Win32製品build: `dotnet build apps/windows-probe/AkapenProbe/AkapenProbe.csproj --no-restore -p:RuntimeIdentifier=win-x64` 成功、0 error。`NU1900`警告のみ。
- Mac GUI build: cache/scratchを`/tmp`へ移し、SwiftPM自身のsandbox制約を外して`swift build --package-path apps/mac --scratch-path /tmp/akapen-swift-build --target AkapenApp`成功。
- SVG: `xmllint --noout assets/icons/akapen-ui-icons.svg`成功。全18 pathのparser test成功。
- GDI TextOut除去: `apps/windows-probe/AkapenProbe`のC#に`TextOut(`がないことを`rg`で確認。
- `git diff --check`成功。
- 参考の旧WinUI `apps/windows/AkapenApp`はmacOSではWindows用`XamlCompiler.exe`を実行できずbuild不可。`EnableWindowsTargeting=true`でも同じホスト制約で、今回のraw Win32ソースの失敗ではない。

### 残課題

- Windows Session 1実機で100/125/150/200% DPI、right/left、palette/faderのmouse・pen・keyboard、Yu Gothic UI日本語、hover/selected/disabled/focusを目視確認し、golden screenshotを取得する。
- raw Win32のcustom-drawn hit regionにUI Automation providerを追加し、Accessibility Insightsでname/role/value/selected/disabledとTab/F6順を検証する。
- `NativeUiRenderer`の境界を維持したままDirect2D/DirectWrite backendへ置換し、GDI+ ClearType版との文字品質・DPI・performance比較を行う。
- Macでcanonical SVGからPDF/Swift Pathを生成してSF Symbols写像を置換し、VoiceOverと縦SliderのPage Up/Down/Home/End相当を実機確認する。
- VEDA側は同じportable command ID、10色、縦fader契約へ写像する実装とaxe/keyboard検証が未着手。

### 実機検証用Windows配布物更新

- Sol Medium版のソース、共有SVG、AppleDouble除去後に実機PCでwin-x64 self-contained publishを再実行。
- `C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip` を更新（36,193,715 bytes）。`Akapen.exe` と `akapen_native.dll` の存在を確認済み。
- 実機でのマウス・ペン・DPI・日本語描画の最終目視確認はユーザー受入待ち。

## GUI再差戻し対応 — Mac SwiftUI正本／Windows mirror

### 差戻し理由

- 前回の232 DIP固定インスペクターは、Windowsを直接デザイン起点にしたためMacの自然な
  SwiftUI構成になっておらず、画面占有が大きかった。
- Open/Save/Undo/Redo/Zoomなどをドックへ常設し、レビュー時に必要なPen/色/サイズとの
  視覚的な重みが分離されていなかった。
- 色が5×2の角丸四角、サイズが高さ112 DIP以上の大きな縦フェーダーで、今回要求の
  「丸10色横1列」「小型volume/knob型」と逆だった。
- 引数なし起動でもblank engineと描画surfaceを生成し、フォルダ選択／dropのempty stateを
  先に見せる契約を満たしていなかった。
- Win32 main HWNDは`WM_ERASEBKGND`を抑制していたが、`WM_PAINT`が`GetDC`直接描画で、
  memory DCによるwhole-client paint契約が不足していた。

### 設計判断

- Mac SwiftUIをUI正本に変更。`empty`／`loading`／`loaded`を純粋契約として追加し、
  emptyでは「画像を開くフォルダを選択」と「画像をここへドロップ」だけを表示する。
  canvas/Metal/tool dockはloadedで初めて生成する。
- フォルダ選択は直下のPNG/JPEG/WebP/BMPを自然順に並べた先頭を既存`open(url:)`へ渡す。
  ファイルOpen、drop、連番、保存の既存経路は維持した。
- loadedはcanvas＋96pt下部dock＋24pt status。dockのcommand buttonはPen/Eraserだけ。
  Open/Save/Undo/Redo/Zoom/表示/連番/設定はネイティブメニュー、ショートカット、ジェスチャーへ残す。
- MS Paintの色を直接選択する常設paletteを調査し、既存の10色値を維持しつつAkapen独自の
  円形swatchへ変更。10個を横1列、24→最小14pt/DIPの範囲で縮小し、選択を3px ringで示す。
- サイズは48×72pt/DIP。上部の下向き三角cap、5段の小目盛り、現在pxだけを持ち、railと
  実径previewを廃止した。1–50px、click/drag、矢印±1、Page Up/Down±5、Home/Endを維持した。
- Windows raw Win32を同じ状態・96/24 DIP・配置へmirror。empty時はengine/canvas HWNDを
  作らず、folder pickerと`WM_DROPFILES`を既存画像Openへ接続する。
- Win32 paintを`BeginPaint`→compatible memory DC/bitmapへ全体描画→単一`BitBlt`へ変更。
  mainに`WS_CLIPCHILDREN`、canvas childに既存`WS_CLIPSIBLINGS|WS_CLIPCHILDREN`を適用し、
  `WM_ERASEBKGND`抑制を維持した。render resizeはphysical size変化時だけ発行する。
- 画像差し替え後もmessage loopが古いローカルengineを参照し得たため、既存共有
  `s_engine`へrender/free参照を一本化した。core/FFI APIには変更を加えていない。

### TDDと検証

- RED: Macは新`AkapenUIContract` target未実装で`swift test`が失敗。Windowsは小型knobと
  memory-back-buffer契約が未定義でC# UI契約testがcompile failureすることを確認した。
- GREEN: Mac UI契約5件（phase、軽量dock、丸10色1列、小型size knob、暗黙animationなし）が成功。
- Mac `swift test --disable-sandbox`で`AkapenApp`を含むSwiftPM build成功、5/5 tests成功。
- Windows raw Win32 UI契約test成功。`dotnet build`成功（0 error、NuGet feedへ接続できない
  既知の`NU1900` warningのみ）。

### 可搬境界と未完の実機確認

- 変更は`apps/mac`、`apps/windows-probe`、UI契約、UI設計／READMEに限定した。
  `crates/`、C ABI/header、portable command ID文字列、VectorDoc、描画／3点保存、VEDA adapterは未変更。
- Windows配布物は更新していない。実装・build完了後にオーケストレーターが行う。
- 未完: Mac実GUIでempty→folder/drop→loaded、VoiceOver、palette/sizeキー操作、resize/hoverの
  目視確認。Windows Session 1でfolder picker、file/folder drop、100/125/150/200% DPI、
  親子HWND境界、resize/hover/palette/sizeの無ちらつき、実ペン入力を確認する。

### 最終検証・レビュー追記

- `cargo fmt --all -- --check`成功。
- `cargo test --workspace`成功（既知のopacity<1 GPU parity 2件は従来どおりignore）。
- `apps/windows/AkapenApp.Tests`は193/193 assertions成功。
- `scripts/check-header-parity.sh`成功。canonical headerとMac mirrorはbyte-identical。
- `xmllint --noout assets/icons/akapen-ui-icons.svg`成功。`git diff --check`成功。
- 多軸レビューで、Windows size目盛りの固定赤を現在色へ修正し、下部固定化後に無効となった
  右／左dock設定をMac設定UIから撤去、Windows設定表示を下部固定の説明へ変更した。
- Mac size knobへslider role、min/max、increment/decrementを追加。Windows back buffer生成失敗時も
  `EndPaint`を通って安全に抜けるガードを追加した。
- 再検証: Mac build＋UI契約5/5、raw Win32 UI契約＋win-x64 build、SVG/diff checkは全て成功。
