using System.Globalization;

namespace Syrtis.Installer;

/// <summary>Minimal bilingual string table for the installer wizard. This
/// project is net48 and cannot reference TokenBar.Core's i18n (net10), so it
/// carries its own: every user-visible string as an English/Chinese pair,
/// picked once from <see cref="CultureInfo.CurrentUICulture"/>. No resx, no
/// satellite assemblies — mirrors the zh-Hant/zh-TW/zh-HK/zh-MO detection in
/// TokenBar.App's AppLanguage.Resolve.</summary>
internal static class Strings
{
    private static readonly bool IsChinese = DetectChinese(CultureInfo.CurrentUICulture.Name);

    internal static bool DetectChinese(string tag) =>
        tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase);

    private static string T(string en, string zh) => IsChinese ? zh : en;

    // Command-line / no-UI errors.
    internal static string ErrorNoSetupPathArg => T(
        "Usage: Syrtis.Installer.exe <path-to-setup.exe>",
        "用法：Syrtis.Installer.exe <setup.exe 的路徑>");

    internal static string ErrorSetupNotFound(string path) => T(
        $"Setup file not found: {path}",
        $"找不到安裝檔：{path}");

    internal static string ErrorUnexpectedArg(string arg) => T(
        $"Unrecognised argument: {arg}\nUsage: Syrtis.Installer.exe [-s|--silent] <path-to-setup.exe>",
        $"無法辨識的參數：{arg}\n用法：Syrtis.Installer.exe [-s|--silent] <setup.exe 的路徑>");

    /// <summary>A switch this wrapper does not support. Named separately from
    /// ErrorUnexpectedArg because after slice 1b this runs under Setup.exe's
    /// own filename, so the script that hits this is likely carrying Setup's
    /// options — and the useful thing to say is which switch was dropped, not
    /// that some token was surplus.</summary>
    internal static string ErrorUnsupportedSwitch(string arg) => T(
        $"This installer does not support {arg}.\nUsage: Syrtis.Installer.exe [-s|--silent] <path-to-setup.exe>",
        $"此安裝程式不支援 {arg}。\n用法：Syrtis.Installer.exe [-s|--silent] <setup.exe 的路徑>");

    internal static string ErrorSetupLaunchFailed(string path, string reason) => T(
        $"Could not start the setup file: {path}\n{reason}",
        $"無法啟動安裝檔：{path}\n{reason}");

    // Window chrome.
    internal static string WindowTitle => T("Syrtis Setup", "Syrtis 安裝精靈");

    // Buttons.
    internal static string ButtonBack => T("< Back", "< 上一步");
    internal static string ButtonNext => T("Next >", "下一步 >");
    internal static string ButtonCancel => T("Cancel", "取消");
    internal static string ButtonFinish => T("Finish", "完成");
    internal static string ButtonBrowse => T("Browse…", "瀏覽…");

    // Page 1: Welcome.
    internal static string WelcomeHeading(string productName) => T(
        $"Welcome to {productName} Setup",
        $"歡迎使用 {productName} 安裝精靈");
    internal static string WelcomeBody(string productName, string version) => T(
        $"This wizard will install {productName} version {version} on your computer.\n\nClick Next to continue.",
        $"此精靈將在您的電腦上安裝 {productName} 版本 {version}。\n\n按下一步繼續。");

    // Page 2: Location.
    internal static string LocationHeading => T("Choose Install Location", "選擇安裝位置");
    internal static string LocationBody => T(
        "Setup will install Syrtis to the following folder. To install to this folder, click Next. To install to a different folder, click Browse.",
        "安裝程式將把 Syrtis 安裝到下列資料夾。若要安裝到此資料夾，請按下一步；若要選擇其他資料夾，請按瀏覽。");
    internal static string LocationPerUserNotice => T(
        "This installs for the current user only and does not require administrator rights.",
        "此安裝僅適用於目前使用者，不需要系統管理員權限。");
    internal static string LocationInvalidPath => T(
        "Enter a valid absolute folder path.",
        "請輸入有效的絕對資料夾路徑。");
    internal static string BrowseDialogDescription => T(
        "Select the folder to install Syrtis into.",
        "選擇要安裝 Syrtis 的資料夾。");

    // Page 3: Installing.
    internal static string InstallingHeading => T("Installing", "安裝中");
    internal static string InstallingStatus => T(
        "Please wait while Syrtis is being installed…",
        "請稍候，Syrtis 正在安裝中…");

    // Page 4: Done.
    internal static string DoneHeadingSuccess => T("Setup Complete", "安裝完成");
    internal static string DoneBodySuccess(string productName) => T(
        $"{productName} has been installed on your computer.",
        $"{productName} 已成功安裝到您的電腦。");
    internal static string DoneLaunchCheckbox(string productName) => T(
        $"Launch {productName}", $"啟動 {productName}");
    internal static string DoneHeadingFailure => T("Setup Failed", "安裝失敗");
    internal static string DoneBodyFailure(string productName, int exitCode, string logPath) => T(
        $"{productName} installation failed (exit code {exitCode}).\n\nSee the log file for details:\n{logPath}",
        $"{productName} 安裝失敗（結束代碼 {exitCode}）。\n\n詳情請參閱記錄檔：\n{logPath}");

    /// <summary>Setup ran and failed, but wrote no log. Says so rather than
    /// naming a path that is absent or left over from an unrelated run.</summary>
    internal static string DoneBodyFailureNoLog(string productName, int exitCode) => T(
        $"{productName} installation failed (exit code {exitCode}).\n\nNo log file was written.",
        $"{productName} 安裝失敗（結束代碼 {exitCode}）。\n\n沒有產生記錄檔。");

    /// <summary>Setup could not be started at all. There is no exit code worth
    /// showing and no log, so this shows the reason instead.</summary>
    internal static string DoneBodyLaunchFailed(string productName, string reason) => T(
        $"The {productName} setup file could not be started.\n\n{reason}",
        $"無法啟動 {productName} 的安裝檔。\n\n{reason}");
}
