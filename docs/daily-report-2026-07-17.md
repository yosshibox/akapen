# 日報 — 2026-07-17

## 今日の完了事項

- Windows出荷版シェル `apps/windows`(raw Win32)をV1.1からV1.1.2まで一気に前進させ、GitHubへ公開した。
- **V1.1「Photoshop UX」**: キーマッププリセット(Photoshop既定 / CLIP STUDIO)、15度回転、矢印キー(←→=前後フレーム、↑↓=ズーム)、Photoshopライクなナビゲーター、設定画面刷新(`app.manifest` によるcomctl32 v6 + PerMonitorV2、Segoe UI、白背景)を実装。バージョン1.0.1→1.1.0。
- **V1.1.1**: 筆圧ON/OFFトグル(既定ON、設定・メニューで切替)、タイトルバー「A」問題の根治(`DefWindowProcW` 明示でANSI/Unicode混在を解消)、exe埋込アイコン表示、バージョン情報ダイアログ、筆圧サブメニュー化、EULA同意画面付きインストーラ、Markdown版マニュアルを追加。1.1.0→1.1.1。
- **V1.1.2**: EULAを平明な日本語へ全面書き直し(`japanese-tech-writing` スキル準拠)、apps再編(旧WinUI検証シェル廃止 → 製品シェルを `apps/windows` へ改名)、HTML版マニュアル同梱(ヘルプ→「Akapen ヘルプ」から起動)、**Windows arm64版を追加**。1.1.1→1.1.2。
- **GitHub公開**: `v1.1.1`(x64)と `v1.1.2`(x64 + arm64)の2リリースを登録。dist の `*.exe` はGit LFS管理。
- **Mac版V1.2着手**: M-mac0/1(キーマッププリセット、矢印、筆圧トグルの縦切り)をコミット(`b5109ae`)。Windows V1.1.1を正本にした写像設計・ロードマップを `docs/mac-v1.1-ui-design-and-roadmap-2026-07-17.md` に記録。

## 検証結果

- macOS: `cargo fmt / clippy / test --workspace`、header-parity、`dotnet build`(AkapenProbe)、UIコントラクトテスト(ナビゲーター幾何・プリセットround-trip込み)すべてPASS。
- 192.168.11.34(Windows実機): `cargo test`(core/ffi)、publish、コントラクトテスト、`--headless-export`、`--interactive-smoke`(schtasksでデスクトップセッション実行、DX12 attach・描画・自動保存)すべてPASS。ソースをv1.1.2で統一、1.1.2インストール済み。
- 192.168.11.18(Surface Go): V1.1.0を `--install-dir D:\Akapen` でインストールし `--interactive-smoke` PASS(C空き2.5GBのためD指定)。
- 実GUIでタイトル「Akapen」表示、1.1.1のFileVersion 1.1.1.0を確認。

## 配布物(本日リリース)

- `dist/AkapenSetup-1.1.1-win-x64.exe`(SHA-256 `83809d39…9842`)
- `dist/AkapenSetup-1.1.2-win-x64.exe`(SHA-256 `73a36e12…2f87`)
- `dist/AkapenSetup-1.1.2-win-arm64.exe`(SHA-256 `d690993f…8c44`、クロスビルド)
- 詳細は [バージョンヒストリ](version-history.md) を参照。

## 残件・申し送り

- **192.168.11.30**: SSH接続拒否(port 22)。現地でOpenSSH Serverを有効化し、ソース配置と1.1.2配備を行う。
- **arm64実機検証**: x64/arm64ともクロスビルドのみ。ARM64実機(Snapdragon等)での起動・描画・筆圧の目視確認が未了。
- **Mac版V1.2 M-mac2〜5**: ドックV1.1化、ナビゲーター移植、設定・筆圧・About、品質・配布(署名/公証dmg + EULA)。
- **未検証(Windows)**: ペン実機(筆圧)でのナビゲーター操作・新設定画面の目視、Photoshop手癖での通し操作。
- **EULA文面**: 発注者チェック待ち(修正時は `EULA-ja.txt` 差し替え → インストーラ再ビルド)。
- **MSI化**: 現状はself-extractingインストーラ。企業配布向けMSI化は将来検討。

## 運用メモ

- 192.168.11.34 の作業コピーは .git 無し。tar over sshで同期する際、macOSのtarが `._*`(AppleDouble)を混ぜてCSCが落ちるため `COPYFILE_DISABLE=1 tar` を使う。
- SSH(Session 0)からはDX12 present不可。GUI検証は `schtasks /create ... /it` + `/run` でログオン中セッションに流すと自走できる。
