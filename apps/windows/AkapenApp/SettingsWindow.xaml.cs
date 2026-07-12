// 出力先・接尾辞のユーザー設定ウィンドウ code-behind(spec §4.7 / §9 M2 — M2-E)。
//
// 責務は mac シェルの SettingsView.swift(apps/mac/Sources/AkapenApp/SettingsView.swift)
// と揃える:
//   - 5 個の設定フィールド(dirMode / subfolderName / fixedDir / flatSuffix /
//     strokesSuffix)を SettingsStore の JSON から読み、UI に反映する。
//   - 各 TextBox の TextChanged で SettingsStore の IsValid* を呼び、視覚的な
//     フラグ(BorderBrush 赤 + ヘルプ TextBlock 赤)と即時書き出しを行う。
//   - 「Browse…」ボタン(fixedAbsolute モード時のみ表示)は WinRT の
//     FolderPicker を InitializeWithWindow で HWND に紐付けて開く。unpackaged
//     WinUI 3 の picker は明示的な HWND 初期化が無いと NoWindow で throw する
//     (MainWindow の FileOpenPicker と同じ既知の quirk)。
//   - 値の永続化は「即時書き出し」— UI 変更が起きるたびに JSON を書く。mac の
//     @AppStorage は変更の瞬間に UserDefaults へ書くので、その挙動を写像した。
//     Apply/OK/Cancel の三段構えは持たない(mac 側にも無い)。
//
// modal vs top-level: WinUI 3 は WPF/UWP の「ShowDialog」相当を Window に
// 持たないので、素直に独立トップレベル Window として起動する。MainWindow から
// は _settingsWindow フィールドで「開いてれば Activate、無ければ new + Show」
// のシンプルな Show/Focus パターンで扱う(MainWindow 側 EnsureSettingsWindow)。
//
// 変更検知: MainWindow の TrySave は毎回 SettingsStore.Load() で最新値を読む。
// SettingsWindow → MainWindow の直接の event/observer は敷かない(mac の
// AppState.save も同じで、UserDefaults.standard.string を毎回読み直す)。この
// シンプルさは、設定の変更頻度が保存の頻度に比べて十分低いから成立する — 保存
// のたびに JSON を open/read/parse するコストは、保存の PNG エンコード+ディ
// スク書きに埋もれる。

using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace AkapenApp;

public sealed partial class SettingsWindow : Window
{
    // UI からの生の入力を保持する。SettingsStore.AkapenSettings は immutable
    // record なので、更新のたびに `with` で作り直して即時保存する。
    private AkapenSettings _settings = AkapenSettings.Defaults;

    // 初期反映中の TextChanged 抑止フラグ:UI に値を書き戻すループで
    // TextChanged が発火 → OnXxxChanged が再書き込み、を防ぐ。
    private bool _suppressChangeHandlers;

    // 「有効入力に戻ったときの BorderBrush / BorderThickness」は、コンストラク
    // タ時点(まだ Loaded 前)ではテーマリソースが解決されていないことがあり、
    // 生の BorderBrush を控えても null が返る場合がある。DP を ClearValue で
    // 元に戻す方が確実で、テーマ切替にも追従する。ここでは「エラー表示用の
    // 明示ブラシ」だけを持ち、既定への復帰は ClearValue(TextBox.
    // BorderBrushProperty) 経由で行う。
    private static readonly SolidColorBrush ErrorBorderBrush = new(Colors.Red);
    private static readonly SolidColorBrush ErrorForegroundBrush = new(Colors.Red);
    private static readonly SolidColorBrush HelpForegroundBrush = new(Colors.Gray);

    public SettingsWindow()
    {
        this.InitializeComponent();
        // Title は XAML でも設定済み。念のためコード側でも保つ(WinUI 3 の
        // Window Title は XAML 属性が先勝ちで問題無し)。
        this.Title = "Akapen — Settings";

        // 最初に JSON を読み、UI に流し込む。読み書きは全て SettingsStore を
        // 通し、mac の @AppStorage キー体系との 1 対 1 対応を維持する。
        // Load が warning(JSON 破損・I/O 失敗)を返したら SaveStatusText に
        // 表示する — 以降の最初の編集が成功保存すれば ShowSettingsMessage(null)
        // で自然に消える(Codex 指摘 Medium 1)。
        var (settings, loadWarning) = SettingsStore.Load();
        _settings = settings;
        ApplySettingsToUI();
        ShowSettingsMessage(loadWarning);
    }

    /// <summary>
    /// 読み込んだ設定(または直近保存済みの設定)を UI コントロールへ書き戻す。
    /// TextChanged ハンドラを再入させないため、_suppressChangeHandlers を立てる。
    /// </summary>
    private void ApplySettingsToUI()
    {
        _suppressChangeHandlers = true;
        try
        {
            // ラジオ:BesideInput / FixedAbsolute。Checked ハンドラ経由で
            // Subfolder/FixedDir パネルの Visibility も更新される。
            if (_settings.DirMode == AkapenOutputDirMode.FixedAbsolute)
            {
                FixedAbsoluteRadio.IsChecked = true;
            }
            else
            {
                BesideInputRadio.IsChecked = true;
            }

            SubfolderNameBox.Text = _settings.SubfolderName;
            FixedDirBox.Text = _settings.FixedDir;
            FlatSuffixBox.Text = _settings.FlatSuffix;
            StrokesSuffixBox.Text = _settings.StrokesSuffix;

            // ヘルプ / エラーテキストを初期化。パネル可視性も radio に追従。
            UpdateSubfolderValidation();
            UpdateFixedDirValidation();
            UpdateSuffixValidation(FlatSuffixBox, FlatSuffixHelpText,
                _settings.FlatSuffix, AkapenSettings.Defaults.FlatSuffix, isFlat: true);
            UpdateSuffixValidation(StrokesSuffixBox, StrokesSuffixHelpText,
                _settings.StrokesSuffix, AkapenSettings.Defaults.StrokesSuffix, isFlat: false);
            UpdateOutputDirPanelsVisibility();
        }
        finally
        {
            _suppressChangeHandlers = false;
        }
    }

    // ── DirMode(RadioButtons)──────────────────────────────────────────

    private void OnOutputDirModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeHandlers) return;
        var mode = FixedAbsoluteRadio.IsChecked == true
            ? AkapenOutputDirMode.FixedAbsolute
            : AkapenOutputDirMode.BesideInput;
        _settings = _settings with { DirMode = mode };
        UpdateOutputDirPanelsVisibility();
        // モード切替そのものは値検証には影響しないが、可視パネル側のヘルプは
        // 再計算する(空/無効時のフォールバック案内を出しなおす)。
        UpdateSubfolderValidation();
        UpdateFixedDirValidation();
        ShowSettingsMessage(SettingsStore.Save(_settings) ? null : SaveFailedMessage);
    }

    private void UpdateOutputDirPanelsVisibility()
    {
        bool beside = _settings.DirMode == AkapenOutputDirMode.BesideInput;
        SubfolderPanel.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        FixedDirPanel.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── SubfolderName ──────────────────────────────────────────────────

    private void OnSubfolderNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChangeHandlers) return;
        _settings = _settings with { SubfolderName = SubfolderNameBox.Text };
        UpdateSubfolderValidation();
        ShowSettingsMessage(SettingsStore.Save(_settings) ? null : SaveFailedMessage);
    }

    private void UpdateSubfolderValidation()
    {
        string raw = _settings.SubfolderName;
        bool valid = SettingsStore.IsValidSubfolder(raw);
        ApplyBorderValidation(SubfolderNameBox, valid);
        if (valid)
        {
            string effective = raw.Trim();
            SubfolderHelpText.Foreground = HelpForegroundBrush;
            SubfolderHelpText.Text =
                $"書き出し先は <入力ファイルのあるフォルダ>/{effective}/ になります。";
        }
        else
        {
            SubfolderHelpText.Foreground = ErrorForegroundBrush;
            // Codex 指摘 Medium 1: 予約デバイス名・制御文字は「パス区切りが
            // 使えません」だけでは理由が伝わらないため、該当時は専用の文言を
            // 出す(mac には無い Windows 固有の制約なので原因を明示する)。
            if (SettingsStore.ContainsControlChar(raw))
            {
                SubfolderHelpText.Text =
                    $"制御文字を含む名前は使えません。空欄のときは既定の \"{AkapenSettings.Defaults.SubfolderName}\" を使います。";
            }
            else if (SettingsStore.IsReservedDeviceName(raw.Trim()))
            {
                SubfolderHelpText.Text =
                    $"\"{raw.Trim()}\" は Windows の予約デバイス名(reserved Windows device name)のため使えません。空欄のときは既定の \"{AkapenSettings.Defaults.SubfolderName}\" を使います。";
            }
            else
            {
                SubfolderHelpText.Text =
                    $"パス区切り(/ や \\)や \".\" / \"..\" は使えません。空欄のときは既定の \"{AkapenSettings.Defaults.SubfolderName}\" を使います。";
            }
        }
    }

    // ── FixedDir + Browse ─────────────────────────────────────────────

    private void OnFixedDirChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChangeHandlers) return;
        _settings = _settings with { FixedDir = FixedDirBox.Text };
        UpdateFixedDirValidation();
        ShowSettingsMessage(SettingsStore.Save(_settings) ? null : SaveFailedMessage);
    }

    private void UpdateFixedDirValidation()
    {
        string raw = _settings.FixedDir;
        bool valid = SettingsStore.IsValidFixedDir(raw);
        ApplyBorderValidation(FixedDirBox, valid);
        if (valid)
        {
            FixedDirHelpText.Foreground = HelpForegroundBrush;
            FixedDirHelpText.Text = "全案件で共通の review 置き場として使います。";
        }
        else
        {
            FixedDirHelpText.Foreground = ErrorForegroundBrush;
            FixedDirHelpText.Text =
                $"存在する絶対パスを指定してください。無効な指定は保存時に入力フォルダ相対の \"{AkapenSettings.Defaults.SubfolderName}\" にフォールバックします。";
        }
    }

    private async void OnBrowseFixedDirClick(object sender, RoutedEventArgs e)
    {
        // unpackaged WinUI 3 の FolderPicker は MainWindow の FileOpenPicker と
        // 同じく WinRT.Interop.InitializeWithWindow による HWND 初期化が必須。
        // 未初期化のまま PickSingleFolderAsync すると NoWindow で throw する。
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        // FolderPicker.PickSingleFolderAsync のドキュメントは
        // FileTypeFilter に何か 1 個入れることを要求する(空だと COM で失敗する
        // 既知の事情)。"*" を入れれば全ファイル表示 = 全フォルダ選択できる。
        picker.FileTypeFilter.Add("*");

        StorageFolder? folder;
        try
        {
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception)
        {
            // ピッカが失敗しても設定ウィンドウ全体は壊さない。ユーザーは
            // TextBox に直接手入力できる。
            return;
        }
        if (folder is null) return;

        // TextBox に反映すれば TextChanged 経由で _settings と JSON まで一気通貫。
        FixedDirBox.Text = folder.Path;
    }

    // ── Suffix(Flat / Strokes)────────────────────────────────────────

    private void OnFlatSuffixChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChangeHandlers) return;
        _settings = _settings with { FlatSuffix = FlatSuffixBox.Text };
        UpdateSuffixValidation(FlatSuffixBox, FlatSuffixHelpText,
            _settings.FlatSuffix, AkapenSettings.Defaults.FlatSuffix, isFlat: true);
        ShowSettingsMessage(SettingsStore.Save(_settings) ? null : SaveFailedMessage);
    }

    private void OnStrokesSuffixChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChangeHandlers) return;
        _settings = _settings with { StrokesSuffix = StrokesSuffixBox.Text };
        UpdateSuffixValidation(StrokesSuffixBox, StrokesSuffixHelpText,
            _settings.StrokesSuffix, AkapenSettings.Defaults.StrokesSuffix, isFlat: false);
        ShowSettingsMessage(SettingsStore.Save(_settings) ? null : SaveFailedMessage);
    }

    private void UpdateSuffixValidation(
        TextBox box, TextBlock help, string raw, string defaultValue, bool isFlat)
    {
        bool valid = SettingsStore.IsValidSuffix(raw);
        ApplyBorderValidation(box, valid);
        if (valid)
        {
            help.Foreground = HelpForegroundBrush;
            string effective = raw.Trim();
            string sample = isFlat
                ? $"<stem>.{effective}.png"
                : $"<stem>.{effective}.png / .json";
            help.Text = $"例: {sample}";
        }
        else
        {
            help.Foreground = ErrorForegroundBrush;
            // Codex 指摘 Medium 1: 予約デバイス名・制御文字は理由を明示する
            // (SubfolderHelpText と同じ方針)。
            string trimmedRaw = raw.Trim();
            if (SettingsStore.ContainsControlChar(raw))
            {
                help.Text =
                    $"制御文字を含む接尾辞は使えません。無効な入力は既定 \"{defaultValue}\" に戻ります。";
            }
            else if (trimmedRaw.Length > 0 && SettingsStore.IsReservedDeviceName(trimmedRaw))
            {
                help.Text =
                    $"\"{trimmedRaw}\" は Windows の予約デバイス名(reserved Windows device name)のため使えません。無効な入力は既定 \"{defaultValue}\" に戻ります。";
            }
            else
            {
                help.Text =
                    $"パス区切り(/ \\)やドット(.)を含めない、空でない文字列を指定してください。無効な入力は既定 \"{defaultValue}\" に戻ります。";
            }
        }
    }

    // ── 保存/読込結果の表示(Codex 指摘 Medium 1)────────────────────────
    //
    // SettingsStore.Save は I/O 失敗を bool で返すが、これまで戻り値を捨てて
    // いたため保存失敗が完全に無視されていた(UI は「保存できた」体裁のまま
    // 実際にはディスクへ反映されず、MainWindow.TrySave は毎回 Load し直すので
    // 古い/既定値のまま export される)。SaveStatusText に具体的な path 込みの
    // 警告を出し、次の保存が成功すれば自動的に消す。

    private static string SaveFailedMessage =>
        $"設定を保存できませんでした({SettingsStore.SettingsFilePath} に書き込めません)。この変更は反映されていません。";

    /// <summary>
    /// SaveStatusText に警告(保存失敗 / 読み込み時の破損・I/O 警告)を出す。
    /// <paramref name="text"/> が null/空なら非表示に戻す — 直後の編集操作で
    /// 保存が成功すればここが呼ばれて自然に消える設計。
    /// </summary>
    private void ShowSettingsMessage(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            SaveStatusText.Text = string.Empty;
            SaveStatusText.Visibility = Visibility.Collapsed;
            return;
        }
        SaveStatusText.Foreground = ErrorForegroundBrush;
        SaveStatusText.Text = text;
        SaveStatusText.Visibility = Visibility.Visible;
    }

    // ── 内部ヘルパ ────────────────────────────────────────────────────

    /// <summary>
    /// TextBox の BorderBrush / BorderThickness を「有効なら DP 既定に戻す
    /// (ClearValue)、無効なら赤 2 px の明示ブラシに切替」で更新する。
    /// ClearValue は DP の local value を落として ResourceDictionary 側の
    /// テーマ既定に戻すので、コンストラクタ時点で BorderBrush が null でも
    /// 有効入力に戻したときにちゃんとテーマの縁色が復活する(自前スナップ
    /// ショット方式より確実で、テーマ切替にも追従する)。
    /// </summary>
    private static void ApplyBorderValidation(TextBox box, bool valid)
    {
        if (valid)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ClearValue(Control.BorderThicknessProperty);
        }
        else
        {
            box.BorderBrush = ErrorBorderBrush;
            box.BorderThickness = new Thickness(2);
        }
    }
}
