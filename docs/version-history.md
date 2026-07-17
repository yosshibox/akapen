# Akapen バージョンヒストリ

Windows出荷版インストーラの変更履歴。新しい順に記載する。SHA-256 はリポジトリ `dist/` の
成果物およびGitHub Release / 開発日誌の記録に基づく。dist の `*.exe` `*.msi` `*.dmg` はGit LFS管理。

## 1.2.0 (2026-07-17) — Windows / Mac 同時リリース

- バージョンをOS間で統一(以後、両OS同一バージョンで刻む)
- 共通: UIアイコン刷新(Windows=Fluent UI System Icons / Mac=SF Symbols)、起動ロゴ(朱の一筆)、マニュアルOS別分割
- Windows: 設定画面のカードUI化、MSIを全アーキテクチャの正式配布形式に
- Mac: 初回リリース(Photoshop準拠キーマップ・ナビゲーター・15度回転・扇形フェーダー・筆圧トグル・連番先読み。macOS 13+、未署名dmg)
- 配布物:
  - `Akapen-1.2.0-win-x64.msi` — SHA-256 `0da23ec83ae55c782ce19897ce63c9cd61af8083a2e093c6f9216f505e5cd4e1`
  - `Akapen-1.2.0-win-arm64.msi` — SHA-256 `6e40cc52831a05bb47916c72310ae8d091f2e1b7f4b3f6133246cee7038f2dfc`(実機未検証)
  - `Akapen-1.2.0-macos.dmg` — SHA-256 `ccfa411003a29edf1f83bc1860f33958c4109e30092400aa688f00ef19c039ad`

## 1.1.2 (2026-07-17)

### 変更点

- 使用許諾契約書(EULA)を平明な日本語・標準的な条項構成で全面的に書き直した(`japanese-tech-writing` 準拠)。リバースエンジニアリング禁止にはOSSライセンス許諾範囲の除外を明記(本体Apache-2.0との矛盾回避)。
- ヘルプ→「Akapen ヘルプ」からHTML版マニュアルを開けるようにした(アプリに同梱、オフライン閲覧可)。同梱ファイル優先、無ければGitHubへフォールバック。
- HTML版ユーザーマニュアル `docs/manual/akapen-manual-ja.html` を追加。
- リポジトリ再編: 旧WinUI検証シェルを廃止し、製品シェルを `apps/windows` へ改名(動作変更なし)。
- **Windows arm64版を追加**(Snapdragon等のARM64 Windows機向け)。csproj の RuntimeIdentifiers に win-arm64 を追加。

### 配布物

- `AkapenSetup-1.1.2-win-x64.exe`
  - 対象アーキテクチャ: Windows x64(.NETランタイム別途不要)
  - SHA-256: `73a36e12413b02d97de9496879b2efb0bfd2db82662e9c92b0dc8158d8ac2f87`
- `AkapenSetup-1.1.2-win-arm64.exe`
  - 対象アーキテクチャ: Windows arm64(クロスビルド。ARM64実機での動作は未検証)
  - SHA-256: `d690993f9433a803c20ef055357b53d3a1611c2a8cf28d464ce0bdb7b8368c44`
- GitHub Release: https://github.com/yosshibox/akapen/releases/tag/v1.1.2

## 1.1.1 (2026-07-17)

### 変更点

- 筆圧ON/OFFトグルを追加(`input.pressure`、既定ON)。Windows標準経路(WM_POINTER)の筆圧取得は維持しつつ、設定「ペン→筆圧を使う」とメニュー「ツール→筆圧」から切替可能(OFF時は一定の太さ)。
- タイトルバーが「A」とだけ表示される不具合を修正。`DefWindowProc` のCharSet未指定で `DefWindowProcA` に解決されUTF-16キャプションをANSI解釈していた原因を、`DefWindowProcW` 明示(GetMessage/PeekMessage/DispatchMessageもW固定)で根治。
- exe埋込アイコンを `ExtractIconEx` で取得し、タイトルバー左上・Alt-Tab・タスクバーに表示。
- ヘルプ→バージョン情報を追加。
- ツール→筆圧を明示的なサブメニュー化(ON/OFF + 筆圧カーブSoft/Normal/Hardのラジオ表示、OFF中はカーブをグレーアウト)。
- インストーラにEULA同意画面を追加(埋込 `EULA-ja.txt`、無人インストールは `--accept-eula`、非同意時は終了コード2)。
- ユーザーマニュアル `docs/manual/akapen-manual-ja.md` を追加。

### 配布物

- `AkapenSetup-1.1.1-win-x64.exe`
  - 対象アーキテクチャ: Windows x64(.NETランタイム別途不要)
  - SHA-256: `83809d3996d5318650b02438248c3f59a24bfe3f9d4fe791c4d1443ab7229842`
- GitHub Release: https://github.com/yosshibox/akapen/releases/tag/v1.1.1

## 1.1.0 (2026-07-17)

### 変更点

- **キーマッププリセット**: Photoshop準拠(既定)とCLIP STUDIOを切替可能に。Photoshop表はB=ペン、Ctrl+Shift+Z=Redo、Ctrl+Alt+Z=Undo、Ctrl+1=100%、R/Shift+R=回転(Ctrl+Yは非割当)。FFIに `akapen_resolve_key_preset` を追加。保存キーは `keymap.preset`。
- **矢印キー**(両プリセット共通): ←→=前/次フレーム、↑↓=ズームイン/アウト(物理キーのみ)。
- **15度回転**: 回転操作を±90度から±15度単位へ変更(`RotateView` に集約、mod 360正規化)。
- **ナビゲーター**: 新FFI `akapen_thumbnail_rgba`(コア側でボックス縮小)。ビューポート矩形をクリック/ドラッグでパン。ドック幅104→168 DIP。
- **設定画面刷新**: `app.manifest`(comctl32 v6 + PerMonitorV2)、Segoe UI、白背景、見出し+区切り線、ショートカット節(PS/CSPラジオ)。
- バージョン1.0.1→1.1.0。

### 配布物

- `AkapenSetup-1.1.0-win-x64.exe`
  - 対象アーキテクチャ: Windows x64(.NETランタイム別途不要)
  - SHA-256: `16b60db9b02bfefe6928e1cc0b90908f952edd63957dca5d281b7a17d5accf85`
  - GitHub Release未登録(dist/Git LFSで管理)。

## 1.0.1 (2026-07-17)

### 変更点

- Explorerからのフォルダ読み込みでクラッシュする不具合を修正(コミット `7918d40` "fix(windows): prevent Explorer folder load crashes")。
- 容量制約端末向けにDドライブ指定インストール(`--install-dir D:\Akapen`)へ対応。

### 配布物

- `AkapenSetup-1.0.1-win-x64.exe`
  - 対象アーキテクチャ: Windows x64(.NETランタイム別途不要)
  - SHA-256: `ee7d43b7ecc934a8aacae8e30bdac0e1a9ca0129a635fef5da3367f1ba17bc03`
  - GitHub Release未登録(dist/Git LFSで管理)。

## 1.0.0 (2026-07-13)

### 変更点

- Windows出荷版(raw Win32製品シェル)を確定。MVPではなく現時点のAkapen完成版と位置付け、以後のMac版はこのWindows版を正本に移植する方針とした。
- 画像領域と右側ツールドックを非重複配置。矢印/ペン/消しゴムを右上に、直下に扇形の縦長ブラシサイズUI(円形プレビュー・px数値即時同期)を配置。
- CLIP STUDIO準拠ショートカット(`P`/`E`/`A`、Undo/Redo、左右移動、`-`/`^` 等)、画像往復後の履歴保持、ライブ消しゴム、連続リサイズフィット、前後各72枚の連番先読み、`_review` 既定保存を実装。
- Surface/Windows Inkの筆圧、1本指パン・2本指ピンチズーム、ペン入力中の指の描画混入抑止。
- 設定画面(グループ化・標準GUIフォント・Tab移動・100/125/150/200% DPI対応、OKのみ保存)。通常配布はWinExe。

### 配布物

- `AkapenSetup-1.0.0-win-x64.exe`
  - 対象アーキテクチャ: Windows x64(.NETランタイム別途不要)
  - 既定インストール先: `%LOCALAPPDATA%\Programs\Akapen`
  - 容量制約端末向け: `AkapenSetup.exe --install-dir D:\Akapen`
  - SHA-256: `aa0e0d001d236f206fae9d2eab50ed6eec11608b2921b82fe501e74fdf6dbd1f`
  - GitHub Release未登録(dist/Git LFSで管理)。旧配布名 `AkapenSetup-win-x64.exe`(SHA-256 `ce6e204710a42ec5c3d70f34bc9acfcc82214600d7af756e4e0b148c9b1faa86`)から改名・再ビルドした。
