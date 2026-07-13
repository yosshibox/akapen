# Windows版 Akapen MVP 転換方針・全体ロードマップ

> 調査日: 2026-07-13  
> 対象: Akapenプロジェクト全体  
> 前提: Windows先行、raw Win32製品シェル、Rust共通コア、C ABI、VEDA互換JSON  
> 注記: 指定どおりread-onlyで調査し、ファイルは変更していない。既存の出力候補は [docs/windows-mvp-roadmap-and-orchestration-2026-07-13.md](/Users/yosshi/Documents/akapen/docs/windows-mvp-roadmap-and-orchestration-2026-07-13.md) だが、本文で出力先パスが明示されていないため、本回答を完成版Markdownとする。

## 0. 結論

Akapenは「コア技術の検証段階」から「製品化直前」へ進んでいるが、まだ正式なWindows製品ではない。

すでに成立しているのは、Rustコア、GPU描画、C ABI、3点セット出力、画像デコード、連番ロジック、raw HWND上のDX12提示、self-contained配布、Session 1でのsmokeである。一方、現在のraw Win32実装は、出力名こそ`Akapen.exe`になったものの、内部構造・既定起動・UI・検証モード混在・保存状態遷移の面で依然としてProbeである。

したがって次の主目的は、機能追加ではなく、次の転換である。

```text
AkapenProbeという多目的検証プログラム
        ↓
正式なAkapen.exe製品シェル
        ↓
Open → Draw → Save → Nextを安全に完遂できるWindows MVP
```

過去の「mac M1を完成してからWinUI M2」「WinUI 3を正式Windowsシェルにする」「全機材の筆圧検証までリリースを止める」「Rapture比較を初期ゲートにする」という順序は、現行方針には採用しない。

## 1. 現在地と本当の未完了事項

### 1.1 成立済みの基盤

| 領域 | 現状 |
|---|---|
| Rust core | ストローク、筆圧カーブ、undo/redo、座標変換、平滑化、CPU raster、GPU tessellation、パーム状態機械、共通keymapが存在 |
| GPU | Metal/DX12、確定ストローク焼き込み＋wet ink、HWND present、frame latency 1の経路を実証 |
| C ABI | エンジン、入力、描画、保存、keymap、palm routing、診断APIを公開 |
| JSON | `veda-annot-1`、全点に必須の`points[].p`、timecodeの基礎を実装 |
| I/O | PNG/JPEG/WebP/BMP、衝突回避命名、自然順・末尾数字連番ロジックをRust側に実装 |
| Windows検証 | raw HWND、WM_POINTER、self-contained publish、`akapen_native.dll`分離、Session 1 smoke成功 |
| 他面 | mac SwiftUIシェル、.NET binding生成、Node smoke、ヘッダparity、三面CIの基礎あり |

主要契約は [akapen-core](/Users/yosshi/Documents/akapen/crates/akapen-core/src/lib.rs)、[akapen-ffi](/Users/yosshi/Documents/akapen/crates/akapen-ffi/include/akapen.h)、[akapen-io](/Users/yosshi/Documents/akapen/crates/akapen-io/src/lib.rs)、[akapen-render](/Users/yosshi/Documents/akapen/crates/akapen-render/src/lib.rs) に形成されている。

### 1.2 「実装済み」と「製品として確認済み」を分ける

Windows MVPについて確認済みなのは、主に次の範囲である。

- self-containedな`Akapen.exe`を生成できる
- `akapen_native.dll`をロードできる
- HWNDへDX12で継続presentできる
- scripted mouse入力から3点セットを生成できる
- WM_POINTER筆圧を受けるコードがある
- Session 1で起動継続とinteractive smokeが通る

未確認なのは、実ユーザーによる一連の製品操作である。

- 配布zipを展開し、通常起動する
- UIから画像を開く
- 実ペンまたはマウスで描く
-表示、保存PNG、JSONが一致する
- 保存失敗時に現在の画像と未保存ストロークを保持する
- 正常保存後だけ次フレームへ進む
- 終了時の保存失敗をユーザーが認識できる
- 実液タブでpressureとタッチ抑止を確認する

### 1.3 本当の未完了事項

1. **通常起動が製品起動になっていない**  
   現状は引数なしでscripted smokeが走る。製品の既定起動は空ウィンドウまたはOpen導線でなければならない。

2. **Probeの構造が残っている**  
   [Program.cs](/Users/yosshi/Documents/akapen/apps/windows-probe/AkapenProbe/Program.cs) は1,000行を超え、通常UI、headless export、presentation smoke、入力、保存、ナビゲーション、FFI宣言、WndProcが同居する。ウィンドウクラス名、タイトル、ログにもProbeが残る。

3. **操作が発見できない**  
   raw Win32側には製品用ツールバー、メニュー、状態表示がほぼなく、ショートカットを知らないユーザーはOpen、Save、Undo、Nextへ到達しにくい。

4. **保存失敗時の状態遷移が安全ではない**  
   raw Win32側の`TrySave`は成否を返さず、`OpenImageFile`は保存失敗後も新画像へ置換できる。終了時も失敗を知らせず閉じる。仕様§4.5の「保存成功後にのみ移動」を満たしていない。

5. **3点セット書き込みがトランザクショナルではない**  
   FFIは3ファイルを順次直接書き込むため、途中失敗で部分セットが残り得る。MVPでは一時ファイルへの書き込み、検証、rename、失敗時cleanupを要求すべきである。

6. **Windowsシェルが共通契約を迂回している**  
   ショートカットは`akapen_resolve_key`ではなく独自のVK分岐、パーム処理は`akapen_palm_route`ではなく独自状態、連番はRust `neighbor`ではなくC#側実装を使う。Mac/VEDAとの意味論ドリフト要因である。

7. **CIの正式Windows製品経路が未固定**  
   CIは旧WinUIシェルをビルドする一方、raw Win32のself-contained publish、`akapen_native.dll`実ロード、製品zip内容、headless smokeを正式ゲートにしていない。

8. **JSON契約が仕様の全メタデータを満たしていない**  
   `points[].p`は成立しているが、仕様§5.5が求めるpressure source/fallback、描画時pressure curve、min/max width等の再現メタは未実装である。

9. **実画面・実ペン証拠が不足している**  
   scripted smokeは重要だが、実ペンでの座標、幅、色、回転方向、パーム抑止を保証しない。

10. **ドキュメントの正本が分裂している**  
    [apps/windows/README.md](/Users/yosshi/Documents/akapen/apps/windows/README.md) には、冒頭のlegacy宣言と後段の「WinUIがreal MVP」という古い説明が混在する。README、仕様、日報、実装配置を一つの製品経路へ同期する必要がある。

## 2. Probeが製品化を妨げる本質

問題は名称だけではない。Probeは「複数の技術仮説を一つの実行ファイルで高速に検証する」ための構造であり、製品は「一つのユーザーフローを安全かつ予測可能に完遂する」ための構造である。

Probeを拡張し続けると、次の混同が固定化する。

- 成功ログとユーザー向けエラー表示
- scripted入力と実入力
- headless検証とGUI製品起動
- 開発者用引数と配布用CLI
- WndProcのメッセージ処理とドキュメント状態
- 保存試験とユーザーデータの保存
- 機能が存在することと、製品操作として到達可能なこと

したがって「Probeをリネームする」のではなく、Probeから証明済みコードを抽出し、製品シェルと検証ハーネスを別エントリポイントにする。

## 3. Windows MVPの定義と非目標

### 3.1 MVPの定義

Windows MVPは、作監が次の一連の作業を単体アプリで安全に完遂できる状態とする。

1. `Akapen.exe`をダブルクリックする
2. PNG/JPEG/WebP/BMPまたはフォルダ内画像を開く
3. マウス、または利用可能なWM_POINTER筆圧で赤線を描く
4. Pen/Eraser、Undo/Redo、Pan、Zoom、Rotateを使う
5. `Ctrl+S`または自動保存で、元画像を変更せず3点セットを出力する
6. 保存成功後だけ前後フレームへ移動する
7. 取得不能な筆圧は固定圧へフォールバックし、その状態を診断できる
8. .NETやWindows App Runtimeを別途導入せず実行できる

### 3.2 MVPの非目標

- WinUI 3への復帰
- Wintab完全対応
- 全WACOM機種・Inkオン/オフの完全認定
- 図形、Text、スポイト、透明色、任意色UI
- 不透明度付きGPUストローク
- HEIC、PSD、TIFF、動画
- フィルムストリップ、未処理ジャンプ
- 保存済みJSONの再編集
- Mac正式出荷
- VEDA再輸入
- Raptureを上回る数値保証
- MSIX、Store配布、自動更新

これらはMVP後の段階的品質・機能拡張とする。

## 4. 責務分離

| 層 | 持つ責務 | 持たない責務 |
|---|---|---|
| raw Win32製品シェル | HWND、メニュー/ツールバー、ファイルダイアログ、WM_POINTER取得、状態表示、ユーザー向けエラー、アプリライフサイクル | ストローク意味論、pressureカーブ、JSON生成、画像合成、連番アルゴリズムの複製 |
| 開発用smoke/検証 | scripted入力、headless export、DLLロード、zip検査、Session 0/1診断、GPU構成表示 | ユーザー向け通常起動、ユーザーデータの操作UI |
| Rust core | ストローク、undo/redo、筆圧写像、平滑化、座標/keymap/palm純ロジック、VectorDoc | HWND、Windowsメッセージ、ダイアログ、VEDA業務ルール |
| `akapen-render` | wgpu surface、wet ink、bake、present、GPU診断 | UI状態、保存先、入力デバイス判定 |
| `akapen-io` | デコード、連番、命名、衝突回避、出力ターゲット | UI、Windows固有状態 |
| C ABI | OS・言語非依存の最小契約、所有権、エラーコード、構造体レイアウト | 製品UIの都合を反映したAPI、Windows型 |
| JSON/PNG | VEDA/Mac/Windows間の永続互換契約 | Windows UI状態、台帳、Dropbox、承認フロー |

製品シェルは、手書きP/Invokeの増殖を止め、可能ならcsbindgen生成面を利用する。ただし製品化の途中でABI全体を再設計せず、まず既存契約とのparity testを置く。

## 5. ロードマップ

### Phase 0 — 現実の固定と製品境界の確定

依存: なし。

実施内容:

- 正式ソース配置を`apps/windows/AkapenWin32`等へ分離
- `Akapen.exe`とsmoke実行ファイルを分離
- 旧WinUIを`legacy/experimental`と明記
- ネイティブDLL名を`akapen_native.dll`に固定
- C ABI、JSON v1、出力名、MVP対象ツールを契約表にする
- raw Win32製品publishをCIへ追加

受入条件:

- 通常起動でsmokeが走らない
- 製品タイトル、ファイル名、ユーザー向けREADMEからProbeが消える
- zipに`Akapen.exe`、必要なself-contained runtime、`akapen_native.dll`が揃う
- zip展開後のDLL実ロードsmokeが成功する
- 旧WinUIと正式MVPの説明が矛盾しない

証拠:

- zip manifest
- CIログ
- Session 1起動ログ
- C ABI/header parity結果

### Phase 1 — 製品シェル骨格

依存: Phase 0。

実施内容:

- `Win32Host`、`InputAdapter`、`DocumentSession`、`ViewState`、`SaveCoordinator`へ分割
- Open/Save/Pen/Eraser/Undo/Redo/Fit/Prev/Nextのメニューまたはツールバー
- 現在ファイル、dirty、保存先、pressure状態のステータス表示
- 起動・画像読込・GPU attach失敗のユーザー向けダイアログ
- 通常起動、画像引数起動、関連付け起動を統一

受入条件:

- ショートカットを知らなくても主要操作へ到達できる
- WndProcはメッセージ分類とdispatchが中心になる
- GUI、headless、scripted smokeが別エントリポイントで動く
- Windows Runtimeや別途.NETを要求しない

証拠:

- Session 1スクリーンショット
- UI操作表
- self-contained clean-machine起動記録

### Phase 2 — 保存安全性を中心とした縦切りMVP

依存: Phase 1、C ABI/JSON契約。

実施内容:

- `SaveResult`を返す保存状態機械
- 保存成功時だけOpen/Prev/Nextを許可
- 3ファイルを一時名へ生成し、全成功後に確定
- 失敗時cleanupと部分成果物診断
- 元画像ハッシュまたはmtime/size不変テスト
- Rustの連番ロジックをC ABI経由で利用するか、共有テストベクタでC#実装を固定
- dirty判定をストローク数・履歴状態と整合させる

受入条件:

- 書き込み不能時に現画像と未保存ストロークが残る
- 3点セットの一部だけを成功扱いしない
- 衝突時に既存成果物を上書きしない
- 未描画フレーム移動で空成果物を作らない
- `c001 → c002`を優先し、無ければ自然順へフォールバックする

証拠:

- 読取専用ディレクトリ試験
- 途中書き込み失敗注入試験
- 元画像before/afterハッシュ
- JSON schema、PNG寸法、成果物名の検査

### Phase 3 — 入力・ビュー・Windows操作品質

依存: Phase 1、Phase 2の状態機械。

実施内容:

- WM_POINTERをPen/Touch/Mouseへ正規化
- `akapen_palm_route`を正式利用
- `akapen_resolve_key`を正式利用
- 合成マウスイベント抑止
- 単指パン、Spaceパン、ズーム、フィット、100%、回転
- DPI変更、resize、画像外Down、capture cancel、pointer cancelを処理
- pressure source、実測/固定値、デバイス種別を診断ログへ記録

受入条件:

- Penと合成mouseで二重線が出ない
- ペン接触中のtouchが描画にならない
- ズーム・パン・回転後も表示位置と保存座標が一致する
- JIS/US、IME/テキスト入力時のショートカット誤爆がない
- pressureが取れない場合も描画可能で警告が出る

証拠:

- 座標golden test
- input event trace
- Session 1 mouse/pen/touch操作録画
- JSON `points[].p`の確認

### Phase 4 — Windows MVPリリース候補

依存: Phase 0〜3。

実施内容:

- Release self-contained zip生成
- ライセンス、NOTICE、第三者表記
- clean-machine/clean-user-profile試験
- crash/errorログの保存場所を固定
- MVP手動受入シナリオを実施
- READMEをWindows MVP中心へ更新

受入条件:

- 起動→Open→Draw→Save→Next→Auto-save→CloseをSession 1で完遂
- 元画像不変、3点セット整合、JSON `p`必須
- 取得可能なpressureを利用し、固定圧を診断可能
- 既知制限と未検証機材がリリースノートに明記される
- CI、Windows product smoke、レビューが緑

証拠:

- 受入チェックシート
- 成果物一式
- スクリーンショットまたは録画
- zip SHA-256とmanifest
- 実機環境情報

### Phase 5 — MVP後のWindows品質と§5.6

依存: Windows MVP。

- 利用可能なWACOM、液タブ、板タブ、Surfaceで順次検証
- Windows Inkオン/オフを記録
- 必要性が実証された場合のみWintabを実装
- pressure curve UI、tilt、パームロック時間を調整
- 半透明GPUパリティを修正
- 長時間・4K・多数ストローク回帰を収集

### Phase 6 — 性能計測とRapture対照

依存: 機能安定したWindows MVP。

- 実ウィンドウの起動、画像表示、一筆目、presentを計測
- 4K入力、連番切替、保存を個別計測
- PresentMonとアプリ内timestampを対応付ける
- 同一実機・同一ペンでRaptureと比較
- 入力スレッド、waitable swapchain、prediction等は計測結果に基づき導入

Rapture比較は「基本機能を作る理由」ではなく、「基本機能完成後にどこを最適化するか決める証拠」とする。

### Phase 7 — Mac写像

依存: C ABI/JSON v1とWindows MVP状態遷移の安定。

- Windowsで確定した保存安全性、keymap、palm、診断をSwiftUI/AppKitへ写像
- mac固有なのはNSEvent、ImageIO、ウィンドウ/設定のみ
- Windows固有概念がRust coreへ漏れていないことを検証
- Windows/Macで同じ入力ベクタから同じJSONとCPU exportを生成する

### Phase 8 — CSP互換、広形式、動画

依存: MVP契約。Mac写像とは一部並列可。

- ショートカット全表
- 色交換、透明色、スポイト、不透明度
- 図形、Text、IME
- HEIC/PSD/TIFF
- ffmpegフレーム抽出、timecode、フィルムストリップ
- 保存済みVectorDoc再読込・再編集

### Phase 9 — VEDA再輸入

依存: JSON/C ABI安定、Node bindingの製品化。

- `load_vector`を含む再編集契約を完成
- napi-rsの製品APIとprebuildを整備
- WASMは純ロジック用途に限定
- VEDA実台帳で3点セット＋timecode互換を検証
- VEDA固有のIPC、台帳、Dropbox、承認フローはコアへ入れない
- 旧JS参照実装との二重管理を段階的に解消

## 6. サブエージェントへの委譲方針

### 6.1 小粒タスク

| ID | タスク | 主な変更領域 | 依存 |
|---|---|---|---|
| P0-1 | 製品/Probe/legacy配置と名称の決定 | project、README | なし |
| P0-2 | self-contained zip manifest・DLLロードsmoke | build、CI | P0-1 |
| P0-3 | C ABI/JSON/保存契約表とgolden fixture | crates、testdata | なし |
| P1-1 | Win32メニュー/ツールバー骨格 | 製品シェル | P0-1 |
| P1-2 | DocumentSession状態型の抽出 | 製品シェル | P0-1 |
| P1-3 | InputAdapterの抽出 | 製品シェル | P0-1 |
| P2-1 | 保存失敗状態遷移テスト | shell tests | P1-2 |
| P2-2 | 3点セット原子的確定 | FFI/io | P0-3 |
| P2-3 | 連番契約の共有 | io/FFI/shell | P0-3 |
| P3-1 | 共通keymapへの接続 | shell | P1-1 |
| P3-2 | palm routingとpointer cancel | shell | P1-3 |
| P3-3 | view/座標golden test | core/shell tests | P1-3 |
| P4-1 | Windows製品CI/publish | CI/build | P0-2 |
| P4-2 | Session 1受入スクリプト | verification docs | P1〜3 |
| P4-3 | ライセンス・配布文書 | docs/package | P4-1 |

### 6.2 並列化

契約凍結後、次は並列化できる。

- UI骨格
- 保存失敗テストの準備
- product publish/zip検査
- 実機記録テンプレート
- C ABI/JSON golden fixture

ただし、同じ`Program.cs`を複数担当へ渡してはならない。まずファイル分割を一人が完了し、その後に担当ファイルを分離する。

### 6.3 直列にするもの

1. 製品配置と配布名
2. C ABI/JSON/保存契約
3. shell状態機械
4. 原子的保存
5. Open/Next/Close統合
6. Session 1受入
7. MVP判定

C ABI変更とシェル実装を同時開始しない。契約PRの統合後にconsumer側を動かす。

### 6.4 統合ゲート

各ゲートでオーケストレーターが独立に確認する。

- Gate A: 契約 — ABI、JSON、ファイル命名、ownership
- Gate B: build — Rust、raw Win32、binding、self-contained publish
- Gate C: safety — 保存失敗、元画像不変、部分成果物なし
- Gate D: interaction — Session 1で実操作
- Gate E: release — zip、ライセンス、既知制限
- Gate F: review — Medium以上の未解決指摘なし

## 7. Chapter 5.6を含む品質工程

筆圧はMVP全体を支配する単独フェーズではなく、入力品質の一項目である。

### 必須ゲート

- pressureが取得できれば0〜1でRustへ渡す
- 取得不能時は固定圧で描画を継続する
- 固定圧を無言で扱わない
- JSONには常に`points[].p`を保存する
- 実測値かfallback値かを診断できる
- 少なくとも一つのSession 1実入力経路を記録する

### MVP後の品質マトリクス

- Windows Ink on/off
- WACOM板タブ・液タブ
- Surface
- Wintab
- pressure curve
- パームリジェクション
- tilt
- mac NSEvent

未入手機材は「未検証」と記録する。未検証を「非対応」とも「対応済み」とも表現しない。Wintabは、実利用環境でWM_POINTER不足が確認された場合に実装判断する。

## 8. リスクと判断ゲート

| リスク | 判断ゲート |
|---|---|
| 名前だけAkapenで内部はProbe | 通常起動、UI、ログ、ソース配置の4点が分離されるまで製品化完了としない |
| 保存失敗によるデータ損失 | 失敗注入試験が通るまでNext/Openを受入しない |
| 3点セットの部分生成 | 原子的確定または明示的rollbackが入るまでRC不可 |
| 独自keymap/palm/sequenceのドリフト | 共通契約または共有golden testがない複製を追加しない |
| raw Win32の巨大化 | WndProcが状態・保存を保持し始めたら機能追加を止め、分割する |
| WinUI再検討による停滞 | raw Win32で解決不能な具体的要件が出るまで復帰しない |
| 筆圧機材待ち | 必須ゲートを通し、残セルをMVP後へ送る |
| 早すぎる性能最適化 | 実製品経路の測定値が出るまでprediction等を追加しない |
| ABI拡大 | Mac/Node/VEDAのconsumer試験とバージョニング方針なしに追加しない |
| 半透明GPU乖離 | MVPはopacity=1固定。公開前にパリティ修正を別ゲート化 |
| 古い文書による誤誘導 | 仕様、README、製品配置の矛盾をPhase 0で解消する |

## 9. 最初に実行する5タスク

1. **正式な製品配置と起動モードを確定する**  
   `Akapen.exe`は通常起動、smoke/headlessは別実行ファイルへ移す。旧WinUIはlegacyと明記する。

2. **保存状態機械の失敗テストを先に作る**  
   保存失敗時にOpen/Nextしない、現在のengineとdirtyを保持することを固定する。

3. **3点セットを原子的に確定する**  
   一時ファイル、全成果物検証、rename、失敗cleanupを実装する契約を固める。

4. **raw Win32製品publishをCIの正式Windowsゲートにする**  
   self-contained publish、`akapen_native.dll`実ロード、headless export、zip manifestを検査する。旧WinUI buildは参考レーンへ降格する。

5. **Session 1の最小実操作証拠を採る**  
   通常起動→Open→実入力→Save→Nextを実行し、画面、PNG、JSON、元画像不変、pressure/fallbackログを一組の証拠として保存する。

この5件が完了するまでは、Wintab、図形、Text、HEIC/PSD、動画、VEDA統合、Rapture比較へ進まない。
