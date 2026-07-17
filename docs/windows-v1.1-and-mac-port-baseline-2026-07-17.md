# Akapen Windows V1.1 と Mac 移植正本

更新日: 2026-07-17
前版: `windows-final-and-mac-port-baseline-2026-07-13.md`(V1.0 正本)

## 決定

Windows V1.1 を現時点の Akapen 完成版とする。以後 Mac 版を開発・更新するときは、
V1.0 正本の受入条件に加えて本書の V1.1 仕様を正本として移植する。
コアの可搬性境界(Rust コア / C ABI / ベクター JSON / バインディング)は V1.0 から不変。

## V1.1 で追加・変更した仕様

### 1. ショートカットのキーマッププリセット(既定 = Photoshop 準拠)

- キー→アクション写像は従来どおり Rust コアの純関数が正本
  (`crates/akapen-core/src/keymap.rs`)。V1.1 で **プリセット機構**を導入した。
  - `KeymapPreset::Photoshop`(**製品既定**): Photoshop の既定ショートカットに
    存在するものは完全コピーする。`B`=ブラシ(ペン)、`Ctrl+Shift+Z`=やり直し、
    `Ctrl+Alt+Z`=元に戻す(step backward 相当)、`Ctrl+1`=100%、
    `Ctrl+0`=フィット、`R` / `Shift+R`=ビュー回転(右/左)。
    **`Ctrl+Y` は割当なし**(Photoshop では校正表示のため。忠実コピーの判断)。
    ベア `-` / `^` も割当なし(Photoshop に存在しないため)。
    Photoshop に相当機能がない Akapen 固有キー(P/U/A/O/T/I/X/C、`[` `]`、
    PageUp/PageDown)は衝突しないため維持。
  - `KeymapPreset::ClipStudio`: 従来の仕様書 §3 表そのまま
    (`Ctrl+Y`=やり直し、`-`/`^`=回転、`R`=矩形ツールキー)。
- C ABI: `akapen_resolve_key_preset(preset, ...)` を追加
  (`AKAPEN_KEYMAP_CLIPSTUDIO=0` / `AKAPEN_KEYMAP_PHOTOSHOP=1`)。
  既存 `akapen_resolve_key` は CLIP STUDIO 表のまま凍結(後方互換)。
- 設定は `keymap.preset`(`photoshop` 既定 / `clipstudio`)として
  `%LocalAppData%\Akapen\settings.json` に保存し、設定画面のラジオボタンで切替。

### 2. 矢印キー

- `←` / `→` = 前 / 次の画像(従来の PageUp/PageDown と等価)。
- `↑` / `↓` = ズームイン / ズームアウト(V1.1 新規)。
- ブラシサイズフェーダーにフォーカスがある間は、従来どおり
  `↑`/`↓`/`PageUp`/`PageDown`/`Home`/`End` がサイズ調整を優先する。
- 矢印はコア keymap の物理キー(`AKAPEN_PK_ARROW_*`)として両プリセット共通。

### 3. 回転は 15 度単位

- ビュー回転の 1 ステップを 90 度から **15 度**に変更(キー・ツールドックの
  回転ボタン・メニューの全経路)。座標変換・GPU 行列は元々任意角対応
  (`coord.rs` / `transform.rs`)のためコア変更なし。

### 4. Photoshop ライクなナビゲーター(右ドック上部)

- 右ドックを 104→168 DIP に拡幅し、最上部にナビゲーターを追加。
  - サムネイル: コアの新 C ABI `akapen_thumbnail_rgba`(表示合成を Rust 側で
    最大 256px へボックス縮小して返す)。ストローク確定・undo/redo・画像
    切替時に更新し、毎フレームはコピーしない。
  - ビューポート矩形: キャンバス四隅を逆変換して画像座標→サムネイル座標へ
    写像(回転時は平行四辺形になる。Photoshop と同挙動)。
  - 操作: サムネイルのクリック/ドラッグでその点を画面中央へパン。
    `−` / `+` ボタンでズーム、中央に倍率 % 表示。
  - 幾何は `DockLayout.Navigator`、写像は `NavigatorMath`(純関数・契約テスト有)。

### 5. 設定画面の刷新

- `app.manifest` で Common Controls v6(visual styles)へオプトインし、
  クラシック(Windows 3.1 風)描画を廃止。
- Segoe UI フォント、白背景、太字のフラットな節見出し+区切り線構成
  (GROUPBOX 廃止)、フッターの OK / キャンセル。OK のみ保存、
  キャンセルは無変更でキャンバスへフォーカス復帰(従来どおり)。
- 新設定: ショートカット節(Photoshop 準拠(既定)/ CLIP STUDIO PAINT 準拠)。

## Mac 移植時の受入条件(V1.0 分に追加)

- キーマッププリセットは `akapen_resolve_key_preset` 経由で解決し、
  既定 = Photoshop、設定で CLIP STUDIO へ切替できること。
  mac シェルの設定キーは Windows と同名 `keymap.preset` を用いる。
- 矢印キー(←→=フレーム、↑↓=ズーム)、15 度回転、ナビゲーター
  (サムネイル+ビューポート矩形+クリックパン+ズーム操作)を省略しない。
  ナビゲーターのサムネイルは `akapen_thumbnail_rgba` を使い、
  シェル側で全画素合成を複製しない。
- 設定画面は SwiftUI の標準外観へ自然に翻訳してよいが、節構成
  (ショートカット / キャンバス / 画像切り替え / 保存先)と OK/Cancel の
  セマンティクスを維持する。
- VEDA への供給境界(§7.2 の 3 点セット+VectorDoc)は本版で不変。
  keymap プリセットとサムネイルは表示層の追加であり、
  ベクター JSON スキーマに変更はない。

## 配布物

- ファイル: `dist/AkapenSetup-1.1.0-win-x64.exe`
- 対象: Windows x64、.NET ランタイム別途不要(self-contained)
- SHA-256: `16B60DB9B02BFEFE6928E1CC0B90908F952EDD63957DCA5D281B7A17D5ACCF85`
- 既定インストール先: `%LOCALAPPDATA%\Programs\Akapen`
- 容量制約端末向け: `AkapenSetup.exe --install-dir D:\Akapen`

## 検証記録(2026-07-17)

- Rust workspace 全テスト PASS(macOS / Windows 実機の両方。
  keymap プリセット・矢印・`akapen_thumbnail_rgba` の新規テスト含む)。
- `cargo clippy --workspace --all-targets -- -D warnings` PASS。
- `scripts/check-header-parity.sh` PASS(mac ミラーヘッダ同期済み)。
- AkapenProbe UI contract tests PASS(macOS / Windows 実機の両方。
  ナビゲーター幾何・キーマップ設定の新規契約を含む)。
- `--headless-export` PASS(192.168.11.34)。
- `--interactive-smoke`(実 GUI・DX12 present・描画・自動保存):
  192.168.11.34(デスクトップセッション経由)と Surface Go 192.168.11.18
  (`D:\Akapen` インストール)の両方で PASS。
- インストール: 192.168.11.34 = 既定パス、192.168.11.18 = `D:\Akapen`
  (C ドライブ空き 2.5GB のため D 指定)。両機 FileVersion 1.1.0.0 確認。
- 未検証(人手での確認待ち): ペン実機の筆圧を伴うナビゲーター/新設定画面の
  目視確認、Photoshop 手癖での通し操作。
