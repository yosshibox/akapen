// SettingsStore.cs の境界値試験(spec §4.7 / §9 M2-E)。
//
// このプロジェクトの由来は Codex 再レビュー Medium 2: SettingsStore.cs を
// 書いた際の手元 /tmp 境界試験は session 終了時に消え、apps/windows 配下にも
// 試験が一切残っていなかった(CI で再走できる形が無かった)。この
// AkapenApp.Tests はその埋め合わせで、SettingsStore.cs を Link 参照する
// standalone console app として repo に残す(csproj のコメント参照)。
//
// フレームワークは使わず素朴な Assert 集計(Check ヘルパ)。理由:
//   - SettingsStore.cs 自体が単純な static ヘルパの集合で、xUnit/NUnit を
//     引き込むほどの複雑さがない
//   - 「外部 npm 依存を勝手に増やさない」という VEDA 側の規約と同じ精神で、
//     Akapen 側でも試験専用の外部 NuGet 依存を増やさず、既存 dotnet SDK だけ
//     で完結させる
//
// 制御文字を含む入力は文字列リテラルの \uXXXX エスケープではなく
// (char)0x01 等の明示的な数値キャストで組み立てる — ソースファイルに生の
// 制御バイトが紛れ込むのを避けるため(エディタ/diff/git での事故防止)。
//
// 実行: `dotnet run --project apps/windows/AkapenApp.Tests`
// 失敗があれば非ゼロで終了するので CI のステップとしてそのまま使える。

using System;
using System.IO;
using AkapenApp;

int total = 0;
int failed = 0;

void Check(bool condition, string description)
{
    total++;
    if (!condition)
    {
        failed++;
        Console.WriteLine($"FAIL: {description}");
    }
}

string withEmbeddedControlChar = "with" + (char)0x01 + "ctrl";
string bareNulChar = ((char)0x00).ToString();

// ── IsValidSuffix ────────────────────────────────────────────────────────

Check(SettingsStore.IsValidSuffix(null) == false, "IsValidSuffix(null)");
Check(SettingsStore.IsValidSuffix("") == false, "IsValidSuffix(\"\")");
Check(SettingsStore.IsValidSuffix("   ") == false, "IsValidSuffix(whitespace only)");
Check(SettingsStore.IsValidSuffix("review") == true, "IsValidSuffix(\"review\")");
Check(SettingsStore.IsValidSuffix("strokes") == true, "IsValidSuffix(\"strokes\")");
Check(SettingsStore.IsValidSuffix(" padded ") == true, "IsValidSuffix(\" padded \") trims to valid");
Check(SettingsStore.IsValidSuffix("a/b") == false, "IsValidSuffix contains '/'");
Check(SettingsStore.IsValidSuffix("a\\b") == false, "IsValidSuffix contains '\\'");
Check(SettingsStore.IsValidSuffix("a.b") == false, "IsValidSuffix contains '.'");
Check(SettingsStore.IsValidSuffix("a:b") == false, "IsValidSuffix contains ':'");
Check(SettingsStore.IsValidSuffix("a*b") == false, "IsValidSuffix contains '*'");
Check(SettingsStore.IsValidSuffix("a?b") == false, "IsValidSuffix contains '?'");
Check(SettingsStore.IsValidSuffix("a\"b") == false, "IsValidSuffix contains '\"'");
Check(SettingsStore.IsValidSuffix("a<b") == false, "IsValidSuffix contains '<'");
Check(SettingsStore.IsValidSuffix("a>b") == false, "IsValidSuffix contains '>'");
Check(SettingsStore.IsValidSuffix("a|b") == false, "IsValidSuffix contains '|'");
// Codex 指摘 Medium 1: 予約デバイス名(大文字小文字を区別しない)
Check(SettingsStore.IsValidSuffix("CON") == false, "IsValidSuffix(\"CON\") reserved");
Check(SettingsStore.IsValidSuffix("con") == false, "IsValidSuffix(\"con\") reserved, case-insensitive");
Check(SettingsStore.IsValidSuffix("Con") == false, "IsValidSuffix(\"Con\") reserved, mixed case");
Check(SettingsStore.IsValidSuffix("PRN") == false, "IsValidSuffix(\"PRN\") reserved");
Check(SettingsStore.IsValidSuffix("AUX") == false, "IsValidSuffix(\"AUX\") reserved");
Check(SettingsStore.IsValidSuffix("NUL") == false, "IsValidSuffix(\"NUL\") reserved");
Check(SettingsStore.IsValidSuffix("COM1") == false, "IsValidSuffix(\"COM1\") reserved");
Check(SettingsStore.IsValidSuffix("com9") == false, "IsValidSuffix(\"com9\") reserved, case-insensitive");
Check(SettingsStore.IsValidSuffix("LPT1") == false, "IsValidSuffix(\"LPT1\") reserved");
Check(SettingsStore.IsValidSuffix("lpt9") == false, "IsValidSuffix(\"lpt9\") reserved, case-insensitive");
Check(SettingsStore.IsValidSuffix("CONSOLE") == true, "IsValidSuffix(\"CONSOLE\") not an exact reserved match");
Check(SettingsStore.IsValidSuffix("COM10") == true, "IsValidSuffix(\"COM10\") outside COM1-9 range");
// Codex 指摘 Medium 1: 制御文字
Check(SettingsStore.IsValidSuffix(withEmbeddedControlChar) == false, "IsValidSuffix contains embedded control char");
Check(SettingsStore.IsValidSuffix(bareNulChar) == false, "IsValidSuffix is a bare NUL char");

// ── IsValidSubfolder ─────────────────────────────────────────────────────

Check(SettingsStore.IsValidSubfolder(null) == false, "IsValidSubfolder(null)");
Check(SettingsStore.IsValidSubfolder("") == false, "IsValidSubfolder(\"\")");
Check(SettingsStore.IsValidSubfolder("   ") == false, "IsValidSubfolder(whitespace only)");
Check(SettingsStore.IsValidSubfolder("_review") == true, "IsValidSubfolder(\"_review\")");
Check(SettingsStore.IsValidSubfolder(".veda") == true, "IsValidSubfolder(\".veda\") leading dot allowed");
Check(SettingsStore.IsValidSubfolder(".") == false, "IsValidSubfolder(\".\")");
Check(SettingsStore.IsValidSubfolder("..") == false, "IsValidSubfolder(\"..\")");
Check(SettingsStore.IsValidSubfolder("a/b") == false, "IsValidSubfolder contains '/'");
Check(SettingsStore.IsValidSubfolder("a\\b") == false, "IsValidSubfolder contains '\\'");
Check(SettingsStore.IsValidSubfolder("C:\\abs\\path") == false, "IsValidSubfolder rooted absolute path");
Check(SettingsStore.IsValidSubfolder("C:review") == false, "IsValidSubfolder drive-relative rooted path");
Check(SettingsStore.IsValidSubfolder("foo ") == false, "IsValidSubfolder trailing space");
Check(SettingsStore.IsValidSubfolder("foo.") == false, "IsValidSubfolder trailing dot");
Check(SettingsStore.IsValidSubfolder("foo:bar") == false, "IsValidSubfolder contains ':'");
// Codex 指摘 Medium 1: 予約デバイス名(拡張子付きの stem 一致も含む)
Check(SettingsStore.IsValidSubfolder("CON") == false, "IsValidSubfolder(\"CON\") reserved");
Check(SettingsStore.IsValidSubfolder("con") == false, "IsValidSubfolder(\"con\") reserved, case-insensitive");
Check(SettingsStore.IsValidSubfolder("Nul.txt") == false, "IsValidSubfolder(\"Nul.txt\") reserved stem with extension");
Check(SettingsStore.IsValidSubfolder("COM1") == false, "IsValidSubfolder(\"COM1\") reserved");
Check(SettingsStore.IsValidSubfolder("LPT9") == false, "IsValidSubfolder(\"LPT9\") reserved");
Check(SettingsStore.IsValidSubfolder("CONFIG") == true, "IsValidSubfolder(\"CONFIG\") not an exact reserved match");
// Codex 指摘 Medium 1: 制御文字
Check(SettingsStore.IsValidSubfolder(withEmbeddedControlChar) == false, "IsValidSubfolder contains embedded control char");
Check(SettingsStore.IsValidSubfolder("valid_name-123") == true, "IsValidSubfolder(\"valid_name-123\")");

// ── 予約デバイス名・制御文字: 網羅境界試験(Codex 再レビュー 4巡目 Medium 2) ──
//
// 上の IsValidSuffix / IsValidSubfolder の予約デバイス名試験は代表値
// (CON/PRN/AUX/NUL/COM1/COM9/LPT1/LPT9 のみ)で、COM2-8・LPT2-8 の抜け・
// superscript バリアントの抜け・複数拡張子の抜けが Codex から指摘された
// (4巡目 Medium 2)。ここで 1-9 全件・superscript 全件・複数拡張子を網羅する。

for (int i = 1; i <= 9; i++)
{
    string com = $"COM{i}";
    string lpt = $"LPT{i}";
    Check(SettingsStore.IsValidSuffix(com) == false, $"IsValidSuffix(\"{com}\") reserved (exhaustive 1-9)");
    Check(SettingsStore.IsValidSuffix(lpt) == false, $"IsValidSuffix(\"{lpt}\") reserved (exhaustive 1-9)");
    Check(SettingsStore.IsValidSubfolder(com) == false, $"IsValidSubfolder(\"{com}\") reserved (exhaustive 1-9)");
    Check(SettingsStore.IsValidSubfolder(lpt) == false, $"IsValidSubfolder(\"{lpt}\") reserved (exhaustive 1-9)");
}

// Superscript バリアント(Codex 指摘 4巡目 Medium 1): Microsoft の現行資料が
// 明記する COM¹/COM²/COM³/LPT¹/LPT²/LPT³(上付き数字、通常数字とは別の
// コードポイント)。
string[] superscriptReservedNames =
{
    "COM¹", "COM²", "COM³",
    "LPT¹", "LPT²", "LPT³",
};
foreach (string name in superscriptReservedNames)
{
    Check(SettingsStore.IsValidSuffix(name) == false, $"IsValidSuffix(\"{name}\") reserved superscript variant");
    Check(SettingsStore.IsValidSubfolder(name) == false, $"IsValidSubfolder(\"{name}\") reserved superscript variant");
}

// 複数拡張子(Codex 指摘 4巡目 Medium 1): stem は「最初のドットまで」で判定する
// ため、NUL.tar.gz / CON.log.txt / AUX.bak.7z はいずれも拒否されるべき。旧実装
// は Path.GetFileNameWithoutExtension が最後のドットまでしか切り落とさず、
// NUL.tar.gz の stem が NUL.tar になって予約名判定をすり抜けていた。
Check(SettingsStore.IsValidSubfolder("NUL.tar.gz") == false,
    "IsValidSubfolder(\"NUL.tar.gz\") reserved stem across multiple extensions");
Check(SettingsStore.IsValidSubfolder("CON.log.txt") == false,
    "IsValidSubfolder(\"CON.log.txt\") reserved stem across multiple extensions");
Check(SettingsStore.IsValidSubfolder("AUX.bak.7z") == false,
    "IsValidSubfolder(\"AUX.bak.7z\") reserved stem across multiple extensions");
Check(SettingsStore.IsReservedDeviceName("NUL.tar.gz") == true,
    "IsReservedDeviceName(\"NUL.tar.gz\") true via first-dot stem");
Check(SettingsStore.IsReservedDeviceName("NUL.txt") == true,
    "IsReservedDeviceName(\"NUL.txt\") true (single extension, regression check)");
Check(SettingsStore.IsReservedDeviceName(".gitignore") == false,
    "IsReservedDeviceName(\".gitignore\") empty stem before leading dot is not reserved");

// 制御文字: 代表値(U+0000/U+0001)以外に U+0002-U+001F 範囲の追加網羅
// (Codex 指摘 4巡目 Medium 2)。embedded(文字列中に混入)/ bare(単独文字)の
// 両方、および ContainsControlChar 単体 + IsValidSuffix/IsValidSubfolder 経由
// の両方を確認する。
int[] extraControlCodePoints = { 0x02, 0x07, 0x09, 0x0A, 0x0D, 0x1B, 0x1F };
foreach (int cp in extraControlCodePoints)
{
    char ctrl = (char)cp;
    string embedded = "pre" + ctrl + "post";
    string bare = ctrl.ToString();
    Check(SettingsStore.ContainsControlChar(embedded) == true, $"ContainsControlChar embedded U+{cp:X4}");
    Check(SettingsStore.ContainsControlChar(bare) == true, $"ContainsControlChar bare U+{cp:X4}");
    Check(SettingsStore.IsValidSuffix(embedded) == false, $"IsValidSuffix rejects embedded U+{cp:X4}");
    Check(SettingsStore.IsValidSuffix(bare) == false, $"IsValidSuffix rejects bare U+{cp:X4}");
    Check(SettingsStore.IsValidSubfolder(embedded) == false, $"IsValidSubfolder rejects embedded U+{cp:X4}");
    Check(SettingsStore.IsValidSubfolder(bare) == false, $"IsValidSubfolder rejects bare U+{cp:X4}");
}

// ── IsValidFixedDir ──────────────────────────────────────────────────────

string existingTempDir = Directory.CreateTempSubdirectory("akapen-settingsstore-fixeddir-").FullName;
try
{
    Check(SettingsStore.IsValidFixedDir(null) == false, "IsValidFixedDir(null)");
    Check(SettingsStore.IsValidFixedDir("") == false, "IsValidFixedDir(\"\")");
    Check(SettingsStore.IsValidFixedDir("relative/path") == false, "IsValidFixedDir(relative path)");
    Check(SettingsStore.IsValidFixedDir(Path.Combine(existingTempDir, "does-not-exist")) == false,
        "IsValidFixedDir(non-existent absolute path)");
    Check(SettingsStore.IsValidFixedDir(existingTempDir) == true, "IsValidFixedDir(existing absolute dir)");
}
finally
{
    Directory.Delete(existingTempDir, recursive: true);
}

// ── ResolveOutputDir ─────────────────────────────────────────────────────

string fakeInputPath = Path.Combine(Path.GetTempPath(), "akapen-input-dir", "frame001.png");
string fakeInputDir = Path.GetDirectoryName(fakeInputPath)!;

{
    var settings = AkapenSettings.Defaults with { SubfolderName = "_custom" };
    var (dir, warning) = SettingsStore.ResolveOutputDir(fakeInputPath, settings);
    Check(dir == Path.Combine(fakeInputDir, "_custom"), "ResolveOutputDir valid subfolder resolves beside input");
    Check(warning is null, "ResolveOutputDir valid subfolder has no warning");
}
{
    // Codex 指摘 Medium 1 の直接の回帰試験: 修正前は "CON" が IsValidSubfolder を
    // 素通りし、ResolveOutputDir が警告なしで <input>/CON を返していた。
    var settings = AkapenSettings.Defaults with { SubfolderName = "CON" };
    var (dir, warning) = SettingsStore.ResolveOutputDir(fakeInputPath, settings);
    Check(dir == Path.Combine(fakeInputDir, AkapenSettings.Defaults.SubfolderName),
        "ResolveOutputDir falls back to default dir when subfolder is a reserved device name");
    Check(warning is not null, "ResolveOutputDir warns when subfolder is a reserved device name");
}
{
    string existingFixedDir = Directory.CreateTempSubdirectory("akapen-settingsstore-resolve-").FullName;
    try
    {
        var settings = AkapenSettings.Defaults with
        {
            DirMode = AkapenOutputDirMode.FixedAbsolute,
            FixedDir = existingFixedDir,
        };
        var (dir, warning) = SettingsStore.ResolveOutputDir(fakeInputPath, settings);
        Check(dir == existingFixedDir, "ResolveOutputDir fixed-absolute mode with valid dir");
        Check(warning is null, "ResolveOutputDir fixed-absolute valid dir has no warning");
    }
    finally
    {
        Directory.Delete(existingFixedDir, recursive: true);
    }
}
{
    var settings = AkapenSettings.Defaults with
    {
        DirMode = AkapenOutputDirMode.FixedAbsolute,
        FixedDir = Path.Combine(Path.GetTempPath(), "akapen-does-not-exist-" + Guid.NewGuid()),
    };
    var (dir, warning) = SettingsStore.ResolveOutputDir(fakeInputPath, settings);
    Check(dir == Path.Combine(fakeInputDir, AkapenSettings.Defaults.SubfolderName),
        "ResolveOutputDir fixed-absolute mode falls back when dir does not exist");
    Check(warning is not null, "ResolveOutputDir fixed-absolute invalid dir warns");
}

// ── ResolveOutputNaming ──────────────────────────────────────────────────

{
    var settings = AkapenSettings.Defaults with { FlatSuffix = "flatx", StrokesSuffix = "strokesx" };
    var (flat, strokes, warning) = SettingsStore.ResolveOutputNaming(settings);
    Check(flat == "flatx" && strokes == "strokesx", "ResolveOutputNaming valid suffixes pass through");
    Check(warning is null, "ResolveOutputNaming valid suffixes have no warning");
}
{
    // Codex 指摘 Medium 1 の間接回帰試験: 予約デバイス名の接尾辞も
    // ResolveOutputNaming の時点で既定にフォールバックすること。
    var settings = AkapenSettings.Defaults with { FlatSuffix = "NUL" };
    var (flat, _, warning) = SettingsStore.ResolveOutputNaming(settings);
    Check(flat == AkapenSettings.Defaults.FlatSuffix, "ResolveOutputNaming falls back flat suffix for reserved device name");
    Check(warning is not null, "ResolveOutputNaming warns for reserved-device-name flat suffix");
}
{
    var settings = AkapenSettings.Defaults with { StrokesSuffix = "a.b" };
    var (_, strokes, warning) = SettingsStore.ResolveOutputNaming(settings);
    Check(strokes == AkapenSettings.Defaults.StrokesSuffix, "ResolveOutputNaming falls back invalid strokes suffix");
    Check(warning is not null, "ResolveOutputNaming warns for invalid strokes suffix");
}
{
    var settings = AkapenSettings.Defaults with { FlatSuffix = "a/b", StrokesSuffix = "a\\b" };
    var (flat, strokes, warning) = SettingsStore.ResolveOutputNaming(settings);
    Check(flat == AkapenSettings.Defaults.FlatSuffix && strokes == AkapenSettings.Defaults.StrokesSuffix,
        "ResolveOutputNaming falls back both suffixes when both invalid");
    Check(warning is not null, "ResolveOutputNaming warns when both suffixes invalid");
}

// ── Save / Load: 実ファイル I/O ──────────────────────────────────────────
//
// 本物の %LocalAppData%(実行中ユーザーの実際の settings.json)を汚さない
// ため、SettingsFilePath のテスト専用 setter(Codex 指摘 Medium 2)で一時
// ディレクトリ配下に差し替えてから Save/Load を検証する。env var
// (HOME/XDG_DATA_HOME)越しの差し替えは macOS ではネイティブのホーム解決に
// 追従せず効かないため使わない(SettingsStore.cs の setter コメント参照)。

string testHome = Directory.CreateTempSubdirectory("akapen-settingsstore-home-").FullName;
string testSettingsPath = Path.Combine(testHome, "Akapen", "settings.json");
string originalSettingsFilePath = SettingsStore.SettingsFilePath;
SettingsStore.SettingsFilePath = testSettingsPath;

try
{
    string settingsPath = SettingsStore.SettingsFilePath;
    Check(settingsPath == testSettingsPath,
        $"SettingsFilePath is redirected under the test HOME (got: {settingsPath})");

    // 正常系ラウンドトリップ。
    var roundtrip = new AkapenSettings(
        AkapenOutputDirMode.FixedAbsolute, "_mysub", "/some/fixed/dir", "myflat", "mystrokes");
    Check(SettingsStore.Save(roundtrip) == true, "Save succeeds against the redirected settings path");
    var (loaded, loadWarning) = SettingsStore.Load();
    Check(loaded == roundtrip, "Load round-trips the exact settings just saved");
    Check(loadWarning is null, "Load has no warning for a well-formed round-tripped file");

    // 壊れた JSON(パース不能)。
    File.WriteAllText(settingsPath, "not json");
    var (afterBadJson, badJsonWarning) = SettingsStore.Load();
    Check(afterBadJson == AkapenSettings.Defaults, "Load falls back to defaults for unparsable JSON");
    Check(badJsonWarning is not null, "Load warns for unparsable JSON");

    // ルートが object でない JSON(配列)。
    File.WriteAllText(settingsPath, "[]");
    var (afterArray, arrayWarning) = SettingsStore.Load();
    Check(afterArray == AkapenSettings.Defaults, "Load falls back to defaults for a JSON array root");
    Check(arrayWarning is not null, "Load warns for a non-object JSON root");

    // 欠落キー(スキーマとしては正常、無警告で既定埋め)。
    File.WriteAllText(settingsPath, "{\"output.flatSuffix\":\"custom\"}");
    var (afterPartial, partialWarning) = SettingsStore.Load();
    Check(afterPartial.FlatSuffix == "custom", "Load reads the one present key");
    Check(afterPartial.StrokesSuffix == AkapenSettings.Defaults.StrokesSuffix,
        "Load silently defaults missing keys");
    Check(partialWarning is null, "Load has no warning for merely-missing keys (not a schema violation)");

    // Codex 指摘 Medium 3: キーはあるが値の型が文字列でない(スキーマ違反)。
    File.WriteAllText(settingsPath, "{\"output.flatSuffix\":42}");
    var (afterBadType, badTypeWarning) = SettingsStore.Load();
    Check(afterBadType.FlatSuffix == AkapenSettings.Defaults.FlatSuffix,
        "Load falls back to default for a non-string 'output.flatSuffix' value");
    Check(badTypeWarning is not null && badTypeWarning.Contains("output.flatSuffix") && badTypeWarning.Contains("non-string"),
        $"Load warns about the schema violation by key name (got: {badTypeWarning})");

    File.WriteAllText(settingsPath, "{\"output.subfolderName\":true}");
    var (_, boolWarning) = SettingsStore.Load();
    Check(boolWarning is not null && boolWarning.Contains("output.subfolderName"),
        $"Load warns for a boolean value in a string field (got: {boolWarning})");

    File.WriteAllText(settingsPath, "{\"output.flatSuffix\":42,\"output.strokesSuffix\":null}");
    var (_, multiWarning) = SettingsStore.Load();
    Check(multiWarning is not null
        && multiWarning.Contains("output.flatSuffix")
        && multiWarning.Contains("output.strokesSuffix"),
        $"Load warns about every schema-violating key, not just the first (got: {multiWarning})");

    // Codex 指摘 4巡目 Medium 2: スキーマ違反試験に Array/Object が無かった
    // (Number/Bool/Null は既にあったが Array/Object の非文字列値は未試験)。
    File.WriteAllText(settingsPath, "{\"output.flatSuffix\":[]}");
    var (afterArrayValue, arrayValueWarning) = SettingsStore.Load();
    Check(afterArrayValue.FlatSuffix == AkapenSettings.Defaults.FlatSuffix,
        "Load falls back to default for an array 'output.flatSuffix' value");
    Check(arrayValueWarning is not null && arrayValueWarning.Contains("output.flatSuffix"),
        $"Load warns about an array-valued schema violation (got: {arrayValueWarning})");

    File.WriteAllText(settingsPath, "{\"output.flatSuffix\":{}}");
    var (afterObjectValue, objectValueWarning) = SettingsStore.Load();
    Check(afterObjectValue.FlatSuffix == AkapenSettings.Defaults.FlatSuffix,
        "Load falls back to default for an object 'output.flatSuffix' value");
    Check(objectValueWarning is not null && objectValueWarning.Contains("output.flatSuffix"),
        $"Load warns about an object-valued schema violation (got: {objectValueWarning})");

    // Load IOException 経路(Codex 指摘 4巡目 Medium 2): 同一プロセス内で
    // FileShare.None のハンドルを保持したまま Load() を呼ぶと、共有違反により
    // File.ReadAllText が IOException を投げる(確認済み: Windows のネイティブ
    // 共有違反と同じ例外型で、.NET ランタイム側は OS 非依存にこの型へ正規化
    // する)。File.Exists 自体は true のままなので、「ファイルが無い」(無警告)
    // とは別の catch(IOException) 分岐を通ることを確認する。
    File.WriteAllText(settingsPath, "{}");
    using (var lockedStream = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var (afterLocked, lockedWarning) = SettingsStore.Load();
        Check(afterLocked == AkapenSettings.Defaults,
            "Load falls back to defaults when the settings file is exclusively locked (IOException path)");
        Check(lockedWarning is not null,
            "Load warns when the settings file is exclusively locked (IOException path)");
    }

    // Load UnauthorizedAccessException 経路(Codex 指摘 4巡目 Medium 2):
    // ファイル自体の読み取り権限を落とす(親ディレクトリの権限はそのまま)。
    // chmod は Unix のみ有効な API なので、下の Save I/O 失敗ケースと同様
    // Windows 実行時はこのサブケースだけ skip する。
    if (!OperatingSystem.IsWindows())
    {
        File.WriteAllText(settingsPath, "{}");
        UnixFileMode originalFileMode = File.GetUnixFileMode(settingsPath);
        try
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.None);
            var (afterNoPerm, noPermWarning) = SettingsStore.Load();
            Check(afterNoPerm == AkapenSettings.Defaults,
                "Load falls back to defaults when the settings file is unreadable (UnauthorizedAccessException path)");
            Check(noPermWarning is not null,
                "Load warns when the settings file is unreadable (UnauthorizedAccessException path)");
        }
        finally
        {
            File.SetUnixFileMode(settingsPath, originalFileMode);
        }
    }
    else
    {
        Console.WriteLine("SKIP: Load UnauthorizedAccessException case (chmod-based) only runs on Unix hosts.");
    }

    // Save I/O 失敗ケース: 設定ファイルを消した上で親ディレクトリの書き込み
    // 権限を落とし、再作成できないようにする(chmod は Unix のみ有効な API
    // なので Windows 実行時はこのサブケースだけ skip する — CI は
    // ubuntu-latest で走らせるため通常は実行される)。
    if (!OperatingSystem.IsWindows())
    {
        File.Delete(settingsPath);
        string parentDir = Path.GetDirectoryName(settingsPath)!;
        UnixFileMode original = File.GetUnixFileMode(parentDir);
        try
        {
            File.SetUnixFileMode(parentDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            bool saveResult = SettingsStore.Save(AkapenSettings.Defaults);
            Check(saveResult == false, "Save returns false when the settings directory is not writable");
        }
        finally
        {
            File.SetUnixFileMode(parentDir, original);
        }
    }
    else
    {
        Console.WriteLine("SKIP: Save I/O-failure case (chmod-based) only runs on Unix hosts.");
    }
}
finally
{
    SettingsStore.SettingsFilePath = originalSettingsFilePath;
    Directory.Delete(testHome, recursive: true);
}

Console.WriteLine($"{total - failed}/{total} assertions passed.");
if (failed > 0)
{
    Console.WriteLine($"{failed} assertion(s) FAILED.");
    return 1;
}
return 0;
