    1025 /tmp/akapen-sol-detailed-design.md
# Akapen Windows MVP 詳細設計・実装分解

> 調査日: 2026-07-13  
> 正本: [Windows MVPロードマップ](/Users/yosshi/Documents/akapen/docs/windows-mvp-roadmap-gpt-5.6-sol-medium-2026-07-13.md)  
> 対象: ロードマップ Phase 0からWindows MVP成立まで  
> 状態: read-only設計。ファイル変更なし  
>
> 注記: GPT-5.6-Sol（reasoning medium）がロードマップ、仕様、実装をread-onlyで照合した詳細設計を、オーケストレーターが成果物として登録した。

## 0. 結論

最初の実装は、新機能追加ではなく次の縦切りである。

```text
Akapen.exe
  └─ 通常起動専用のraw Win32製品シェル
       └─ Open → Draw → Save → Next → Close

Akapen.Windows.Smoke.exe
  ├─ headless export
  ├─ HWND/DX12 present smoke
  └─ scripted input

Rust
  ├─ akapen-core: 描画・履歴・座標・keymap・palm・VectorDoc
  ├─ akapen-io: decode・連番・命名・トランザクション保存
  ├─ akapen-render: GPU表示
  └─ akapen-ffi: versioned C ABI
```

現行 [Program.cs](/Users/yosshi/Documents/akapen/apps/windows-probe/AkapenProbe/Program.cs:1) は、製品、headless、presentation smoke、WndProc、P/Invoke、保存、入力、連番を1,083行に混在させている。これを直接拡張せず、既存Probeを比較対象として残しながら新しい製品経路へ段階的に抽出する。

最優先の安全条件は次の3点である。

1. 保存失敗時に現在のengine、表示画像、未保存ストロークを失わない。
2. Open、Next、Closeは保存成功後にだけ破壊的遷移を行う。
3. 3点セットは「全部成功」または「失敗」となり、部分成果物を正常保存として扱わない。

---

## 1. 設計原則と採用・不採用案

### 1.1 採用する原則

- Windows MVPはraw HWNDとself-contained .NET 8 hostを正式経路とする。
- `Akapen.exe`の引数なし起動は、空の製品ウィンドウを表示する。
- smoke/headlessは別プロセス、別プロジェクト、別実行ファイルにする。
- 製品シェルはOSイベントとUIを担当し、描画意味論、座標計算、keymap、palm、連番を複製しない。
- C ABIは既存関数を直ちに削除せず、versionedなv1 APIを追加してconsumerを段階移行する。
- dirtyは単純な「一度描いた」boolではなく、coreの履歴状態IDと最後に保存した状態IDの差で判定する。
- 入力はOSごとのイベントから共通の正規化サンプルへ変換する。
- JSONは既存`veda-annot-1`の必須フィールドを維持し、新情報は省略可能フィールドとして追加する。
- ファイル保存はRust側へ集約し、Windows、Mac、Nodeで同一の命名・衝突回避・検証・rollbackを使う。
- GPU表示とCPU exportを分離し、保存結果の正本は引き続きCPU exportとする。

### 1.2 不採用案

| 不採用案 | 理由 |
|---|---|
| 現行`Program.cs`をそのまま製品化 | 通常起動、smoke、入力、保存状態が密結合したままになる |
| WinUI 3を再びMVP経路にする | 対象機でWindows App Runtime導入後も起動障害を確認済み |
| 製品exe内に隠しsmokeフラグを残す | 配布物に開発経路が混在し、通常起動の回帰を防げない |
| keymap、palm、連番をC#で再実装 | Mac、Node、VEDAとの意味論ドリフトを固定化する |
| すべてをC ABIで一度に再設計 | consumerを同時破壊し、Phase 0が巨大化する |
| JSONを入力・描画中のIPCに使う | 高頻度入力で割当・parseコストが発生する |
| 「3回renameしたので完全原子的」と表現 | 3ファイル全体を単一filesystem操作で原子化することはできない |
| Wintabを最初に実装 | WM_POINTERとfallbackでMVPを成立させ、実機不足が確認された場合に追加する |
| pointer prediction等を先行 | 製品経路の測定値がまだない |

---

## 2. 現行コードから目標構造への移行

### 2.1 移行方式

既存Probeを直接移動・削除せず、strangler方式を採用する。

1. 現行挙動を契約テストとfixtureで固定する。
2. 新しい製品プロジェクトとsmokeプロジェクトを追加する。
3. `Program.cs`から製品に必要なコードを責務単位で抽出する。
4. 新旧双方でheadless成果物とHWND presentを比較する。
5. CIと配布zipを新製品経路へ切り替える。
6. Gate A〜D通過後に`apps/windows-probe`をdeprecated化する。
7. Probe削除はWindows MVP後の別タスクとする。

これにより、現在のdirty worktreeと実証済みSession 1経路を壊さず移行できる。

### 2.2 目標ディレクトリ

```text
apps/windows/
├─ README.md
├─ AkapenWin32/
│  ├─ AkapenWin32.csproj
│  ├─ Program.cs
│  ├─ Product/
│  │  ├─ AkapenApplication.cs
│  │  ├─ Win32Host.cs
│  │  ├─ MainWindow.cs
│  │  ├─ CommandRouter.cs
│  │  └─ StatusPresenter.cs
│  ├─ Application/
│  │  ├─ DocumentSession.cs
│  │  ├─ DocumentState.cs
│  │  ├─ SaveCoordinator.cs
│  │  ├─ NavigationCoordinator.cs
│  │  └─ ViewState.cs
│  └─ Adapters/
│     ├─ Native/AkapenNative.cs
│     ├─ Input/Win32PointerAdapter.cs
│     ├─ Dialogs/Win32FileDialog.cs
│     └─ Diagnostics/WindowsDiagnostics.cs
├─ AkapenWin32.Tests/
│  ├─ DocumentSessionTests.cs
│  ├─ SaveCoordinatorTests.cs
│  └─ InputAdapterTests.cs
├─ AkapenWindowsSmoke/
│  ├─ AkapenWindowsSmoke.csproj
│  ├─ Program.cs
│  ├─ HeadlessExportSmoke.cs
│  └─ HwndPresentSmoke.cs
└─ legacy/
   └─ README.md
```

既存WinUIソースは最初のPRでは物理移動しない。`apps/windows/AkapenApp`をREADMEとCI上でlegacyと明示し、作業ツリーが安定した後に`legacy/winui`へ移動する。

---

## 3. raw Win32製品シェルの責務と状態機械

### 3.1 モジュール責務

| モジュール | 責務 |
|---|---|
| `Program` | 引数解析、単一インスタンス開始、終了コード |
| `AkapenApplication` | 製品ライフサイクル、初期Open要求 |
| `Win32Host` | window class登録、message loop、DPI初期化 |
| `MainWindow` | HWND、メニュー、ツールバー、ステータスバー |
| `CommandRouter` | UI commandを`DocumentSession`のintentへ変換 |
| `DocumentSession` | 文書状態、dirty、Open/Save/Next/Close遷移 |
| `SaveCoordinator` | 保存要求、成功・失敗の統一処理 |
| `NavigationCoordinator` | Rust連番APIによる前後候補取得 |
| `ViewState` | zoom/pan/rotationとfit/actual-size |
| `Win32PointerAdapter` | WM_POINTER/mouse/touchの分類・正規化 |
| `AkapenNative` | generated P/Invokeと`SafeHandle` |
| `StatusPresenter` | ユーザー向け状態・pressure・エラー表示 |

WndProcは次に限定する。

```text
Win32 message
  → OS情報を読み出す
  → typed event/commandへ変換
  → Application層へdispatch
  → handled/resultを返す
```

WndProc内でファイルを書いたり、engineを置換したり、dirtyを直接変更してはならない。

### 3.2 状態型

```csharp
enum SessionPhase
{
    Empty,
    Loading,
    Ready,
    Saving,
    Closing
}

sealed record DocumentState(
    SessionPhase Phase,
    string? SourcePath,
    AkapenEngineHandle? Engine,
    ulong ContentStateId,
    ulong SavedStateId,
    bool HasInProgressStroke,
    UserFacingError? LastError)
{
    public bool IsDirty =>
        HasInProgressStroke || ContentStateId != SavedStateId;
}
```

`LastError`は文書状態を置換しない。保存失敗は「Error画面」へ移動するのではなく、同じ文書を保持した`Ready + LastError`になる。

### 3.3 遷移表

| 現在 | 操作 | 成功 | 失敗 |
|---|---|---|---|
| Empty | 通常起動 | Emptyの製品UI | 起動エラー表示 |
| Empty | Open(path) | Ready/Clean | Empty + open error |
| Ready/Clean | Draw commit | Ready/Dirty | 現状態維持 |
| Ready/Dirty | Save | Ready/Clean | Ready/Dirty + save error |
| Ready/Clean | Next/Prev | 次画像をLoading | Ready + navigation error |
| Ready/Dirty | Next/Prev | Save成功後のみLoading | Ready/Dirty、移動禁止 |
| Ready/Clean | Open(path) | 新画像へLoading | 現画像維持 |
| Ready/Dirty | Open(path) | Save成功後のみLoading | 現画像・dirty維持 |
| Ready/Clean | Close | 終了 | — |
| Ready/Dirty | Close | Save成功後に終了 | 終了を中止しRetry/Cancel/明示Discard |
| Loading | 任意操作 | 入力を抑止 | 現文書へ復帰 |
| Saving | Open/Next/Close | intentを1件だけ保持 | 二重保存しない |

### 3.4 intent付き保存

```csharp
abstract record AfterSaveIntent
{
    public sealed record Stay : AfterSaveIntent;
    public sealed record Open(string Path) : AfterSaveIntent;
    public sealed record Step(SequenceDirection Direction) : AfterSaveIntent;
    public sealed record Close : AfterSaveIntent;
}
```

`SaveAsync(intent)`の規則:

1. 非dirtyなら保存せずintentを実行する。
2. dirtyなら現在の`ContentStateId`をsnapshotする。
3. Rustのトランザクション保存を呼ぶ。
4. 成功時だけ`SavedStateId = snapshotStateId`とする。
5. 保存中に新入力が発生し得る構成では、現在IDがsnapshotと違えばdirtyを維持する。
6. intentは保存成功後だけ実行する。
7. 失敗時はengine、source path、view、historyを一切置換しない。

MVPでは保存中の描画入力を短時間抑止してよい。後にbackground保存へ変えても上記ID比較で安全性を維持できる。

---

## 4. smoke/headless経路の分離

### 4.1 実行ファイル契約

`Akapen.exe`:

- 引数なし: Empty状態で製品ウィンドウを表示
- 画像パス1件: 同じ製品ウィンドウを開き、その画像をOpen
- 未知の`--`オプション: エラー表示して非0終了
- scripted input、headless export、フレーム数指定を持たない

`Akapen.Windows.Smoke.exe`:

```text
headless-export --out <dir> [--image <path>]
hwnd-present --frames 300 --resizes 3
scripted-input --out <dir>
dll-load
package-manifest --dir <publish-dir>
```

### 4.2 分離条件

- productとsmokeで`Main`を共有しない。
- smokeは製品の`DocumentSession`を直接操作しない。
- 共用可能なのはgenerated P/Invoke、SafeHandle、fixture、低レベルadapterのみ。
- headless smokeはHWND、message pump、GPU attachを呼ばない。
- HWND smokeはユーザーデータや通常の`_review`へ保存しない。
- Session 1製品受入は、smokeフラグではなく実際の`Akapen.exe`を通常操作して行う。

---

## 5. Rust core・C ABI・.NET P/Invoke安定契約

現行C ABIはopaque handleを持つが、ABI version、構造体size、統一エラー、履歴snapshotがない。[akapen.h](/Users/yosshi/Documents/akapen/crates/akapen-ffi/include/akapen.h:1) の既存APIは互換層として残し、次を加える。

### 5.1 ABI version

```c
#define AKAPEN_ABI_VERSION_1_0 0x00010000u

uint32_t akapen_abi_version(void);
uint64_t akapen_capabilities(void);
```

- 上位16bitをmajor、下位16bitをminorとする。
- major不一致はconsumerがロードを拒否する。
- minorは末尾フィールド・新関数・capability追加に限定する。
- enumの既存数値は再利用・変更しない。

### 5.2 opaque handleとownership

```c
typedef struct AkapenEngine AkapenEngine;
```

- create/open成功時のhandleはcaller所有。
- `akapen_engine_destroy(NULL)`は安全。
- 非NULL handleの二重destroyは契約違反。
- engineは単一スレッド所有。作成、入力、render、destroyを同じスレッドから呼ぶ。
- .NETは`SafeAkapenEngineHandle : SafeHandle`で包み、生の`IntPtr`をApplication層へ露出しない。
- Swiftは現在と同様、final classの`deinit`で1回解放する。
- Nodeはnapi objectのfinalizerで解放する。
- WASMではRust/wasm-bindgenの所有権に任せ、C handleを模倣しない。

### 5.3 statusとエラー

```c
typedef int32_t AkapenStatus;

enum {
    AKAPEN_OK = 0,
    AKAPEN_E_INVALID_ARGUMENT = 1,
    AKAPEN_E_INVALID_HANDLE = 2,
    AKAPEN_E_UNSUPPORTED = 3,
    AKAPEN_E_DECODE = 10,
    AKAPEN_E_IO = 11,
    AKAPEN_E_CONFLICT = 12,
    AKAPEN_E_PARTIAL_COMMIT = 13,
    AKAPEN_E_GPU = 20,
    AKAPEN_E_ABI_MISMATCH = 30,
    AKAPEN_E_PANIC = 99
};

typedef struct {
    uint32_t struct_size;
    int32_t code;
    int32_t os_code;
    char *message;
    size_t message_capacity;
    size_t message_required;
} AkapenErrorSinkV1;
```

すべての新しいfallible APIは`AkapenStatus`を返す。エラー文言は診断用であり、UI分岐はcodeで行う。Rustのextern境界は`catch_unwind`で包み、panicをC境界の外へunwindさせない。

### 5.4 正規化入力

```c
typedef struct {
    uint32_t struct_size;
    uint32_t pointer_id;
    uint32_t kind;          /* Pen, Touch, Mouse */
    uint32_t phase;         /* Down, Move, Up, Cancel */
    uint32_t flags;         /* PRESSURE_VALID, TILT_VALID, COALESCED... */
    uint32_t pressure_source;
    double x;
    double y;
    float pressure;
    float tilt_x;
    float tilt_y;
    uint64_t timestamp_ns;
} AkapenPointerSampleV1;

AkapenStatus akapen_engine_push_samples_v1(
    AkapenEngine *,
    const AkapenPointerSampleV1 *,
    size_t count,
    AkapenMutationResultV1 *,
    AkapenErrorSinkV1 *);
```

高レート入力では1サンプルずつP/Invokeせず、coalesced sampleをbatchで渡せるようにする。

### 5.5 履歴・dirty契約

```c
typedef struct {
    uint32_t struct_size;
    uint64_t content_state_id;
    uint32_t committed_stroke_count;
    uint8_t has_in_progress_stroke;
    uint8_t can_undo;
    uint8_t can_redo;
} AkapenHistoryStateV1;

AkapenStatus akapen_engine_history_state_v1(
    AkapenEngine *, AkapenHistoryStateV1 *, AkapenErrorSinkV1 *);
```

`content_state_id`は履歴ノードを表す。

- commitで新ID
- undoで直前ノードのID
- redoで元ノードのIDへ復帰
- undo後に新規描画した場合は新ID
- styleやviewの変更だけでは変更しない

これにより「保存後Undo」「Undo後Redoで保存状態に戻る」を正しくdirty判定できる。

### 5.6 .NET binding

- 製品は手書きDllImportを使わず、csbindgen生成面を使用する。
- DLL論理名は`akapen_native`へ統一する。生成器にもこの名前を設定する。
- `SafeHandle`、enum、status例外変換は生成物の上の手書きmanaged wrapperに置く。
- P/Invoke構造体には`StructLayout.Sequential`と固定幅型を使う。
- CIでCの`sizeof/offsetof`とC#の`Marshal.SizeOf/OffsetOf`を比較する。
- x64をMVP対象とし、`size_t`は`nuint`へ写像する。

---

## 6. VectorDoc/JSONと3点セット保存

### 6.1 互換スキーマ

現行 [vector.rs](/Users/yosshi/Documents/akapen/crates/akapen-core/src/vector.rs:1) の必須形は維持する。

```json
{
  "schema": "veda-annot-1",
  "natural_w": 1920,
  "natural_h": 1080,
  "strokes": [
    {
      "kind": "pen",
      "points": [{"x": 10.0, "y": 20.0, "p": 0.4}],
      "size": 6.0,
      "color": "#ff0000",
      "erase": false,
      "opacity": 1.0,
      "smoothing": "off",
      "timecode": null,
      "pressure_meta": {
        "source": "wm_pointer",
        "is_fallback": false,
        "curve": "normal",
        "min_width": 1.8,
        "max_width": 6.0,
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

規則:

- `points[].p`は常に必須。
- `pressure_meta`と`document_meta`は省略可能。
- `p`はカーブ適用前の0〜1値。
- fallback時も`p`は格納するが、`is_fallback=true`とsourceを必ず記録する。
- unknown fieldを旧consumerが無視できることをgolden testで確認する。
- NaN、Infinity、負のnatural size、過大な点数はparse時に拒否する。
- schema文字列を変えるのは既存readerが読めない破壊変更時だけとする。

### 6.2 原子的保存の実装

完全な「3ファイル同時rename」は一般のfilesystemでは不可能である。MVPでは、クラッシュ回復付きトランザクションとして次を実装する。

```text
Export snapshot
  → collision-free target予約
  → 同一出力ディレクトリ内のstageへ3ファイル生成
  → PNG/JSON再検証
  → flush
  → commit journal作成
  → no-replaceで3ファイル確定
  → journal更新
  → 完了後stage/journal削除
```

具体条件:

1. stageは出力先と同一filesystem内の`.akapen-txn-<uuid>`。
2. 3ファイルは`create_new`で生成する。
3. PNGはsignature、寸法、decode完了を確認する。
4. JSONは再parseし、schema、natural size、全`points[].p`を検証する。
5. commitは上書き禁止APIを使う。
6. 途中失敗時は、このtransactionが作った確定ファイルだけをrollbackする。
7. rollback失敗時は`AKAPEN_E_PARTIAL_COMMIT`を返し、journalを残す。
8. 次回起動時に未完了journalを検出し、rollbackまたは完了処理を行う。
9. Save成功は3ファイルが最終名で存在し、再検証に通った時だけ返す。
10. 元画像パスと出力3パスの正規化後同一性を検査し、元画像への書き込みを拒否する。

強い単一renameを得る「世代ディレクトリ丸ごとrename」はVEDAの既存3ファイル配置と非互換なので採用しない。

---

## 7. Mac・Node・WASM adapter境界

### 7.1 共通境界

```text
Platform event / file / surface
        ↓ adapter
NormalizedInput / DecodedRgba / RenderSurface / SaveRequest
        ↓
Rust application + core
```

Rust coreへ入れてよいもの:

- normalized pointer sample
- view transform
- decoded RGBA
- tool/style
- history command
- VectorDoc
- export snapshot

入れてはいけないもの:

- HWND、NSEvent、Node Bufferの所有概念
- FileOpenPicker、NSOpenPanel
- VEDA台帳、IPC、Dropbox、承認状態
- WindowsのVK、macのkeyCodeそのもの

### 7.2 Mac

現在の [CanvasView.swift](/Users/yosshi/Documents/akapen/apps/mac/Sources/AkapenApp/CanvasView.swift:276) を次のadapterに整理する。

- `MacPointerAdapter`: NSEvent/coalesced events → normalized sample
- `MacSurfaceAdapter`: NSView/CAMetalLayer lifetime
- `MacFileDialogAdapter`: NSOpenPanel
- `MacDocumentSession`: Windowsと同じ状態遷移をSwiftで写像
- `AkapenEngine.swift`: C ABI marshallingだけ

Mac固有のpressure sourceは`nsevent_tablet`。Force Touchは`mouse_fixed`であり、penとして記録しない。

### 7.3 Node/napi-rs

現在のNode面はCI probeであり製品APIではない。将来は次の単位で公開する。

```ts
class AkapenDocument {

