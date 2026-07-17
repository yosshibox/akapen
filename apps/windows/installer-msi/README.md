# Akapen Windows MSI インストーラ (WiX)

`apps/windows/installer/` の自己展開 EXE 版に代わる、WiX Toolset v5 製の
per-user MSI インストーラです。

## 成果物

- `bin/Release/Akapen-1.1.2-win-x64.msi`

## 仕様

- インストール先: `%LOCALAPPDATA%\Programs\Akapen` (per-user。管理者権限不要)。
- 配置内容: `AkapenProbe` の win-x64 自己完結 publish フォルダ一式
  (`Akapen.exe` / `akapen_native.dll` / `manual\akapen-manual-ja.html` など)。
- スタートメニュー + デスクトップに `Akapen` ショートカットを作成。
- 「アプリと機能」に登録し、アンインストール可能。
- `UpgradeCode` 固定 (`A4EF9BDF-CA54-4869-9B3F-7CD2B4342375`) によるメジャー
  アップグレード (上書き更新) 対応。
- EULA (`../installer/EULA-ja.txt`) を `WixUI_InstallDir` の同意画面に表示。
  日本語が化けないよう、`Make-LicenseRtf.ps1` が非 ASCII 文字を RTF の
  `\uN?` Unicode エスケープに変換した `License.rtf` を生成する。
- サイレントインストール `/qn` 対応。

## 前提ツール (Windows)

- .NET SDK (dotnet)。WiX は `WixToolset.Sdk` / `WixToolset.UI.wixext` /
  `WixToolset.Util.wixext` を NuGet から自動復元するため、グローバルツールの
  導入は不要 (初回はインターネット接続が必要)。
- Rust ツールチェーン (cargo)。

## ビルド手順

リポジトリを Windows 機に配置し、`apps` のあるルートで以下を実行する。

```powershell
# フルビルド (Rust ビルド → publish → RTF 生成 → MSI ビルド)
powershell -ExecutionPolicy Bypass -File apps\windows\installer-msi\Build-Msi.ps1
```

publish 済みの場合は再ビルドを省略できる。

```powershell
powershell -ExecutionPolicy Bypass -File apps\windows\installer-msi\Build-Msi.ps1 -SkipPublish
```

手動で行う場合は次の順序に相当する。

```powershell
cargo build -p akapen-ffi --release
dotnet publish -c Release -r win-x64 --self-contained true apps\windows\AkapenProbe\AkapenProbe.csproj
copy target\release\akapen.dll apps\windows\AkapenProbe\bin\Release\net8.0\win-x64\publish\akapen_native.dll
powershell -ExecutionPolicy Bypass -File apps\windows\installer-msi\Make-LicenseRtf.ps1
dotnet build apps\windows\installer-msi\Akapen.wixproj -c Release ^
    -p:PublishDir=apps\windows\AkapenProbe\bin\Release\net8.0\win-x64\publish
```

出力は `apps\windows\installer-msi\bin\Release\Akapen-1.1.2-win-x64.msi`。

## インストール / アンインストール

```powershell
# サイレントインストール
msiexec /i Akapen-1.1.2-win-x64.msi /qn

# サイレントアンインストール
msiexec /x Akapen-1.1.2-win-x64.msi /qn
```

GUI インストールでは EULA 同意画面とインストール先選択画面が表示される。

## 実装メモ

- per-user (`%LOCALAPPDATA%`) インストールでは、自動 harvest したファイル
  コンポーネントが HKCU キーパスを持たないため ICE38/ICE43/ICE57/ICE64 が
  誤検知として発火する。`Akapen.wixproj` の `SuppressIces` でこれらの検証のみ
  抑止している。アンインストール時のファイル削除は MSI のコンポーネント参照で
  正しく管理される。
- 空フォルダは ICE64 の仕様上、標準では削除されないため、`util:RemoveFolderEx`
  でインストール先を再帰削除し、アンインストール後に残骸を残さない。
- EULA 本文を変更したら `Make-LicenseRtf.ps1` を再実行して `License.rtf` を
  作り直す (`Build-Msi.ps1` は自動で再生成する)。
