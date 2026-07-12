// 出力先・接尾辞のユーザー設定の永続化(spec §4.7 / §9 M2 — M2-E)。
//
// mac シェルは `@AppStorage`(=UserDefaults の plist)に載せているが、Windows
// 側は「独自 JSON を `%LocalAppData%\Akapen\settings.json` に書く」方式を採る。
// unpackaged WinUI 3 でも WindowsAppSDK 1.7+ の
// `Microsoft.Windows.Storage.ApplicationData.GetForUnpackagedAsync(publisher)`
// を使えば `ApplicationData.LocalSettings` に相当する KVS が持てるが、
//   1) unpackaged では `publisher` 引数の扱いに実装差があり、CI で
//      windows-latest のヘッドレス実行時に確度がとりにくい、
//   2) 設定値は 5 個・ネスト無し・型は文字列だけなので、System.Text.Json で
//      直接読み書きするコストの方が API 依存を増やすより安い、
//   3) デバッグ時に `notepad` で直接開いて中身を確認できる。
// の 3 点で、JSON ファイルを採った(README apps/windows/README.md に根拠を再掲)。
//
// mac の @AppStorage キーと 1 対 1:
//   output.dirMode         : "besideInput" | "fixedAbsolute"
//   output.subfolderName   : 入力フォルダ相対のサブフォルダ名(既定 "_review")
//   output.fixedDir        : 固定絶対パス(既定 空文字)
//   output.flatSuffix      : フラット PNG のサフィックス(既定 "review")
//   output.strokesSuffix   : ストローク(PNG+JSON)のサフィックス(既定 "strokes")
//
// 入力検証は UI 側と `TrySave` 側の二か所で走る(mac の SettingsView と
// AppState.resolve* が同じ isValid* を共有しているのと同じ構図)。さらに Rust
// 側 `sanitize_suffix` (crates/akapen-ffi/src/lib.rs) が最終防衛として同じ規則
// (空白/`/`/`\\`/`.` を弾く)を通すので、UI がすり抜けても不正値がディスクに
// 落ちない。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AkapenApp;

/// <summary>
/// 出力先モード(spec §4.7)。既定は <see cref="BesideInput"/>(入力フォルダ相対)。
/// mac 側の <c>AkapenOutputDirMode</c> と 1 対 1、シリアライズ時の文字列表現も
/// mac の rawValue と揃える(<c>besideInput</c> / <c>fixedAbsolute</c>)。
/// </summary>
internal enum AkapenOutputDirMode
{
    BesideInput,
    FixedAbsolute,
}

/// <summary>
/// UI と保存層のあいだで受け渡す設定値。フィールドは全て公開の生の文字列で、
/// 検証は <see cref="SettingsStore"/> のヘルパを通す。
/// </summary>
internal sealed record AkapenSettings(
    AkapenOutputDirMode DirMode,
    string SubfolderName,
    string FixedDir,
    string FlatSuffix,
    string StrokesSuffix)
{
    /// <summary>
    /// 起動直後 / JSON 未生成時の既定値。mac 側 <c>AkapenSettingsDefault</c> と
    /// 完全一致(スペックの §4.3 / §4.7 の既定と同じ)。
    /// </summary>
    public static AkapenSettings Defaults => new(
        AkapenOutputDirMode.BesideInput,
        SubfolderName: "_review",
        FixedDir: "",
        FlatSuffix: "review",
        StrokesSuffix: "strokes");
}

/// <summary>
/// 設定 JSON の読み書き + 出力先・命名の解決層(spec §4.7)。<see cref="SettingsWindow"/>
/// と <see cref="MainWindow"/> の <c>TrySave</c> が同じルーチンを通ることで、
/// UI のプレビュー計算と実際の保存経路がズレない。
/// </summary>
internal static class SettingsStore
{
    // JSON 内のキー名は mac の @AppStorage キーと 1 対 1(SettingsView.swift の
    // AkapenSettingsKey を参照)。ドット付き文字列は System.Text.Json が特別扱い
    // しないので、そのまま生キーとして持てる。
    private const string KeyDirMode = "output.dirMode";
    private const string KeySubfolderName = "output.subfolderName";
    private const string KeyFixedDir = "output.fixedDir";
    private const string KeyFlatSuffix = "output.flatSuffix";
    private const string KeyStrokesSuffix = "output.strokesSuffix";

    private const string DirModeBesideInputRaw = "besideInput";
    private const string DirModeFixedAbsoluteRaw = "fixedAbsolute";

    // Windows のファイル/フォルダ名では使えない予約文字(NTFS ではなく Win32
    // API 層の制約 — `CreateFile` 系がこれらを別の意味に解釈するか、そもそも
    // 拒否する)。`:` はドライブレター区切りにも使われるため、これを弾くだけで
    // `C:review`(drive-relative)や `C:\abs\path` も同時に弾ける(いずれも
    // `:` を含む)。パス区切り(`/` `\`)は既存どおり別チェックのまま残す。
    private static readonly char[] WindowsReservedChars = { ':', '*', '?', '"', '<', '>', '|' };

    private static bool ContainsWindowsReservedChar(string s)
    {
        foreach (char ch in s)
        {
            if (Array.IndexOf(WindowsReservedChars, ch) >= 0) return true;
        }
        return false;
    }

    // Windows/DOS の予約デバイス名(Win32 API 層。NTFS の制約ではなく `CreateFile`
    // 系がこれらを実ファイルではなくデバイスとして解釈する)。拡張子を付けても
    // (`NUL.txt` 等)デバイスとして扱われるため、比較は拡張子を除いた stem に対して
    // 行う。大文字小文字は区別しない(Codex 指摘 Medium 1)。
    //
    // Codex 再レビュー Medium 1(4巡目): Microsoft の現行資料は通常数字の
    // COM1-9/LPT1-9 に加え、上付き数字(superscript)の `COM¹`(U+00B9)・
    // `COM²`(U+00B2)・`COM³`(U+00B3)・`LPT¹`/`LPT²`/`LPT³` も同じ予約名として
    // 明記している(Windows が内部的に COM1-3/LPT1-3 の別表記として扱う)。
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³",
        "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// <paramref name="name"/> の「最初のドットまで」を stem として、Windows 予約
    /// デバイス名(大文字小文字を区別しない)と一致するか。
    ///
    /// Codex 再レビュー Medium 1(4巡目): 従来は <see cref="Path.GetFileNameWithoutExtension"/>
    /// を使っていたが、これは「最後のドット」までしか切り落とさないため
    /// <c>NUL.tar.gz</c> の stem が <c>NUL.tar</c> になってしまい予約名判定を
    /// すり抜けていた。Microsoft は <c>NUL.txt</c> と <c>NUL.tar.gz</c> の両方が
    /// <c>NUL</c> と同等(デバイスとして解釈される)だと明記しているため、
    /// 最初のドットまでを stem として比較する。stem が空(先頭がドット、例:
    /// <c>.gitignore</c>)の場合は予約名ではない(他のルールに判定を委ねる)。
    /// 呼び出し側は制御文字を含まない文字列で呼ぶこと。
    /// </summary>
    internal static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        string stem = dot < 0 ? name : name.Substring(0, dot);
        if (stem.Length == 0) return false;
        return WindowsReservedDeviceNames.Contains(stem);
    }

    /// <summary>
    /// U+0000-U+001F の C0 制御文字(NUL 含む)を 1 文字でも含むか。これらは
    /// Windows のファイル/フォルダ名としては通っても `CreateFile` 系が拒否する
    /// ため、無警告で通すと export 直前まで気づけない失敗になる(Codex 指摘
    /// Medium 1)。
    /// </summary>
    internal static bool ContainsControlChar(string s)
    {
        foreach (char ch in s)
        {
            if (ch <= '\u001F') return true;
        }
        return false;
    }

    /// <summary>
    /// 設定ファイルの絶対パス。ディレクトリが無い / 権限が無い環境では
    /// <see cref="Save"/> が握り潰して既定値を返せるよう、Load はパスの解決だけ
    /// 別関数に切り出してある。
    ///
    /// setter はテスト専用の差し替え口(AkapenApp.Tests の Save/Load 境界値試験
    /// — Codex 指摘 Medium 2)。本番コードは一切代入しないため、実アプリでは
    /// 常に <see cref="ResolveSettingsPath"/> の結果のまま — macOS では
    /// <c>Environment.SpecialFolder.LocalApplicationData</c> がネイティブの
    /// ホームディレクトリ解決(<c>getpwuid</c> 相当)を使い、プロセスの
    /// <c>HOME</c> 環境変数の書き換えに追従しないため、テスト側での
    /// パス差し替えは env var 越しでなくこの setter を直接使う。
    /// </summary>
    internal static string SettingsFilePath { get; set; } = ResolveSettingsPath();

    private static string ResolveSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Akapen", "settings.json");
    }

    /// <summary>
    /// JSON を読み、mac 側と同じ 5 キー(未書き込みなら既定)を組み立てる。
    /// ファイルが壊れている(不正な JSON / スキーマ違反 / I/O 失敗)ときは既定値
    /// に落ちる — mac 側の @AppStorage も型不一致は既定に落とすので同じ挙動。
    /// 欠落キーは(壊れているわけではないので)無警告で既定値埋めするが、JSON
    /// 自体が読めない/パースできない場合、および**キーはあるが値の型が不正**
    /// (例: <c>{"output.flatSuffix":42}</c>)な場合は <c>LoadWarning</c> に理由を
    /// 積んで返す。後者は「欠落」とは別のスキーマ違反であり、無警告で既定値に
    /// 落とすと壊れた設定に気づけない(Codex 指摘 Medium 3)。呼び出し側
    /// (<see cref="SettingsWindow"/> / <c>MainWindow.TrySave</c>)はこれを既存の
    /// statusText 表示へ合流させ、「設定が読めていないのに気づけない」状態を
    /// 防ぐ(Codex 指摘 Medium 1)。
    /// </summary>
    public static (AkapenSettings Settings, string? LoadWarning) Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return (AkapenSettings.Defaults, null);
            }
            string json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (AkapenSettings.Defaults,
                    $"{SettingsFilePath} の内容が不正なため既定値を使います。");
            }
            var root = doc.RootElement;
            var schemaWarnings = new List<string>();
            string? Get(string key) => GetString(root, key, schemaWarnings);
            var settings = new AkapenSettings(
                DirMode: ParseDirMode(Get(KeyDirMode)),
                SubfolderName: Get(KeySubfolderName) ?? AkapenSettings.Defaults.SubfolderName,
                FixedDir: Get(KeyFixedDir) ?? AkapenSettings.Defaults.FixedDir,
                FlatSuffix: Get(KeyFlatSuffix) ?? AkapenSettings.Defaults.FlatSuffix,
                StrokesSuffix: Get(KeyStrokesSuffix) ?? AkapenSettings.Defaults.StrokesSuffix);
            string? warning = schemaWarnings.Count > 0
                ? string.Join("; ", schemaWarnings)
                : null;
            return (settings, warning);
        }
        catch (IOException)
        {
            return (AkapenSettings.Defaults,
                $"{SettingsFilePath} を読み込めません。既定値を使います。");
        }
        catch (UnauthorizedAccessException)
        {
            return (AkapenSettings.Defaults,
                $"{SettingsFilePath} への読み取り権限がありません。既定値を使います。");
        }
        catch (JsonException)
        {
            return (AkapenSettings.Defaults,
                $"{SettingsFilePath} が壊れているため既定値を使います。");
        }
    }

    /// <summary>
    /// 現在の設定を JSON に書き出す。フォルダ作成・上書きに失敗しても例外は
    /// 呼び出し側に伝えず false を返す(<see cref="SettingsWindow"/> は
    /// StatusText 相当の場所を持たないので、失敗しても UI は既定値に頼って
    /// 動き続ける)。
    /// </summary>
    public static bool Save(AkapenSettings s)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // 生 JSON を System.Text.Json で書く。読み手のデバッグを楽にするため
            // Indented=true。キーの表記は mac と 1 対 1、値は全て文字列で保持
            // (数値やブールを入れないのでスキーマ変更に強い)。
            var opts = new JsonWriterOptions { Indented = true };
            using var stream = File.Create(SettingsFilePath);
            using var writer = new Utf8JsonWriter(stream, opts);
            writer.WriteStartObject();
            writer.WriteString(KeyDirMode, DirModeToRaw(s.DirMode));
            writer.WriteString(KeySubfolderName, s.SubfolderName);
            writer.WriteString(KeyFixedDir, s.FixedDir);
            writer.WriteString(KeyFlatSuffix, s.FlatSuffix);
            writer.WriteString(KeyStrokesSuffix, s.StrokesSuffix);
            writer.WriteEndObject();
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── 入力検証(SettingsWindow と TrySave で共有)──────────────────────
    //
    // C# の `char.IsWhiteSpace` は Unicode の "White_Space" 集合と一致し、
    // 改行(U+000A / U+000D)も含む。Rust の `str::trim` は Unicode の
    // "White_Space" ではなく `char::is_whitespace`(=同じ White_Space 集合)
    // を使うので、両者の trim 判定は完全に一致する。mac 側は
    // `.whitespacesAndNewlines` に統一済み(Ch.6 で一度そこで揃えた)なので、
    // 三シェル(Rust / mac / Windows)全て同じ規則になる。

    /// <summary>
    /// 接尾辞(mac の <c>isValidSuffix</c> 相当)。空・空白のみ・
    /// パス区切り(<c>/</c>・<c>\\</c>)・ドット(<c>.</c>)・Windows 予約文字
    /// (<c>: * ? " &lt; &gt; |</c>)を含むと無効。Rust 側 <c>sanitize_suffix</c>
    /// と同一ルール(予約文字は Windows 固有の追加防衛 — Codex 指摘 High:
    /// 予約文字を含むサフィックスは「有効」表示のまま export 時に失敗し得た)。
    ///
    /// Codex 指摘 Medium 1 への対応で以下も無効とする:
    ///   - C0 制御文字(U+0000-U+001F)を含む入力。ファイル名としては通っても
    ///     `CreateFile` 系が拒否し得る
    ///   - Windows 予約デバイス名(<c>CON</c>/<c>NUL</c>/<c>COM1</c> 等。大文字
    ///     小文字を区別しない)と一致する入力。<c>&lt;stem&gt;.&lt;suffix&gt;.png</c>
    ///     の <c>&lt;suffix&gt;</c> 単体が予約名と一致するとファイル名全体が
    ///     デバイス名解釈される余地は無いが、接尾辞は独立の項目として同じ
    ///     予約名チェックを Subfolder と揃えておく
    /// </summary>
    public static bool IsValidSuffix(string? s)
    {
        if (s is null) return false;
        if (ContainsControlChar(s)) return false;
        string trimmed = s.Trim();
        if (trimmed.Length == 0) return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == '/' || ch == '\\' || ch == '.') return false;
        }
        if (ContainsWindowsReservedChar(trimmed)) return false;
        if (IsReservedDeviceName(trimmed)) return false;
        return true;
    }

    /// <summary>
    /// 入力フォルダ配下のサブフォルダ名(mac の <c>isValidSubfolder</c> 相当)。
    /// 空・空白のみ・パス区切りを含むと無効。trim 後が <c>.</c> / <c>..</c>
    /// と完全一致するのも無効(directory traversal 対策)。
    /// 接頭のドット(<c>.veda</c> のような正当なケース)は許可する — 単体の
    /// <c>.</c> / <c>..</c> だけを弾けば十分。
    ///
    /// Codex 指摘 High への対応で以下を追加拒否する:
    ///   - <c>Path.IsPathRooted</c> が true になる文字列(絶対パスだけでなく
    ///     <c>C:review</c> のような drive-relative も rooted 判定される —
    ///     `Path.Combine(inputDir, "C:review")` は rooted な第2引数を採用して
    ///     inputDir を素通りするため、無対策だと入力フォルダ外の C ドライブ
    ///     直下に書き出せてしまう directory traversal だった)
    ///   - Windows 予約文字(<c>: * ? " &lt; &gt; |</c>)。<c>:</c> は上の
    ///     rooted 判定と多くの場合重複するが、UNC でないドライブ文字以外の
    ///     コロン使用(<c>foo:bar</c>)も明示的に弾く
    ///   - 末尾が半角スペースまたは <c>.</c> の入力(trim 前の raw value で
    ///     判定)。Windows は名前末尾のスペース/ピリオドをサイレントに落とす
    ///     ため、UI 表示と実際に作られるフォルダ名がズレる余地を断つ
    ///
    /// Codex 指摘 Medium 1 への対応で以下も追加拒否する:
    ///   - C0 制御文字(U+0000-U+001F)を含む入力。ファイル名としては通っても
    ///     `CreateFile` 系が拒否し得る(無警告で通すと export 直前まで気づけ
    ///     ない失敗になっていた)
    ///   - Windows 予約デバイス名(<c>CON</c>/<c>PRN</c>/<c>AUX</c>/<c>NUL</c>/
    ///     <c>COM1</c>-<c>COM9</c>/<c>LPT1</c>-<c>LPT9</c>)。大文字小文字を
    ///     区別せず、拡張子付き(<c>NUL.txt</c>)も stem 一致で弾く。無対策だと
    ///     <c>ResolveOutputDir</c> が <c>&lt;input&gt;/CON</c> を警告なしで返し、
    ///     最終的に <c>Directory.CreateDirectory</c> が失敗するまで気づけな
    ///     かった
    /// </summary>
    public static bool IsValidSubfolder(string? s)
    {
        if (s is null) return false;
        if (ContainsControlChar(s)) return false;
        if (s.Length > 0)
        {
            char lastRaw = s[s.Length - 1];
            if (lastRaw == ' ' || lastRaw == '.') return false;
        }
        string trimmed = s.Trim();
        if (trimmed.Length == 0) return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == '/' || ch == '\\') return false;
        }
        if (trimmed == "." || trimmed == "..") return false;
        if (ContainsWindowsReservedChar(trimmed)) return false;
        if (IsReservedDeviceName(trimmed)) return false;
        try
        {
            if (Path.IsPathRooted(trimmed)) return false;
        }
        catch (ArgumentException)
        {
            // 制御文字などで Path 側が例外を投げる場合。無効扱い。
            return false;
        }
        return true;
    }

    /// <summary>
    /// 固定絶対パス(mac の <c>isValidFixedDir</c> 相当)。空・相対・非存在
    /// ディレクトリは無効。<c>Path.IsPathFullyQualified</c> は Windows の
    /// UNC(<c>\\server\share</c>)やドライブレター(<c>C:\...</c>)を絶対
    /// パスとして正しく判定する。
    /// </summary>
    public static bool IsValidFixedDir(string? s)
    {
        if (s is null) return false;
        string trimmed = s.Trim();
        if (trimmed.Length == 0) return false;
        try
        {
            if (!Path.IsPathFullyQualified(trimmed)) return false;
            return Directory.Exists(trimmed);
        }
        catch (ArgumentException)
        {
            // 制御文字などで Path 側が例外を投げる場合。無効扱い。
            return false;
        }
    }

    // ── 出力先・命名の解決(TrySave が呼ぶ)─────────────────────────────

    /// <summary>
    /// 現在の設定に従って書き出しフォルダを決める。設定が無効(空・区切りや
    /// <c>.</c>/<c>..</c> を含むサブフォルダ名、存在しない固定パスなど)なら
    /// <c>&lt;入力フォルダ&gt;/_review/</c> にフォールバックし、その理由を
    /// <paramref name="fallbackWarning"/> に返す(呼び出し側が statusText に
    /// 合流させる — mac の <c>AppState.resolveOutputDir</c> と同型)。
    /// </summary>
    public static (string Dir, string? FallbackWarning) ResolveOutputDir(
        string inputPath, AkapenSettings settings)
    {
        string inputDir = Path.GetDirectoryName(inputPath) ?? ".";
        string defaultDir = Path.Combine(inputDir, AkapenSettings.Defaults.SubfolderName);

        switch (settings.DirMode)
        {
            case AkapenOutputDirMode.BesideInput:
                if (IsValidSubfolder(settings.SubfolderName))
                {
                    string trimmedSubfolder = settings.SubfolderName.Trim();
                    // 二段防衛(Codex 指摘 High): IsValidSubfolder が rooted な
                    // 値(`C:review` 等)を弾く前提だが、ここでも同じ判定を
                    // 独立に取り、`Path.Combine` が inputDir を素通りして
                    // ドライブ直下に書き出す事態を最後の砦として防ぐ。
                    // IsValidSubfolder の実装が将来変わっても、この export の
                    // 出口だけは inputDir 配下から出ないことを保証する。
                    if (Path.IsPathRooted(trimmedSubfolder))
                    {
                        return (defaultDir,
                            $"設定のサブフォルダ名が無効なので {AkapenSettings.Defaults.SubfolderName}/ にフォールバック");
                    }
                    return (Path.Combine(inputDir, trimmedSubfolder), null);
                }
                return (defaultDir,
                    $"設定のサブフォルダ名が無効なので {AkapenSettings.Defaults.SubfolderName}/ にフォールバック");

            case AkapenOutputDirMode.FixedAbsolute:
                if (IsValidFixedDir(settings.FixedDir))
                {
                    return (settings.FixedDir.Trim(), null);
                }
                return (defaultDir,
                    $"設定の固定パスが無効なので {AkapenSettings.Defaults.SubfolderName}/ にフォールバック");

            default:
                return (defaultDir, null);
        }
    }

    /// <summary>
    /// 現在の設定から接尾辞ペアを解決する。無効なフィールドは既定
    /// (<c>review</c> / <c>strokes</c>)に落とす — Rust 側 <c>sanitize_suffix</c>
    /// と同じ二段目チェック。フォールバックが起きたら短い説明を返し、呼び出し側
    /// が statusText へ合流できるようにする(mac は AppState.save 側で警告を
    /// 別立てで扱っていたが、Windows は resolve* を一箇所に集めた)。
    /// </summary>
    public static (string Flat, string Strokes, string? FallbackWarning) ResolveOutputNaming(
        AkapenSettings settings)
    {
        bool flatOk = IsValidSuffix(settings.FlatSuffix)
            && !ContainsWindowsReservedChar(settings.FlatSuffix.Trim());
        bool strokesOk = IsValidSuffix(settings.StrokesSuffix)
            && !ContainsWindowsReservedChar(settings.StrokesSuffix.Trim());
        // 二段防衛(Codex 指摘 High): IsValidSuffix はすでに予約文字を弾くが、
        // export 直前のこの解決層でも同じ判定を独立に取り直す。予約文字が
        // 万一すり抜けて export に渡ると「有効」表示のまま書き出しだけ失敗する
        // ので、ここが最後の砦になる。
        string flat = flatOk ? settings.FlatSuffix.Trim() : AkapenSettings.Defaults.FlatSuffix;
        string strokes = strokesOk ? settings.StrokesSuffix.Trim() : AkapenSettings.Defaults.StrokesSuffix;

        if (flatOk && strokesOk)
        {
            return (flat, strokes, null);
        }
        string warn = (flatOk, strokesOk) switch
        {
            (false, true) => "設定の flat 接尾辞が無効なので既定 \"review\" にフォールバック",
            (true, false) => "設定の strokes 接尾辞が無効なので既定 \"strokes\" にフォールバック",
            _ => "設定の flat / strokes 接尾辞が無効なので既定にフォールバック",
        };
        return (flat, strokes, warn);
    }

    // ── 内部ヘルパ ────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="key"/> を文字列として読む。キー自体が無い(欠落)場合は
    /// 無警告で null を返す — スキーマとしては正常(未書き込み)なケース。
    /// キーは存在するが値が文字列でない場合(<c>{"output.flatSuffix":42}</c>
    /// のような Number/Bool/Null/Array/Object)は、既定値へ落とすと同時に
    /// <paramref name="warnings"/> にスキーマ違反の理由を積む(Codex 指摘
    /// Medium 3 — 「欠落」と「型不正」を区別せず両方無警告だったのが問題)。
    /// </summary>
    private static string? GetString(JsonElement root, string key, List<string> warnings)
    {
        if (!root.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        warnings.Add($"'{key}' has non-string value, using default");
        return null;
    }

    private static AkapenOutputDirMode ParseDirMode(string? raw)
    {
        return raw switch
        {
            DirModeFixedAbsoluteRaw => AkapenOutputDirMode.FixedAbsolute,
            DirModeBesideInputRaw => AkapenOutputDirMode.BesideInput,
            _ => AkapenSettings.Defaults.DirMode,
        };
    }

    public static string DirModeToRaw(AkapenOutputDirMode mode) => mode switch
    {
        AkapenOutputDirMode.FixedAbsolute => DirModeFixedAbsoluteRaw,
        _ => DirModeBesideInputRaw,
    };
}
