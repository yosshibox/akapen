# Akapen 引き継ぎ文書 — 2026-07-13

## 1. 引き継ぎ要旨

Akapen は Windows Win32 MVP を優先して開発中。Mac SwiftUI を UI の正本とし、Windows は同じ状態遷移・操作モデルを Win32 に写像する方針である。VEDA/Electron への移植性、描画性能、筆圧 API の取得可能範囲は考慮するが、現時点では Windows MVP の実機受入を最優先とする。

今回の担当は、既存の Sol Medium 実装を確認し、Windows 配布物を実機へ同期したところで終了した。次担当は、実機スクリーンショットに基づく UI 差戻しを実装する前に、必ず現行仕様と本書を読み、既存の誤ったスレッドやモデルを再利用しないこと。

## 2. ユーザーからの最新差戻し

以下が最新要求の要点である。

- フォルダを開くボタンで固まる問題を修正する。
- 空状態画面を完全に中央揃えにする。
- ファイルドロップで開ける経路は維持する。
- 開いた後の左右矢印キーで前後画像へ移動できるようにする。
- ツール群は画面下ではなく、描画領域の完全な右横に置く。受入時に下部配置は拒否する。
- スライダーはより縦長にする。
- 実際のペン先サイズを示す円形プレビューを表示する。Photoshop を参考にする。
- サイズの px 数字を常時表示する。
- スライダー上部の逆三角形は廃止または意図を再設計する。
- 昔の Windows の縦ボリュームフェーダーを参考にする。ただし音量 UI ではないため、Y 軸線対称の視覚構造にする。
- 添付スクリーンショットを必ず参照する。

参照画像はユーザーの依頼に添付された以下の3枚。

- `/Volumes/Users/yosshi/Pictures/ScreenShots/キャプチャ2007.png`
- `/Volumes/Users/yosshi/Pictures/ScreenShots/キャプチャ200８.png`
- `/var/folders/yy/pvcrk7cx1kj9gypl0tn_xcjc0000gn/T/TemporaryItems/NSIRD_screencaptureui_IwAFqj/Screenshot 2026-07-13 at 20.12.44.png`

## 3. 直前に完成していた実装

Sol Medium により、次の変更が実装済みである。

- Mac SwiftUI に `empty/loading/loaded` の表示状態を追加。
- 起動時はフォルダ選択と画像ドロップ UI のみ表示。
- 画像読み込み後に canvas とツールドックを表示。
- 常設ドックを Pen/Eraser、10色パレット、小型サイズ UI に整理。
- パレットを円形10色・横一列へ変更。
- Windows Win32 側も同じ状態・寸法・配置へ写像。
- Win32 の `WM_ERASEBKGND` 抑制、memory DC、全体描画、単一 `BitBlt` によるちらつき対策。
- Mac の暗黙 animation/transition を抑制。
- Mac のネイティブメニューへ保存・Undo・Redo を移動。
- 旧ドック位置設定は保存互換性を残し、現行 UI からは撤去する方向で修正。

主要ファイル:

- `apps/mac/Sources/AkapenApp/ContentView.swift`
- `apps/mac/Sources/AkapenApp/SidePanelView.swift`
- `apps/mac/Sources/AkapenApp/AppState.swift`
- `apps/mac/Sources/AkapenUIContract/WorkspaceContract.swift`
- `apps/windows-probe/AkapenProbe/Program.cs`
- `apps/windows-probe/AkapenProbe/Ui/DockLayout.cs`
- `apps/windows-probe/AkapenProbe/Ui/NativeUiRenderer.cs`
- `apps/windows-probe/AkapenProbe/Ui/UiContracts.cs`

## 4. 検証済み事項

- Mac SwiftPM build 成功。
- Mac UI 契約テスト 5/5 成功。
- Windows UI 契約テスト成功。
- Windows Win32 `win-x64` build 成功。
- Rust workspace test 成功。
- 既存 Windows 設定テスト 193/193 成功。
- `scripts/check-header-parity.sh` 成功。
- SVG XML 検証成功。
- `git diff --check` 成功。

ただし、以下は未確認である。

- Windows 実機でフォルダ選択ボタンを押したときの固まり。
- Windows 実機での左右矢印キーによる画像移動。
- Windows 実機での右側ドック表示。
- 100/125/150/200% DPI。
- ペン入力・筆圧・円形サイズプレビュー。
- resize/hover 時の実機ちらつき。
- Mac 実 GUI の VoiceOver と実操作。

## 5. Windows 配布物

SSH 接続先:

- ユーザー: `yosshi`
- ホスト: `192.168.11.34`
- 鍵: `/Users/yosshi/.ssh/veda_win_ed25519`

最新配布物は実機の次にある。

`C:\Users\yosshi\Downloads\AkapenWin32Mvp-win-x64.zip`

直前の確認値は 36,196,995 bytes。次担当が UI を修正した場合は、必ず再 publish してから実機へ配置すること。AppleDouble (`._*`) を Windows ソースへ同期しないこと。

## 6. 重要な設計判断

既存設計書には旧案が残っている。次の改訂節を優先し、後続の旧記述をそのまま再採用しないこと。

- `docs/akapen-ui-detailed-design-gpt-5.6-sol-medium-2026-07-13.md`
  - 先頭の「2026-07-13 差戻し改訂（Mac SwiftUIをUI正本とする）」
- `docs/daily-report-2026-07-13.md`
  - 「GUI再差戻し対応 — Mac SwiftUI正本／Windows mirror」
- `docs/仕様書.md`
- `CLAUDE.md`
- `MEMORY.md`（存在する場合はプロジェクトルートおよび親ディレクトリを確認）

最新差戻しでは、先行実装の「下部ドック」「小型ノブ」「逆三角形キャップ」をユーザーが明確に拒否している。次の実装ではユーザーの添付画像を正本として、右側縦ドック、縦長フェーダー、Y軸線対称のつまみ、円形サイズプレビューを再設計すること。

## 7. エージェント運用上の注意

今回、Sol Medium はシェル経由で実行されたため、Codex UI の管理対象サブエージェントとして科学者・哲学者名のタブには登録されていなかった。後続担当は、モデル名・推論強度・エージェント ID・UI 表示可否を実行前に確認すること。

誤って「Windows MVPを検証」という別スレッド（5.4 mini）へ最新差戻し文を送信した経緯がある。このスレッドは Sol Medium ではなく、今後の Akapen UI 実装の宛先に使用してはならない。

agmsg はユーザー許可済み。ただし、宛先が `harmony-engine` の別プロジェクトである場合は送信しないこと。Akapen の Sol Medium 宛先が agmsg 上に存在するかを確認できない場合、別エージェントへ誤送信せず、ユーザーへ報告すること。

## 8. 次担当の推奨作業順

1. 本書、`CLAUDE.md`、`MEMORY.md`、`docs/仕様書.md`、チャットログ相当の日報を読む。
2. 添付スクリーンショットと現在の Windows 実機画面を比較する。
3. `apps/windows-probe/AkapenProbe/Program.cs` のフォルダ選択・drop・左右キー経路を先に再現する。
4. Mac の空状態と右側ドックの UI 契約を先に設計し、Windowsへ写像する。
5. UI 契約テストを RED → 実装 → GREEN の順で追加する。
6. 実機でフォルダ選択、drop、左右キー、右側ドック、縦長フェーダー、円形プレビュー、ちらつきを確認する。
7. 日報を追記し、self-contained exe/zip を作成して Windows の Downloads へ同期する。

## 9. 引き継ぎ状態

- 本書作成時点で、コード変更と前回 Windows 配布物作成までは完了。
- 最新ユーザー差戻しは未実装。
- 本担当は解任対象。次担当は本書を読んだことを明示してから作業を開始すること。
