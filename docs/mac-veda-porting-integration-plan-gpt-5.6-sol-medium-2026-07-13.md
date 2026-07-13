     908 /tmp/akapen-sol-mac-veda-port-plan.md
# Akapen Mac版・VEDA移植／統合計画

> 調査日: 2026-07-13  
> 注記: GPT-5.6-Sol（reasoning medium）が既存ロードマップ、詳細設計、仕様、Mac実装、Node binding、共通契約をread-onlyで照合した計画を、オーケストレーターが成果物として登録した。
> 優先順位: **Windows MVP成立 → Mac段階移植・製品化 → VEDA段階統合**

## 0. 結論

AkapenのMac版は、SwiftUI/AppKitシェル、NSEvent入力、CPU表示、wgpu/Metal表示、設定、3点セット保存、連番移動まで既に存在する。ただし、製品としての文書状態機械、保存失敗・終了時のデータ保護、正確なdirty判定、保存済みVectorDoc再読込、配布・署名・実機試験が不足している。

VEDA側は、本リポジトリ内には実ソースがなく、仕様書に記録された既存資産だけを確認できた。`SUBMISSION_ANNOTATED`、`file`・`flatFile`・`vectorFile`、`timecode`、Electron IPC、Dropbox同期、承認フローが既存資産である。一方、`bindings/node` は4関数だけのCI probeであり、製品API、prebuild、VEDA実台帳試験、再輸入経路はいずれも未実装である。

移植の成否はUIを揃えることではなく、次を一つの共通契約として先に固定できるかで決まる。

```text
NormalizedPointerSample
        ↓
Rust Engine / History / View / Keymap / Palm
        ↓
ExportSnapshot
        ↓
transactional 3-file set
  ├─ transparent strokes PNG
  ├─ flat composite PNG
  └─ versioned VectorDoc JSON
        ↓
Windows / Mac / Node consumers
```

Windows MVP中は、この境界の契約・golden vectors・三面consumer testだけを進める。Macの完全製品化とVEDA実統合はWindows MVPのGate W通過後に着手する。

---

## 1. 現在地

### 1.1 共通Rust資産

| 領域 | 実装済み | 主なファイル | 未完了 |
|---|---|---|---|
| Engine | Pen/Eraser、undo/redo、pressure、CPU bake、`load_vector` | `crates/akapen-core/src/engine.rs` | history state ID、保存snapshot、入力batch、Cancel phase |
| VectorDoc | `veda-annot-1`、必須`points[].p`、stroke単位`timecode` | `crates/akapen-core/src/vector.rs` | schema厳密検証、minor revision、pressure metadata、document metadata、制限値検証 |
| Export | strokes PNG、flat PNG、VectorDoc | `crates/akapen-core/src/export.rs` | 再現メタ、標準化されたmanifest |
| 座標・keymap・palm | 純Rust実装と単体テスト | `coord.rs`、`keymap.rs`、`palm.rs` | 共有外部fixture化、consumer別contract test |
| I/O | PNG/JPEG/WebP/BMP decode、命名、衝突回避、自然順・連番 | `crates/akapen-io` | 3ファイルtransaction、journal recovery、パス安全性の統一API |
| GPU | Metal/DX12、baked＋wet ink、CPU oracle試験 | `crates/akapen-render` | opacity<1パリティ、Mac実画面証拠、長時間・4K試験 |
| C ABI | opaque handle、入力、保存、GPU、keymap、palm | `crates/akapen-ffi/src/lib.rs`、`include/akapen.h` | ABI version、構造体size、統一status/error、load-vector、history、batch入力、transaction save |
| Node | napi-rsロード・4関数smoke | `bindings/node` | 製品API全体、非同期I/O、prebuild、VEDA統合 |

### 1.2 Mac版

既存資産:

- `AkapenApp.swift`: SwiftUIエントリ、Open、Settings
- `AppState.swift`: engine、ファイル、ツール、保存、連番、dirty相当
- `CanvasView.swift`: AppKit `NSView`、NSEvent、座標変換、keymap、palm、CPU/Metal表示
- `AkapenEngine.swift`: C ABIのSwiftラッパ
- `SettingsView.swift`: 出力先・suffix設定
- `akapen-harness/main.swift`: Swift→C ABI→Rust→3点セットのheadless証拠
- `Package.swift`: Rust static library、Metal/QuartzCoreへのリンク

確認できた機能:

- PNG/JPEG/WebP/BMPのOpen
- マウスおよび`.tabletPoint`の`NSEvent.pressure`
- Force Touchをペン筆圧として扱わない分類
- Pen/Eraser、色、サイズ、pressure curve
- undo/redo
- zoom/pan/rotate
- 共通`akapen_resolve_key`
- 共通`akapen_palm_route`
- CPU compositeとwgpu/Metal表示
- `_review/`への3点セット保存
- suffix設定、衝突回避
- 自然順のPrev/Next
- headless harness

未完了・問題点:

1. `AppState.open(url:)`はdirty文書を保存せず置換する。
2. Window close時の保存状態機械がない。
3. `hasUnsavedStrokes`はpointer Upでtrue、saveでfalseにするだけで、保存後Undo、全Undo、Redo、保存状態への復帰を正しく表せない。
4. Prev/NextはRustの末尾数字トークン優先ロジックを使わず、自然順だけである。
5. `AkapenEngine.swift`にVectorDoc読込APIがなく、保存済み注釈を再編集できない。
6. C ABIの保存は3ファイルを直接順番に書き、途中失敗で部分セットが残り得る。
7. pressure fallbackの値は保存されるが、実測かfallbackかをVectorDocから判別できない。
8. NSEvent coalesced samples、pointer ID、tilt、Cancelが共通入力へ届かない。
9. `apps/mac/README.md`と`CanvasView.swift`冒頭はMetal未実装と記すが、実装は既に存在する。
10. Swift単体テストtargetがなく、主な証拠はRustテストとheadless harnessに限られる。
11. `.app` bundle、Release用Rust library、署名、notarization、DMG、crash log、clean-user試験がない。
12. 実WACOM、Retina、複数ディスプレイ、sleep/wake、GPU fallbackの実画面試験が未完了。

### 1.3 VEDA

仕様から確認できる既存資産:

- `renderer/js/annotate.js`: 現行注釈UI、描画、save
- `lib/annotate-geometry.js`
- `lib/annotate-keys.js`
- `lib/annotate-view.js`
- `lib/annotate-target.js`
- `lib/state.js` reducer
- `SUBMISSION_ANNOTATED`
- `file`: 透過ストロークPNG
- `flatFile`: flat合成PNG
- `vectorFile`: VectorDoc JSON
- 動画注釈の`timecode`
- Electron IPC、Dropbox同期、承認・台帳フロー
- B23のflat表示経路
- ffmpeg-static利用実績

未完了:

- VEDAリポジトリ実装との現行shape照合
- `@akapen/core-node`製品API
- mac universal / win x64 prebuild
- Electron versionとN-API compatibility確認
- `SUBMISSION_ANNOTATED`実payload fixture
- IPC channel、保存先、URL/path変換のcontract test
- `vectorFile`再読込と編集再開
- 壊れた／旧VectorDocのfallback
- annotate.jsとRustコアのshadow比較
- lib/annotate-*二重管理解消
- 実台帳・Dropbox・B23表示のE2E試験

本リポジトリにはVEDA本体がないため、これらは「仕様上の現在地」であり、実統合開始時にVEDAリポジトリをread-only監査して確定する必要がある。

---

## 2. 移植方針と非目標

### 方針

- Windows MVPの製品化を最優先する。
- Windowsで確定した保存状態機械、C ABI v1、JSON、3点セットtransactionをMacとNodeへ写像する。
- OSシェルはイベント・surface・ダイアログ・設定だけを担当する。
- VEDA固有の台帳、IPC、Dropbox、承認状態をRust coreへ入れない。
- CPU exportを永続成果物の正本とし、GPUは表示用とする。
- 同じgolden inputからWindows、Mac、Nodeが同じVectorDocとCPU exportを生成する。
- 既存APIは削除せず、versioned v1 APIを追加して段階移行する。
- Mac/VEDAの実装開始前にも、Swift/.NET/Nodeのbuild/load smokeをWindows MVPのmerge gateとして維持する。

### 非目標

Windows MVP成立前には、次を行わない。

- MacのUI全面再設計
- Mac App Store対応
- VEDA annotate.jsの置換
- npm公開
- WASMを主描画経路にすること
- Text・PSD・HEIC・動画対応
- opacity<1の公開
- Wintabや全機材認定を理由とするWindows MVP停止
- Rust coreへのElectron、SwiftUI、AppKit、Dropbox概念の導入

---

## 3. 共通契約の設計

### 3.1 Rust core

次の型を共通正本にする。

```rust
pub struct PointerSampleV1 {
    pub pointer_id: u32,
    pub kind: PointerKind,
    pub phase: Phase,          // Down | Move | Up | Cancel
    pub flags: SampleFlags,
    pub x: f64,
    pub y: f64,
    pub pressure: f32,
    pub pressure_source: PressureSource,
    pub tilt_x: f32,
    pub tilt_y: f32,
    pub timestamp_ns: u64,
}

pub struct HistoryStateV1 {
    pub content_state_id: u64,
    pub committed_stroke_count: u32,
    pub has_in_progress_stroke: bool,
    pub can_undo: bool,
    pub can_redo: bool,
}

pub struct ExportSnapshotV1 {
    pub content_state_id: u64,
    pub strokes_png: Vec<u8>,
    pub flat_png: Vec<u8>,
    pub vector: VectorDoc,
}
```

`content_state_id`の規則:

- stroke commitで新ID
- undoで過去ノードのID
- redoで元ノードのID
- undo後の描画は新ID
- view、選択ツール、色変更では変化しない
- dirtyは`current != savedContentStateId || has_in_progress_stroke`

### 3.2 C ABI v1

既存関数を互換層として残し、次を追加する。

```c
uint32_t akapen_abi_version(void);
uint64_t akapen_capabilities(void);

AkapenStatus akapen_engine_push_samples_v1(
    AkapenEngine *,
    const AkapenPointerSampleV1 *,
    size_t count,
    AkapenMutationResultV1 *,
    AkapenErrorSinkV1 *);

AkapenStatus akapen_engine_history_state_v1(
    AkapenEngine *,
    AkapenHistoryStateV1 *,
    AkapenErrorSinkV1 *);

AkapenStatus akapen_engine_load_vector_json_v1(
    AkapenEngine *,
    const uint8_t *json,
    size_t json_len,
    AkapenLoadResultV1 *,
    AkapenErrorSinkV1 *);

AkapenStatus akapen_engine_export_snapshot_v1(
    AkapenEngine *,
    AkapenExportBuffersV1 *,
    AkapenErrorSinkV1 *);

AkapenStatus akapen_save_export_transaction_v1(
    const AkapenSaveRequestV1 *,
    AkapenSaveResultV1 *,
    AkapenErrorSinkV1 *);
```

規律:

- 全構造体先頭に`struct_size`
- ABI major不一致はロード拒否
- minor追加は末尾フィールド、新関数、capability bitだけ
- enum値の再利用禁止
- Rust panicは`catch_unwind`で`AKAPEN_E_PANIC`
- engineは単一スレッド所有
- messageは診断専用、UI分岐はstatus code
- Swift/Node/.NETでhandleをfinalizer/deinit/SafeHandleから1回だけ解放
- `sizeof/offsetof`をC、Swift、C#、Nodeで検査

### 3.3 3点セットtransaction

`akapen-io`へ保存処理を集約する。

```text
ExportSnapshot
  → collision-free targetを予約
  → 同一filesystemの.akapen-txn-UUIDへ3ファイル生成
  → PNG decode・寸法検査
  → JSON再parse・schema検査
  → flush
  → journal作成
  → no-replaceで最終名へ確定
  → 最終3ファイル再検査
  → journal/stage削除
```

成功条件は3ファイルがすべて最終名で検証済みであること。rollback失敗時は`PARTIAL_COMMIT`とjournalを残し、次回起動時にrecoverする。

---

## 4. VectorDocと後方互換

### 4.1 v1互換形

既存必須形を変更しない。

```json
{
  "schema": "veda-annot-1",
  "schema_revision": 1,
  "required_features": ["per_point_pressure"],
  "natural_w": 1920,
  "natural_h": 1080,
  "strokes": [
    {
      "kind": "pen",
      "points": [{"x": 10, "y": 20, "p": 0.4}],
      "size": 6,
      "color": "#ff0000",
      "erase": false,
      "opacity": 1,
      "smoothing": "off",
      "timecode": null,
      "pressure_meta": {
        "source": "nsevent_tablet",
        "is_fallback": false,
        "curve": "normal",
        "min_width": 1.8,
        "max_width": 6,
        "opacity_from_pressure": false
      }
    }
  ],
  "document_meta": {
    "producer": "Akapen",
    "producer_version": "0.1.0"
  }
}
```

### 4.2 バージョニング規則

- `schema`はmajor互換ID。破壊変更時だけ`veda-annot-2`へ進める。
- `schema_revision`は同一major内の加算的変更。
- v1 readerは未知フィールドを無視する。
- 新フィールドは原則optional/default付き。
- 新しい意味論が無視不能なら`required_features`へ追加する。
- readerが未知のrequired featureを見た場合、編集読込を拒否し、flat/strokes表示へfallbackする。
- `points[].p`、natural size、strokeの基本字段はv1で必須のまま。
- schema文字列の不一致、NaN/Infinity、負値、異常寸法、過大点数、深すぎるJSONを拒否する。
- `Engine::load_vector`はschemaとnatural sizeを検証し、未知`kind`を無条件Penへ落とさない。unsupported toolとして警告または拒否する。
- JSON Schemaを`schemas/vector-doc-v1.schema.json`へ置き、Rust serdeだけに互換判定を依存させない。

---

## 5. Mac SwiftUI/AppKit adapter

目標構造:

```text
apps/mac/Sources/
├─ AkapenApp/
│  ├─ Application/
│  │  ├─ MacDocumentSession.swift
│  │  ├─ SaveCoordinator.swift
│  │  └─ NavigationCoordinator.swift
│  ├─ Adapters/
│  │  ├─ MacPointerAdapter.swift
│  │  ├─ MacSurfaceAdapter.swift
│  │  ├─ MacImageDecoder.swift
│  │  ├─ MacFileDialogAdapter.swift
│  │  └─ MacKeyEventAdapter.swift
│  └─ Views/...
└─ AkapenKit/
   ├─ AkapenEngine.swift
   └─ AkapenTypes.swift
```

### 入力

- `mouseDown/Dragged/Up`とtablet eventを`PointerSampleV1`へ変換
- `event.coalescedMouseEvents`をまとめて`push_samples_v1`
- `pointer_id`をdevice/tablet point lifecycle単位で維持
- `.tabletPoint`のみ`PressureSource::NseventTablet`
- mouse、trackpadは`MouseFixed`、pressure=1.0、`is_fallback=true`
- tiltを取得可能ならflags付きで渡す
- `mouseExited`、capture消失、window resign、device cancelを`Cancel`へ写像
- bounds外Downは開始しないが、開始済みstrokeのMove/Upは継続
- direct touchはpalm gateへ送り、Drawには渡さない
- deliberate touch pan/pinchはview adapter側で消費

### 描画

- `MacSurfaceAdapter`が`MetalHostView`とCAMetalLayer lifetimeを管理
- attach/resize/frame/detachはMainActor上
- display linkでdirty時だけpresentし、常時timerを避ける
- surface attach失敗時はCPU compositeへfallback
- Retina scale、display移動、sleep/wake、window occlusionを処理
- GPU/CPUの座標・回転・色をgolden screenshotで比較
- opacity=1をMac製品版の公開制約とする

### 画像I/O

- MVP形式は共通`akapen-io::decode_rgba`を正本にする。
- 将来HEIC/TIFFは`MacImageDecoder`でImageIO→RGBAへ変換する。
- EXIF orientation、ICC/color space、alpha、巨大寸法をadapter境界で正規化する。
- 元画像はread-onlyで開き、出力先との同一パスをtransaction層で拒否する。

### 保存と文書状態

`MacDocumentSession`はWindowsと同じ状態遷移を持つ。

```text
Empty | Loading | Ready | Saving | Closing
```

Open、Prev、Next、Closeはdirtyなら`SaveCoordinator`を経由し、成功後だけ破壊的遷移する。失敗時はengine、history、current URL、view、dirtyを保持する。

### keymap

- `NSEvent`からcharacter、physical key、Command/Shift/Option、IME状態を抽出
- Space holdだけshell-local
- Open、Save、SettingsはOS command
- その他は`akapen_resolve_key`
- keyCode 24のJIS `^`／US `=`誤爆防止をconsumer testへ固定
- field editorやmarked text中はNone

### undo/redo

Swift側boolではなく`HistoryStateV1`を参照する。メニューのenable状態、dirty marker、保存済み状態へのUndo/Redo復帰を同じIDで判定する。

### pressure fallback

ステータス表示だけでなくVectorDocへ次を保存する。

- `source=nsevent_tablet | mouse_fixed`
- `is_fallback`
- raw `p`
- 描画時curve/min/max
- 診断ログにはdevice種別と分散を保存するが、生の長時間入力列は既定では保存しない。

---

## 6. VEDA Electron/Node adapter

### 6.1 製品Node API

`bindings/node`のCI probeは残し、製品面を別モジュールとして追加する。

```ts
export class AkapenDocument {
  static openImage(path: string): Promise<AkapenDocument>;
  static create(width: number, height: number): AkapenDocument;

  getInfo(): DocumentInfo;
  pushSamples(samples: PointerSample[]): MutationResult;
  setTool(style: ToolStyle): void;
  undo(): HistoryState;
  redo(): HistoryState;
  loadVector(json: Uint8Array): LoadResult;

  renderFrame(view: ViewTransform): FrameDelta;
  exportSnapshot(): ExportSnapshot;
  saveTransaction(request: SaveRequest): Promise<SaveResult>;
  close(): void;
}
```

`AkapenDocument`はnapi object finalizerでhandleを解放する。ファイルdecode、PNG encode、transaction saveは`AsyncTask`へ送り、Electron renderer threadを止めない。

### 6.2 Electron境界

```text
renderer UI
  → contextBridge API
  → preload内 @akapen/core-node
  → Rust core
  → main IPC（Open/Save/台帳更新のみ）
```

高頻度pointer eventをElectron main IPCへ送らない。

- rendererでcoalesced PointerEventを収集
- requestAnimationFrameごとにbatch化
- preload内native addonへ一括送信
- rendererは返されたtyped-array/FrameDeltaをCanvas/WebGLへ描画
- main IPCはファイル選択、transaction save、`SUBMISSION_ANNOTATED`発行だけ
- `contextIsolation=true`
- `nodeIntegration=false`
- rendererへ任意filesystem APIを公開しない

初期統合ではVEDAのCanvas presentationを維持し、Rustをauthoritative stroke/history/exportにする。性能証拠が得られるまで、Electron内にMetal/DX12 surfaceを直接埋め込まない。

### 6.3 `SUBMISSION_ANNOTATED`

保存成功後だけ、VEDA adapterが次を組み立てる。

```ts
{
  type: "SUBMISSION_ANNOTATED",
  submissionId,
  file: saveResult.strokesPngPath,
  flatFile: saveResult.flatPngPath,
  vectorFile: saveResult.vectorJsonPath,
  timecode: saveResult.timecode ?? null
}
```

規則:

- `file`、`flatFile`、`vectorFile`の意味を入れ替えない
- transaction成功前にeventを発行しない
- Dropbox同期完了を保存成功と混同しない
- ledger eventの再送に備え、transaction IDまたはannotation IDで冪等化する
- pathはVEDA既存のcanonical path変換を通す
- flat表示はB23既存経路を維持する

### 6.4 再輸入

1. ledgerから`vectorFile`を取得
2. path traversalと許可rootを検証
3. size上限付きでJSONを読む
4. schema/required featureを検証
5. `loadVector`でcoreへ復元
6. original imageとnatural sizeを照合
7. 編集を再開
8. 再保存は新しい3点セットとして確定し、旧成果物を上書きしない

fallback:

- `vectorFile`欠落・破損: `file`をoverlay表示、編集不可
- `file`欠落: `flatFile`を表示
- 未知schema: flat表示＋「旧／新バージョンで編集不可」
- 一部成果物:正常な`SUBMISSION_ANNOTATED`として扱わず診断対象

---

## 7. Golden vectors・consumer contract tests

### 共通fixtures

`testdata/contracts/`へ次を置く。

- `pointer/basic-pressure.json`
- `pointer/fallback-pressure.json`

