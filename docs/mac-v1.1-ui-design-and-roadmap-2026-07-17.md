# Akapen Mac 版 UI デザイン・詳細設計・実装ロードマップ

作成日: 2026-07-17
正本(UX): `windows-v1.1-and-mac-port-baseline-2026-07-17.md`(V1.1)+ V1.1.1 変更
(開発日誌 2026-07-17 続章)。本書はこれを macOS / SwiftUI へ翻訳する設計書である。

## 0. 前提と方針

- **Windows V1.1.1 の操作性・状態遷移・視覚的情報階層が正本**。SwiftUI の外観へ
  自然に翻訳してよいが、ツール順序・主要操作・既定値を Mac 独自判断で変えない。
- コアは既存の Rust クレート群+C ABI をそのまま使う。**Mac 版のためにコアへ
  UI 依存を持ち込まない**(仕様書 §7 の境界を維持)。V1.1 で追加済みの
  `akapen_resolve_key_preset` / `akapen_thumbnail_rgba` がそのまま使える。
- 既存資産: `apps/mac`(M2 期の SwiftUI シェル約 1,700 行。AppState / CanvasView /
  SidePanelView / SettingsView / UI 契約テスト)。**新規書き起こしではなく増改築**とする。
- 表記: 本書で「⌘」は Command。Windows の Ctrl は mac では primary=⌘ に写像される
  (keymap コアの `Modifiers.primary` がこの吸収を担う。実装済み)。

## 1. UI デザイン

### 1.1 画面構成(Windows V1.1.1 の写像)

```
┌───────────────────────────────────────────────┬──────────────┐
│                                               │ ナビゲーター   │
│                                               │ [サムネイル]   │
│                                               │ [−] 100% [+]  │
│                                               ├──────────────┤
│              キャンバス                        │ 矢印 ペン 消し │
│         (CAMetalLayer / wgpu)                 │ ──────────    │
│                                               │ ブラシフェーダー│
│                                               │ (扇形+px値)   │
│                                               │ ──────────    │
│                                               │ パレット 2×5  │
├───────────────────────────────────────────────┴──────────────┤
│ ファイル名  倍率  ペン先 px  未保存                              │
└──────────────────────────────────────────────────────────────┘
```

- **右ドック幅 168pt**(Windows の DIP と同値)。ナビゲーター → ツール →
  ブラシフェーダー → パレットの縦順を維持。
- **メニューバー**は macOS 標準へ翻訳する:
  - Akapen メニュー: About Akapen(バージョン情報)/ 設定…(⌘,)/ Quit
  - ファイル: 開く…(⌘O)/ 画像フォルダを選択 / 保存(⌘S)
  - 編集: 元に戻す(⌘Z)/ やり直す(⇧⌘Z)
  - 表示: 前の画像 / 次の画像 / ウインドウに合わせる(⌘0)/ 実寸(⌘1)
  - ツール: ペン / 消しゴム / パン / ズーム / 回転 / ブラシサイズ / 色 /
    筆圧(サブメニュー: 筆圧を使う ✓+カーブ3種ラジオ)/ 不透明度(無効)
  - ヘルプ: Akapen ヘルプ(オンラインマニュアルを開く)
- **バージョン情報**: mac 慣習どおり About パネル
  (`Akapen Version <x.y.z> macOS` / `(C) 2026 Yoshino Yoshikawa`)。
- **ウィンドウタイトル**: `Akapen`。アプリアイコンは Windows と同一図案の
  `.icns` を用意(assets/app-icon から生成)。

### 1.2 ナビゲーター(V1.1 の中核追加)

- SwiftUI `Canvas`(または NSView 直描き)で、`akapen_thumbnail_rgba`(≤256px)の
  BGRA→CGImage を表示。ビューポート矩形は `NavigatorMath` と同式を Swift へ移植
  (回転時は平行四辺形)。
- クリック / ドラッグ = その画像点を画面中央へ(pan = −zoom·R(θ)·(p−imgC))。
- 下段に `−` / 倍率% / `+`。更新契機はストローク確定・undo/redo・画像切替のみ。

### 1.3 設定画面(⌘,)

SwiftUI `Settings` シーンの Form へ翻訳。節構成は Windows と同一:

| 節 | 項目 | 既定 |
|---|---|---|
| ショートカット | Photoshop 準拠(既定)/ CLIP STUDIO PAINT 準拠 | Photoshop |
| ペン | 筆圧を使う | オン |
| キャンバス | 画像の範囲外を黒にする | オフ |
| 画像切り替え | 切り替える前に自動保存する | オン |
| 保存先 | 隣接 `_review` / 同フォルダ / 指定フォルダ+フォルダ名/パス | 隣接 `_review` |

- mac は SwiftUI Form の即時反映が慣習だが、**正本の「OK で保存 / Cancel で破棄」
  セマンティクスを維持**する(Apply/Cancel ボタン付き Form。既存 SettingsView の方式を踏襲)。
- 設定キーは Windows と同名(`keymap.preset`, `input.pressure`, `ui.canvasBackdrop`,
  `navigation.autoSave`, `save.*`)。`@AppStorage` ではなく共通 JSON
  (`~/Library/Application Support/Akapen/settings.json`)へ寄せ、キー名の 1:1 を守る。

### 1.4 キーマップ(両プリセット)

- 解決は全キーを `akapen_resolve_key_preset` に集約(mac の `keyDown` →
  `charactersIgnoringModifiers` + 物理キー + modifiers)。
- Photoshop 準拠(既定): `B`/`P`=ペン、`E`=消しゴム、`⌘⇧Z`=やり直し
  (`⌘⌥Z` も戻す)、`⌘Y` 非割当、`⌘1`=100%、`R`/`⇧R`=回転±15°。
- CLIP STUDIO 準拠: `⌘Y`=やり直し、`-`/`^`=回転、ほか仕様書 §3。
- 矢印: `←`/`→`=前後フレーム、`↑`/`↓`=ズーム(コア実装済み・両プリセット共通)。
- mac 固有の予約(⌘Space=Spotlight)への代替 `⌘=`/`⌘-` ズームは実装済みのまま。

### 1.5 入力(ペン・トラックパッド・タッチ)

- NSEvent タブレットイベント(`pressure`, `subtype == .tabletPoint`)を筆圧として採用。
  トラックパッドの Force Touch 圧は筆圧に**使わない**(仕様書 §5.4)。
- `input.pressure` オフ時は固定 1.0(Windows と同じ意味論)。
- スクロール=パン、ピンチ(magnify)=ズーム、2本指回転(rotate gesture)は
  15° スナップの回転として扱う(将来検討。初期リリースは R キーのみで可)。

## 2. 詳細設計

### 2.1 モジュール構成(既存 apps/mac を増改築)

```
apps/mac/Sources/
├─ AkapenKit/AkapenEngine.swift      … C ABI ラッパ(既存)。V1.1 API を追加:
│     resolveKey(preset:…) / thumbnail(maxW:maxH:) → (pixels, w, h)
├─ AkapenApp/
│   ├─ AkapenApp.swift               … App シーン+Settings シーン+メニューコマンド
│   ├─ AppState.swift                … zoom/pan/rotationDeg/tool/dirty/連番/設定(既存拡張)
│   ├─ CanvasView.swift              … CAMetalLayer ホスト NSView+keyDown/タブレット入力(既存拡張)
│   ├─ SidePanelView.swift           … 右ドック。NavigatorView を最上部へ追加
│   ├─ NavigatorView.swift           … 新規(§1.2)
│   ├─ NavigatorMath.swift           … 新規。Windows Ui/UiContracts.cs の NavigatorMath と同式
│   ├─ SettingsView.swift            … 節追加(ショートカット / ペン)
│   └─ PaletteColors.swift           … 既存(10色)
└─ AkapenUIContract/                 … 幾何・状態の純ロジック契約(Windows UiContracts と対)
```

### 2.2 コア境界(追加分のみ)

| C ABI | Swift ラッパ | 用途 |
|---|---|---|
| `akapen_resolve_key_preset(preset, ch, physical, primary, shift, alt, composing, textEditing)` | `AkapenKeymap.resolve(_:preset:)` | 全ショートカット解決 |
| `akapen_thumbnail_rgba(engine, maxW, maxH, out, len, &w, &h)` | `engine.thumbnail(max: 256)` | ナビゲーター |

物理キーコード表(AKAPEN_PK_*)は mac の `keyCode`(kVK_*)から写像する変換表を
`AkapenKeymap` に置く(矢印 = kVK_LeftArrow 123 / Right 124 / Down 125 / Up 126、
B = kVK_ANSI_B 11、1 = kVK_ANSI_1 18 など)。JIS `^`(kVK_ANSI_Equal 相当の
JIS 配列差)は V1.0 実装の防御(文字優先・US `=` 非発火)を踏襲。

### 2.3 状態と更新契機

- `AppState`(ObservableObject)に追加: `keymapPreset`, `pressureEnabled`,
  `navThumbnail: CGImage?`。
- サムネイル更新契機: `strokeEnded` / `undo` / `redo` / `openImage` /
  `documentSwap`。60Hz 描画ループでは更新しない(Windows と同じ規律)。
- ビューポート矩形は AppState の zoom/pan/rotation から毎描画で計算(軽量)。

### 2.4 保存・連番・自動保存

V1.0 mac 実装(3点セット、`_review`、自動保存、連番 step)を維持。
V1.1 で挙動変更なし。フォルダ選択起動・ドロップ起動は Windows V1.0 相当を
mac の NSOpenPanel / onDrop へ写像(未実装なら M-mac2 で追加)。

### 2.5 配布

- 署名・公証(Developer ID + notarytool)前提の `.dmg`。
- EULA: dmg のライセンス表示(`licenseAgreements`)または初回起動ダイアログで
  Windows と同一文面(`EULA-ja.txt`)を表示。
- Sparkle 等の自動更新は当面入れない(GitHub Releases 手動配布)。

## 3. 実装ロードマップ

前提: コア変更なし(V1.1 で完了済み)。各マイルストーンは
「純ロジックのテスト先行 → 実機検収」の既存規律で進める。

| M | 名称 | 内容 | 受入条件 |
|---|---|---|---|
| **M-mac0** | ビルド復旧・土台 | 既存 apps/mac を最新コアでビルド可能に戻す。AkapenKit へ V1.1 API(resolve_key_preset / thumbnail)を追加。設定ストアを共通 JSON(`settings.json`)へ移行 | `swift build` + AkapenUIContractTests 緑。設定キーが Windows と 1:1 |
| **M-mac1** | キーマップ・回転・矢印 | keyDown を resolve_key_preset へ一本化。Photoshop 既定/CSP 切替。回転 15°、矢印(←→フレーム / ↑↓ズーム) | 契約テスト+実機で両プリセットの全表が通る。IME/JIS 回帰(B15/B21/`^`)緑 |
| **M-mac2** | ドック V1.1 化 | ドック幅 168pt、ツール順・扇形フェーダー・パレットを Windows 正本と一致。フォルダ選択起動・ドロップ・空状態 | 見た目と操作が Windows V1.1 スクリーンショットと対応。契約テストの幾何一致 |
| **M-mac3** | ナビゲーター | NavigatorView + NavigatorMath 移植。サムネイル・ビューポート矩形・クリックパン・ズーム行 | 回転含む矩形が Windows と同座標(共通テストベクタ)。4K 画像でドラッグ追従にもたつきなし |
| **M-mac4** | 設定・筆圧・About | 設定画面の節構成(ショートカット/ペン/キャンバス/切替/保存先)、筆圧トグル+カーブメニュー、About パネル、アイコン(.icns) | 設定 round-trip、筆圧 OFF で一定幅、タイトル/アイコン/About 表示 |
| **M-mac5** | 品質・配布 | WACOM 実機の筆圧マトリクス(仕様書 §5.6 の mac 列)、パームリジェクション、署名・公証 dmg + EULA、マニュアルへ mac 節追記 | 実機検収(板タブ/トラックパッド)、公証済み dmg を GitHub Release へ |

- 目安順序は依存順そのもの(M-mac0 → 1 → 2 → 3 → 4 → 5)。M-mac2 と M-mac3 は並行可。
- リリース名は **Mac V1.2**(Windows V1.1.1 と同一 UX 世代)を予定。
- リスク: SwiftUI ↔ NSView 混在部(キーイベントとフォーカス)。V1.0 期に
  CanvasNSView の keyDown 経路は実証済みのため、新規リスクはナビゲーターの
  描画性能のみ(サムネイル更新契機を絞る設計で回避)。

## 4. 未決事項(発注者判断)

1. Mac 配布形態: 署名・公証 dmg(推奨)か、zip 直配布か。
2. 2本指回転ジェスチャの採用可否(15° スナップ)。初期は R キーのみを推奨。
3. Mac 最低対応 OS(推奨: macOS 14 Sonoma 以降。wgpu/Metal と SwiftUI 安定性)。
