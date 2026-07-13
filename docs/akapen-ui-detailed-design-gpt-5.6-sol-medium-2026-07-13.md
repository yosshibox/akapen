     828 /tmp/akapen-sol-ui-design.md
# Akapen 全体UI詳細設計

> 設計日: 2026-07-13  
> 注記: GPT-5.6-Sol（reasoning medium）がロードマップ、詳細設計、仕様、三面の実装をread-onlyで照合したUI設計を、オーケストレーターが成果物として登録した。
> 優先順位: Windows raw Win32 MVP → Mac SwiftUI/AppKit写像 → VEDA Electron統合

## 2026-07-13 差戻し改訂（Mac SwiftUIをUI正本とする）

本節は、後続の「Windows起点」「左右232 DIPインスペクター」「5×2パレット」
「大きな縦フェーダー」「ファイル／履歴／表示commandをドックへ常設」の記述より優先する。
前回実装は操作を過剰に常設し、起動直後から空キャンバスを生成したため、画面の目的と
視覚階層が不明瞭になった。今回の正本はMac SwiftUI MVPであり、Windowsは次の構造を
DIP単位で忠実にmirrorする。

### 状態と表示契約

| phase | 表示 | 非表示 |
|---|---|---|
| `empty` | 「画像を開くフォルダを選択」ボタン、画像drop領域、対応形式 | canvas、Metal/DX12 surface、tool dock |
| `loading` | 静的な読込表示 | canvas、tool dock、重複drop |
| `loaded` | canvas、96pt/DIP下部tool dock、24pt/DIP status | empty/drop UI |

フォルダ選択は、その直下の対応画像を自然順に並べた先頭を既存の画像Open経路へ渡す。
ファイルOpen、drop、Prev/Next、保存経路は既存のまま維持する。画像のengine生成に失敗した
場合は、既存文書があれば`loaded`へ、無ければ`empty`へ戻す。

```text
empty / loading
┌────────────────────────────────────────────────────┐
│          画像を開くフォルダを選択                  │
│        ┌ 画像をここへドロップ ┐                    │
│        └───────────────────────┘                    │
├────────────────────────────────────────────────────┤
│ 24 status                                           │
└────────────────────────────────────────────────────┘

loaded
┌────────────────────────────────────────────────────┐
│                      Canvas                        │
├────────────────────────────────────────────────────┤
│ 96  Pen Eraser │ ●●●●●●●●●● │ compact size knob  │
├────────────────────────────────────────────────────┤
│ 24 status                                           │
└────────────────────────────────────────────────────┘
```

### loaded tool dock

- 常設command buttonは`tool.pen`と`tool.eraser`だけとする。
- Open、Save、Undo、Redo、Zoom、再読込、表示、シーケンス、設定はドックへ置かない。
  OSメニュー、ショートカット、ホイール／ピンチ／Space dragなど既存経路は維持する。
- 色はMS Paintの「色をツールから直接選ぶ」操作を調査し、既存10色値を維持したうえで、
  Akapen独自の丸いスウォッチへ再構成する。10個は常に横1列、1クリック選択、選択中は
  3px accent ringで示す。狭い幅では24から最小14pt/DIPまで縮小し、2列化しない。
- サイズは48×72pt/DIP（最大52×76）の小型縦型コントロールとする。上部に明確な
  下向き三角capを置き、5段の小目盛りと現在pxだけを表示する。大きなrail、太細説明、
  実径previewは置かない。1–50px、click、drag、矢印±1、Page Up/Down±5、Home/Endを維持する。
- MacはSwiftUIの自然な余白・SF Symbolsを使い、Windowsは共有SVGを同じportable command
  IDへ割り当てる。core/FFI、canvas host、描画、保存の責務境界は変更しない。

### ちらつき禁止契約

- Macはphase差し替え、hover、palette、size操作に暗黙animationやtransitionを使わない。
  Metal childは`loaded`でengineが存在するときだけ生成する。
- Win32 main HWNDは`WM_ERASEBKGND`を処理済みで返し、`BeginPaint`から互換memory DCへ
  client全体を描いて最後に1回だけ`BitBlt`する。mainは`WS_CLIPCHILDREN`、canvas childは
  `WS_CLIPSIBLINGS | WS_CLIPCHILDREN`を持つ。
- resizeは実physical sizeが変わった場合だけrender surfaceをresizeする。hoverはcommandが
  変わったときだけ、palette/sizeは値が変わったときだけmainをinvalidateし、erase要求を
  発行しない。
- 契約テストはempty/loading/loaded、丸10色1列、小型size control、Macのanimationなし、
  Win32の背景消去抑制／memory back buffer／whole-client paint／状態変化invalidateを検査する。

### 可搬境界

今回変更するのはMac SwiftUI view/state adapterとWindows raw Win32 layout/paint/input adapter
だけである。`crates/`、C ABI、portable command ID文字列、VectorDoc、3点セット、wgpu surface、
VEDA adapterは変更しない。将来VEDAへ写像するときも`empty/loading/loaded`とloaded dockの
寸法・順序を同じ契約として使用する。

## 2026-07-13 実装改訂（常設スタイル操作）

本節は、後続節に残る64/88 DIPレールおよびBrush/Color flyout案より優先する。
前回案は全commandを同じ重みで並べ、色とサイズを間接操作へ隠したため廃止した。

- 作業ドックは232 DIPの固定インスペクターとし、右既定・左設定を維持する。
- 主ツール5個を最も強い選択グループとし、ファイル/履歴、表示/シーケンス、設定は小さいアクション帯へ分離する。
- MS Paint風の固定10色を5×2で常設し、1クリック選択、3px選択輪郭、独立した現在色previewを持つ。
- ペン先は1–50pxの縦フェーダーを常設する。上=50/太い、下=1/細い、クリックjump、drag連続変更、矢印±1、Page Up/Down±5、Home/End最小最大とする。
- flyoutは主スタイル操作に使用せず、canvas、dock、statusはいずれも非重複領域とする。
- canonical iconは`assets/icons/akapen-ui-icons.svg`。24×24、stroke 1.75、round cap/join、`currentColor`、文字なしを固定する。
- Win32製品UIから`TextOut`とcommand別GDI図形を除去する。MVP実装はSVG pathをGDI+ antialiasで描き、文字はYu Gothic UI優先のClearType描画とする。描画は`NativeUiRenderer`へ隔離し、Direct2D/DirectWriteへの置換時もportable command ID、hit-test、canvas HWND、core/FFI、保存経路を変えない。

## 0. 結論

Akapenの三面共通UIは、次の構造に統一する。

```text
┌──────────────────────────────────────────────┬────────┐
│                                              │ 64 DIP │
│                  Canvas                      │ Tool   │
│                                              │ Dock   │
│                                              │ right  │
├──────────────────────────────────────────────┴────────┤
│ 24 DIP Status: file / dirty / zoom / pressure / save │
└───────────────────────────────────────────────────────┘
```

- 全ツールは既定で画面右側の縦ドックへ置く。
- 設定により、再起動不要で左側へ切り替えられる。
- 上部常設ツールバーは設けない。
- ツール操作は文字ボタンではなく、独自のグラフィカルアイコンを中心にする。
- Windows、Mac、VEDAでcommand IDとショートカット意味論を共有する。
- OS差はファイルダイアログ、設定画面、フォーカス表現、メニュー統合、標準修飾キーの表示に限定する。
- 文書、履歴、保存、入力、ビュー状態と、ドック位置・テーマなどのUI設定を分離する。

現状との差は大きい。Macには上部の文字ツールバーと右フローティングパネルが重複し、旧WinUIには文字中心の上部ツールバーと192 DIP固定右パネルがあり、raw Win32 Probeには製品ツールUIがない。これらを継ぎ足さず、共通command契約から縦ドックを新設する。

また、既存のWindows詳細設計はNode API定義の途中、Mac/VEDA計画はgolden fixture一覧の途中で実ファイルが終端し、末尾重複もある。文書修復を実装前ゲートに含める。

---

## 1. UI原則と情報階層

### 1.1 UI原則

1. **キャンバス最優先**  
   ウィンドウの残余領域をすべてキャンバスに割り当てる。ドックは通常64論理px、状態バーは24論理pxを超えない。

2. **一目で選べるアイコン**  
   Pen、Eraser、Panなどの選択は形、選択背景、カーソルの3要素で判別できるようにする。色だけで状態を伝えない。

3. **一操作一command**  
   アイコン、ショートカット、メニュー、ジェスチャーは同じ`UiCommand`を発行する。各面が独自に業務ロジックを持たない。

4. **一時ツールと選択ツールを区別**  
   Pen/Eraser/Pan/Zoom/Rotateは選択可能。Space Pan、Ctrl/Cmd+Space Zoom、Shift+Space Rotateは押下中だけ有効な一時overrideとする。

5. **保存安全性をUI状態へ反映**  
   `Saving`中は破壊的commandを無効化し、保存失敗時は現在文書、履歴、ビューを保持する。

6. **ネイティブ性は外殻で尊重**  
   WindowsはWin32 tooltip、UI Automation、フォーカス矩形、Macはmenu commands、VoiceOver、Settings scene、VEDAはDOM button、ARIA、Electron IPCを使用する。

### 1.2 情報階層

```text
Application
├─ Document
│  ├─ source path / sequence position
│  ├─ engine / history / dirty / save phase
│  └─ pressure source / warning
├─ View
│  ├─ zoom / pan / rotation / fit mode
│  └─ canvas viewport
├─ Tool
│  ├─ active tool
│  ├─ size / color / curve / opacity
│  └─ temporary override
└─ UI preferences
   ├─ dock side
   ├─ theme / high contrast
   └─ tooltip / touch options
```

`View`および`UI preferences`の変更は文書dirtyにしない。

---

## 2. ウィンドウ、キャンバス、ドック、設定レイアウト

### 2.1 共通論理寸法

| 要素 | 標準 | コンパクト条件 |
|---|---:|---:|
| 最小ウィンドウ | 720×480 | 変更不可 |
| ドック幅 | 64 DIP/pt/CSS px | 56、幅800未満 |
| アイコンボタン | 44×44 | 40×40 |
| アイコン本体 | 22×22 | 20×20 |
| ボタン間隔 | 4 | 2 |
| グループ間隔 | 8＋1px区切り | 6 |
| ドック内余白 | 左右10、上下8 | 8/6 |
| 状態バー | 24 | 高コントラスト時28 |
| flyout幅 | 280 | 表示領域に応じ240 |
| flyout最小行高 | 36 | 変更なし |
| フォーカスリング | 2px、外側1px | 変更なし |
| 最小操作領域 | 44×44 | マウス専用時のみ40 |

ドック内に収まらない場合は縦スクロールする。操作を隠す「…」だけのoverflowにはしない。現在選択ツール、Undo/Redo、Save、Next/Previousは常に初期表示範囲へ置く。

### 2.2 ドック内の順序

```text
Document: Open / Save
History:  Undo / Redo
Tools:    Pen / Eraser / Pan / Zoom / Rotate
Style:    Brush size / Color / Pressure curve / Opacity
View:     Fit / 100%
Sequence: Previous / Next
System:   Settings
```

Windows MVPではopacityを1.0固定とし、Opacityアイコンは表示するがdisabled＋「MVPでは不透明固定」のtooltipを出す。これにより将来追加時も配置が動かない。

### 2.3 flyout

Brush size、Color、Pressure curve、Opacityはアイコン押下でドックのキャンバス側へflyoutを開く。

- 右ドック: flyoutは左へ展開。
- 左ドック: flyoutは右へ展開。
- Esc、外側クリック、同一アイコン再押下で閉じる。
- slider操作中は閉じない。
- flyoutを閉じたら起点ボタンへフォーカスを戻す。
- キャンバスを覆うが、ドック側端から8px離し、影は1層だけにする。
- 設定画面は独立したネイティブウィンドウ／sceneとし、flyoutにはしない。

---

## 3. 右／左ドック設定と永続化

### 3.1 共通設定契約

```rust
pub enum DockSideV1 {
    Right = 0,
    Left = 1,
}

pub struct UiPreferencesV1 {
    pub schema_revision: u32, // 1
    pub dock_side: DockSideV1,
    pub theme: ThemePreferenceV1,
    pub tooltips_enabled: bool,
    pub finger_draw_enabled: bool,
}
```

永続キーは三面で同じ意味を持つ。

```json
{
  "ui.schemaRevision": 1,
  "ui.dockSide": "right",
  "ui.theme": "system",
  "ui.tooltipsEnabled": true,
  "input.fingerDrawEnabled": false
}
```

規則:

- 既定は`right`。
- 欠落、型不正、未知値は`right`へ戻し、非致命警告を記録する。
- 変更は即時反映し、文書dirtyにしない。
- 保存失敗時は画面上の選択を直前値へ戻し、設定画面にエラーを出す。
- 設定保存は一時ファイル＋replaceで行う。

### 3.2 プラットフォーム別永続化

- Windows: `%LocalAppData%\Akapen\settings.json`の既存`SettingsStore`へ`ui.dockSide`を加算的追加。
- Mac: `@AppStorage("ui.dockSide")`／`UserDefaults.standard`。
- VEDA: Electron main側の既存ユーザー設定ストア。rendererは`contextBridge.preferences.get/setDockSide()`のみ利用し、直接filesystemへ触れない。

設定画面では文字ラベルを使用してよい。ツールを文字ボタンに戻さないという制約は作業ドックに適用し、設定では「ツールドックの位置: 右／左」を明記して初見理解を優先する。

---

## 4. アイコン仕様

### 4.1 共通状態

各アイコンは以下の状態を持つ。

- `normal`: semantic foreground。
- `hover`: 8% surface overlay。
- `pressed`: 16% overlay、1px内側移動。
- `selected`: accent 20%背景＋2px accent輪郭＋必要なら左／右の3px marker。
- `focus-visible`: 高コントラストな2px外周。
- `disabled`: foreground 38%、背景なし、ポインタ入力不可。
- `warning`: 右上に8px三角badge。色だけでなく形を付ける。
- `busy`: Saveのみ16px spinnerを重ねる。アイコン形状は残す。

### 4.2 全アイコン一覧

`Primary`はWindows/EDAでCtrl、MacでCommandを意味する。

| ID / 図形 | 意味・状態 | ショートカット | tooltip | disabled条件 |
|---|---|---|---|---|
| `document.open` フォルダ＋画像角 | 画像を開く | Primary+O | 画像を開く (Ctrl/⌘O) | Loading/Saving/Closing |
| `document.save` 下向き矢印＋トレイ | 3点セット保存。dirty時badge、保存中spinner | Primary+S | レビューを保存 (Ctrl/⌘S) | 文書なし、Loading、Saving |
| `history.undo` 左向き曲線矢印 | 直前履歴へ | Primary+Z | 取り消す (Ctrl/⌘Z) | `canUndo=false`、Saving |
| `history.redo` 右向き曲線矢印 | redo | Primary+Y、Primary+Shift+Z | やり直す | `canRedo=false`、Saving |
| `tool.pen` 45°のペン先＋短い朱線 | 描画ツール。選択状態あり | P | ペン (P) | 文書なし、Loading/Saving |
| `tool.eraser` 斜め消しゴム＋消える点線 | 未保存ストローク消去 | E | 消しゴム (E) | 文書なし、消去対象なし |
| `tool.pan` 開いた手 | 選択パン。Space中は一時selected | Spaceドラッグ | 手のひら／移動 (Spaceドラッグ) | 文書なし |
| `tool.zoom` 虫眼鏡＋±小記号 | 選択ズーム | Primary+Space、Alt/Opt+Space | ズーム | 文書なし |
| `tool.rotate` 円弧＋キャンバス角 | 選択回転 | Shift+Spaceドラッグ | キャンバス回転 | 文書なし |
| `style.size` 太細2本線＋円径 | サイズflyout。現在径を下部4px previewで表示 | `[`, `]` | ブラシサイズ: N px ([ / ]) | 文書なし |
| `style.color` 重なった主／副色円 | 色flyout。主色そのものを中央に表示 | X、Iは将来 | 描画色: #RRGGBB | 文書なし |
| `style.pressure` S字カーブ＋入力/出力軸 | Soft/Normal/Hard/Custom | なし | 筆圧カーブ: Normal | 文書なし、pressure機能なし |
| `style.opacity` 市松＋半透明円 | opacity flyout | なし | 不透明度: 100% | MVPでは常時disabled |
| `view.fit` 四隅が内向き | 画像全体をviewportへ | Primary+0 | 全体表示 (Ctrl/⌘0) | 文書なし、viewport 0 |
| `view.actual` `1:1`を図形化した四角＋一点 | 画像pxと表示pxを1:1 | Primary+Alt/Opt+0 | 100%表示 | 文書なし |
| `sequence.previous` 縦線＋左三角 | 前フレーム | PageUp | 前のフレーム (Page Up) | 前候補なし、Saving |
| `sequence.next` 右三角＋縦線 | 次フレーム | PageDown | 次のフレーム (Page Down) | 次候補なし、Saving |
| `system.settings` 歯車 | 設定を開く | Primary+, | 設定 (Ctrl/⌘,) | Closing |
| `status.pressure` ペン先＋波形 | 状態バー。実測、fallback、警告 | なし | 筆圧入力の詳細 | 文書なし |
| `dock.side` 縦長画面＋左右帯 | 設定内のdock側 preview | なし | ツールドックの位置 | なし |

Zoomの＋／−は同一アイコンのsplit flyoutにせず、選択後にキャンバス左クリック＝in、Alt/Opt+クリック＝outとする。トラックパッド／wheel操作も同じview commandへ正規化する。

### 4.3 Brush、Color、Pressure、Opacityのflyout

- Brush: 1–50px、1px刻み。横slider、数値stepper、上部に実径preview。
- Color: 10色presetを2×5、各44×44。下部にOSネイティブcolor picker起動アイコン。色名はaccessible nameのみ常時保持。
- Pressure: Soft/Normal/Hardの3つの曲線サムネイル。CustomはMVP後。選択曲線にはcheckmarkを重ねる。
- Opacity: 0–100%、5%刻み。MVPでは操作不可。
- すべての値はエンジン未生成時もUI poseとして保持し、次のOpen後に適用する。

---

## 5. アイコン中心でも初見理解とアクセシビリティを確保する方法

- 全ボタンに可視文字を置かなくても、Windows UIA `Name`、Mac accessibility label/help、HTML `aria-label`/`aria-keyshortcuts`を必須にする。
- hover 500msまたはkeyboard focus直後に「操作名＋ショートカット」のtooltipを出す。
- 初回起動時だけ、Pen、Open、Save、Nextの4箇所に説明popoverを順番に表示する。スキップ可能、再表示はHelpメニューから行う。
- 選択状態は背景色だけでなく、2px輪郭、marker、カーソル形状で示す。
- disabledでも理由をtooltipまたはaccessibility descriptionで読めるようにする。disabled要素へ通常Tab focusは当てないが、設定の「操作状態を説明」から理由を確認できる。
- Tab順はドックの視覚順と一致。矢印キーで同一グループ内移動、Tabで次グループ、Enter/Spaceで実行。
- `F6`でCanvas → Dock → Status → Canvasを巡回する。
- ユーザー向け文言は日本語を正本とし、resource IDでローカライズする。
- CSPのアイコンを模写しない。操作互換だけを維持し、図形はAkapen独自にする。

---

## 6. 入力相互作用

### 6.1 ポインタ優先順位

```text
active pen contact
  > pen hover lock
  > explicit mouse action
  > deliberate touch navigation
  > palm touch ignored
```

正規化sampleには`pointer_id`、`kind`、`phase`、`pressure_source`、`timestamp_ns`、tilt flagsを持たせる。

- Pen: 描画。OSが返すcoalesced samplesをbatch送信。
- Mouse左: 現在ツール。pressure=1.0、source=`mouse_fixed`。
- Touch: 既定では描画しない。単指pan、二指pinch、二指rotateに限定。
- 指描画は設定で明示的に有効化した場合だけPen相当。
- Pen接触中およびPen Up後500msはtouchをignore。
- 合成mouseはPenイベント後100ms以内か、OSのpromoted flagが立つ場合に捨てる。

### 6.2 ジェスチャーとmodifier

| 操作 | 結果 |
|---|---|
| Space＋drag | 一時Pan |
| Shift＋Space＋drag | 一時Rotate |
| Primary＋Space＋click/drag | 一時Zoom In |
| Alt/Opt＋Space＋click/drag | 一時Zoom Out |
| wheel | Pan |
| Primary＋wheel | ポインタ位置を中心にZoom |
| pinch | ジェスチャー中心を固定してZoom |
| 二指rotate | Rotate |
| `[` / `]` | size ±1 |
| Alt/Opt押下中 | 将来Eyedropper。MVPでは未使用としてOSへ渡す |

modifierはpointer Down時にsnapshotし、stroke途中でツール意味を変更しない。Spaceをstroke途中で押しても現在strokeを中断せず、次のDownからPanになる。

### 6.3 capture、Cancel、範囲外

- 画像外Downはstrokeを開始しない。
- 画像内Down後の範囲外Move/Upはcaptureにより継続する。
- capture loss、window deactivate、device cancelは`Phase::Cancel`へ変換し、未確定strokeを破棄する。
- 現行のUp代用は廃止する。Cancelを正式な共通phaseとして追加する。

### 6.4 フォーカス、IME

- Canvas clickでcanvas focusを回復する。
- Settingsや将来Text toolのtext editor中は、Open/SaveなどOS標準commandを除き、単キーtool shortcutを発火させない。
- IME marked text中はRust keymapへ`composing=true`を渡し、常に`None`。
- キーはcharacter-first、physical fallback。JIS `^`とUS `=`の既存回帰を維持する。
- Windowsは`WM_GETDLGCODE`、`WM_CHAR`、IME composition状態、focused child classを見てtext editingを判定する。
- Macはfield editor／`hasMarkedText()`、VEDAは`isContentEditable`、input/textarea、`KeyboardEvent.isComposing`を使う。

---

## 7. Windows raw Win32具体実装

### 7.1 HWND構成

```text
Akapen.MainWindow                    WS_OVERLAPPEDWINDOW
├─ Akapen.CanvasWindow              child HWND → wgpu/DX12 surface
├─ Akapen.ToolDockWindow            child HWND
│  └─ owner-drawn BUTTON HWND群
├─ Akapen.StatusWindow              child HWND
└─ TOOLTIPS_CLASS                   topmost tooltip control

Akapen.SettingsWindow               owned top-level HWND
```

CanvasをMain HWNDそのものからchild HWNDへ分離し、dockの左右切替やstatus resizeがwgpu surfaceのhit testingと混ざらないようにする。

### 7.2 レイアウト

`WM_SIZE`で次を計算する。

```csharp
statusH = Dip(24);
dockW   = clientW < Dip(800) ? Dip(56) : Dip(64);

if (dockSide == Right) {
    canvas = [0, 0, clientW-dockW, clientH-statusH];
    dock   = [clientW-dockW, 0, dockW, clientH-statusH];
} else {
    dock   = [0, 0, dockW, clientH-statusH];
    canvas = [dockW, 0, clientW-dockW, clientH-statusH];
}
status = [0, clientH-statusH, clientW, statusH];
```

`DeferWindowPos`で3 childを一括移動し、最後に`akapen_render_resize`をphysical pxで呼ぶ。

### 7.3 DPI

- 起動直後に`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`。
- `GetDpiForWindow`を基準に`MulDiv(dip, dpi, 96)`。
- `WM_DPICHANGED`で推奨rectを適用し、アイコンatlas、tooltip max width、button bounds、canvas surfaceを再生成。
- `WM_GETDPISCALEDSIZE`を扱い、モニタ間移動時のジャンプを抑える。
- canvas座標はchild client px→共通`ViewTransform::screen_to_image`へ渡す。dock幅を座標式に手計算で足さない。

### 7.4 描画方式

- Canvas: 既存wgpu/DX12、frame latency 1、background＋baked＋wet ink。
- Dock: Direct2D＋DirectWrite、または`BS_OWNERDRAW`の`WM_DRAWITEM`。背景とfocus/selected状態はsemantic tokenから描く。
- アイコン: canonical SVGからビルド時にD2D PathGeometryまたは96/144/192 DPI atlasへ生成。GDIビットマップの拡大表示はしない。
- 状態バー: DirectWrite。pressure warningはアイコン＋短文で表示可能。
- `WM_THEMECHANGED`、`WM_SETTINGCHANGE`、`WM_SYSCOLORCHANGE`でbrushを再構築。
- canvasは`WM_ERASEBKGND`を処理済みとして返し、flickerを防ぐ。

### 7.5 hit testingとtooltip

標準child button HWNDを使うため、マウス・Tab・UI Automation hit testingを自前実装しない。flyoutのみ専用popup HWNDを使用する。

- `TOOLTIPS_CLASS`＋`TTF_SUBCLASS`。
- tooltip textはcommand registryから取得し、UI文字列を重複定義しない。
- `WM_NOTIFY/TTN_GETDISPINFO`で現在値を含める。
- Pen入力によるhoverではtooltipを遅延900ms、mouseは500ms、keyboard focusは即時表示可能。
- tooltipはキャンバス側へ出し、画面端を越えない。

### 7.6 keyboard

`WM_KEYDOWN/WM_SYSKEYDOWN`を`Win32KeyEventAdapter`で以下へ変換する。

```csharp
record KeyInput(
    uint Character,
    AkapenPhysicalKey Physical,
    bool Primary,
    bool Shift,
    bool Alt,
    bool IsComposing,
    bool IsTextEditing);
```

Open、Save、SettingsはWin32 shell command。その他は`akapen_resolve_key`を呼び、返却Actionを`CommandRouter`へ渡す。現行ProbeのVK分岐は製品経路から除去する。

### 7.7 dock切替

設定変更時にbutton HWNDを破棄・再生成せず、親dockとcanvasの位置だけを入れ替える。flyoutが開いていれば閉じ、起点buttonへfocusを戻してからレイアウトする。

---

## 8. Mac SwiftUI/AppKit実装

```text
ContentView
└─ HStack(spacing: 0)
   ├─ ToolDockView（dockSide == left）
   ├─ CanvasContainer → CanvasNSView / MetalHostView
   └─ ToolDockView（dockSide == right）
StatusBarView
```

- 現行`ContentView.toolbar`は削除する。
- `SidePanelView`を半透明・hover時だけ可視の補助UIから、常設64ptの`ToolDockView`へ置換する。
- `.ultraThinMaterial`の0.4 opacity退避は、視認性とコントラストを損なうため廃止。
- ボタンはSwiftUI `Button`を使用し、`.buttonStyle(AkapenDockButtonStyle)`でselected/focus/disabledを統一。
- Mac標準command menuにはOpen、Save、Undo、Redo、Settingsを残すが、作業画面では同じ操作を右／左dockのアイコンから実行できる。
- `@AppStorage("ui.dockSide")`変更を即時HStackへ反映する。
- flyoutは`.popover`を基本とし、矢印方向をdock sideで変える。
- VoiceOver label、help、valueを設定する。Brush sizeはadjustable actionに対応。
- `CanvasNSView`のAppKit入力、Metal child hit-test透過、Retina scale処理は維持する。
- `touchesCancelled`をUpで代用せず、C ABI v1のCancelへ送る。
- coalesced tablet samplesを`push_samples_v1`へbatch送信する。
- window closeは`NSWindowDelegate.windowShouldClose`から共通`AfterSaveIntent::Close`を実行する。
- `AppState.hasUnsavedStrokes`を廃止し、`HistoryStateV1.content_state_id`と`saved_state_id`でdirtyを計算する。

---

## 9. VEDA Electron／HTML／CSS／IPC実装

### 9.1 DOM構造

```html
<div class="akapen-shell" data-dock-side="right">
  <main class="akapen-canvas-region">
    <canvas class="akapen-canvas"></canvas>
  </main>
  <aside class="akapen-tool-dock" aria-label="注釈ツール"></aside>
  <footer class="akapen-status" role="status"></footer>
</div>
```

```css
.akapen-shell {
  display: grid;
  grid-template-rows: minmax(0, 1fr) 24px;
}
.akapen-shell[data-dock-side="right"] {
  grid-template-columns: minmax(0, 1fr) 64px;
}
.akapen-shell[data-dock-side="left"] {
  grid-template-columns: 64px minmax(0, 1fr);
}
.akapen-tool-button {
  inline-size: 44px;
  block-size: 44px;
}
```

DOM順はdock sideに追従させず、CSS grid areaだけを変更する。スクリーンリーダーとTab順はDocument→History→Tools→Style→View→Sequence→Settingsで固定する。

### 9.2 アイコンとアクセシビリティ

- `<button type="button">`内にcanonical SVGの`<use>`を置く。
- `aria-label`、`aria-pressed`、`aria-disabled`、`aria-keyshortcuts`を設定。
- tooltipはDOM上の`role="tooltip"`をbuttonと`aria-describedby`で関連付ける。
- `:focus-visible`のみfocus ringを出す。
- `prefers-color-scheme`、`forced-colors`、`prefers-reduced-motion`へ対応する。

### 9.3 Node/IPC境界

```text
renderer PointerEvent/coalesced events
  → requestAnimationFrame単位のbatch
  → contextBridge.akapen.pushSamples()
  → napi-rs AkapenDocument
```

高頻度入力はElectron main IPCへ送らない。main IPCは以下だけにする。

```ts
type MainRequest =
  | { type: "document.openDialog" }
  | { type: "document.saveTransaction"; request: SaveRequestV1 }
  | { type: "preferences.get" }
  | { type: "preferences.setDockSide"; side: "left" | "right" }
  | { type: "ledger.commitAnnotated"; transactionId: string };
```

`SUBMISSION_ANNOTATED`はtransaction成功後だけ発行する。`contextIsolation=true`、`nodeIntegration=false`を維持し、rendererへ汎用path/filesystem APIを公開しない。

---

## 10. 共通UI状態モデルとcommand契約

### 10.1 状態

```rust
pub struct UiStateV1 {
    pub document: DocumentUiStateV1,
    pub tool: ToolUiStateV1,
    pub view: ViewUiStateV1,
    pub input: InputUiStateV1,
    pub preferences: UiPreferencesV1,
    pub active_flyout: Option<FlyoutIdV1>,
    pub focused_region: FocusRegionV1,
}

pub struct DocumentUiStateV1 {
    pub phase: SessionPhaseV1,
    pub source_name: Option<String>,
    pub content_state_id: u64,
    pub saved_state_id: u64,
    pub has_in_progress_stroke: bool,
    pub can_undo: bool,
    pub can_redo: bool,
    pub has_previous: bool,
    pub has_next: bool,
}

pub struct ToolUiStateV1 {
    pub selected: ToolV1,
    pub temporary_override: Option<ToolV1>,
    pub size_px: f32,
    pub color_rgba: u32,
    pub pressure_curve: PressureCurveV1,
    pub opacity: f32,
}
```

### 10.2 command

```rust
pub enum UiCommandV1 {
    OpenDocument,
    SaveDocument { after: AfterSaveIntentV1 },
    SelectTool(ToolV1),
    SetBrushSize(f32),
    SetColor(u32),
    SetPressureCurve(PressureCurveV1),
    SetOpacity(f32),
    Undo,
    Redo,
    PanBy { dx: f64, dy: f64 },
    ZoomAt { factor: f64, screen_x: f64, screen_y: f64 },
    RotateAt { degrees: f64, screen_x: f64, screen_y: f64 },
    FitToWindow,
    ActualSize,
    StepFrame(SequenceDirectionV1),
    OpenSettings,
    SetDockSide(DockSideV1),
}
```

command結果は統一形にする。

```rust
pub enum CommandOutcomeV1 {
    Applied { state_revision: u64 },
    StartedAsync { operation_id: u64 },
    Ignored { reason: DisabledReasonV1 },
    Failed { code: AkapenStatus, recoverable: bool },
}
```

### 10.3 Rust keymap接続

```text
OS key event
 → KeyInput
 → akapen_resolve_key
 → Action
 → map_action_to_ui_command
 → CommandRouter.dispatch
 → UiState更新
```

Open、Save、SettingsはOS標準commandとして同じ`UiCommand`へ直結する。ショートカット体系をOSごとに分裂させず、表示上だけ`Ctrl`／`⌘`を切り替える。

---

## 11. テーマ、アイコン資産、色、OS差

### 11.1 資産

```text
assets/icons/
├─ source/*.svg
├─ manifest.json
├─ generated/windows/*.svg|pathdata
├─ generated/mac/*.pdf
└─ generated/web/sprite.svg
```

- viewBoxは24×24。
- stroke幅は1.75、round cap/join。
- 16/20/24pxでpixel-gridを検査する。
- `currentColor`を使用し、SVG内へ固定色を焼かない。
- Colorアイコンだけ現在色をsemantic fillとして使用可能。
- ロゴとツールアイコンを混同しない。
- CSPや他製品の図案をトレースしない。

### 11.2 semantic token

| token | Light | Dark | High contrast |
|---|---|---|---|
| canvas surround | `#262626` | `#161616` | system Canvas |
| dock surface | `#F3F3F3` | `#242424` | system Canvas |
| foreground | `#1A1A1A` | `#F2F2F2` | CanvasText |
| selected | OS accent 20% | OS accent 28% | Highlight |
| border | black 16% | white 18% | ButtonText |
| warning | `#9A6700` | `#FFCC66` | Mark |
| destructive/error | `#B42318` | `#FF817A` | system |

通常文字は4.5:1、アイコン・境界・focusは3:1以上。forced/high-contrastでは透明度、影、materialを無効化する。

### 11.3 OS差

- Windows: Segoe UI、Win32 focus rectangle、UIA、system accent。
- Mac: SF Pro、VoiceOver、menu command、vibrancyは設定画面に限る。ツール形状は共通資産を優先。
- VEDA: Electronの既存フォントとtheme tokenへ接続。CSS独自accentを増やさない。
- command ID、順序、tooltipの意味、disabled条件は共通に保つ。

---

## 12. テストと受入証拠

### 12.1 UI状態テスト

共通fixtureを作る。

```text
testdata/ui/
├─ command-state-transitions.json
├─ keymap-consumer-vectors.json
├─ dock-layout-vectors.json
├─ accessibility-contract.json
└─ input-routing-vectors.json
```

必須ケース:

- Empty→Open→Ready。
- stroke commit→dirty→Save→clean。
- 保存失敗後もdirty、engine、viewを保持。
- Undo/Redoで保存済みstate IDへ戻る。
- Saving中のOpen/Next/Close無効。
- right/left切替でcanvas座標が不変。
- IME、text editing中に単キーcommandなし。
- Pen＋palm＋合成mouseでstrokeが1本。
- Cancel後に未確定strokeなし。

### 12.2 画像golden

各面で以下を100%、125%、150%、200% DPI、light/dark/high contrastで取得する。

- Empty。
- 文書Open＋Pen selected。
- Eraser selected。
- Brush flyout。
- Color flyout。
- pressure warning。
- Saving。
- dock left/right。
- 720×480、1024×768、1440×900、4K。

比較はcanvas内容とOSフォントをmaskし、dock geometryとicon rasterをpixel diffする。許容差は通常0.5%、高DPI antialias境界1.0%。

### 12.3 アクセシビリティ

- Windows Accessibility Insights/UIA tree snapshot。
- Mac Accessibility Inspector/VoiceOver。
- VEDA axe-core＋keyboard-only。
- すべてのicon buttonにname、role、state、shortcut。
- Tab/F6順が仕様どおり。
- 200% text scalingでも操作欠落なし。

### 12.4 実機ユーザビリティ受入

最低5名、うち3名はCSP常用の作監・演出相当とする。

タスク:

1. 初見で画像を開く。
2. Pen→Eraser→Undo。
3. size/colorを変更。
4. pan/zoom/rotate。
5. Save→Next。
6. dockを左へ移す。
7. ペン＋手のひらで描く。

合格条件:

- 主要操作到達率95%以上。
- Open→一筆目中央値30秒以内。
- Save→Nextの誤操作ゼロ。
- アイコン意味の初回正答80%以上、tooltip確認後100%。
- dock位置変更後も再起動で保持。
- ペン＋パームで余分なstrokeゼロ。
- 保存失敗シナリオで「移動できなかった理由」を全員説明できる。

---

## 13. 具体的なファイル変更案

### Windows MVP

- `apps/windows/AkapenWin32/`を製品経路として新設。
- `Product/MainWindow.cs`: root HWNDとchild layout。
