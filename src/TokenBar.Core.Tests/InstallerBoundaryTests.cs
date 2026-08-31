using Syrtis.Installer;

namespace TokenBar.Core.Tests;

// Covers the installer's boundary logic that is honestly testable from a net10
// suite that also runs on macOS: SetupRunner.Quote (pure string), Install-
// Request.Parse (pure argument parsing), SetupRunner.LogWrittenByThisRun
// (FileInfo + timestamps, cross-platform), and Strings.DetectChinese (a pure
// tag -> bool function).
//
// Deliberately NOT covered here: InstallPath.IsValidInstallPath. Its
// Path.IsPathRooted/GetPathRoot/GetFullPath semantics differ between net48
// (throws on invalid characters, measured on 4.8.9337.0) and .NET Core
// (does not throw), and this suite also runs on macOS, where
// Path.IsPathRooted("C:\") is false regardless of framework. A test against it
// here would be green for the wrong reason or red for the wrong reason. That
// surface is exercised instead by `Syrtis.Installer.exe --self-check` on the
// framework that actually ships. See TokenBar.Core.Tests.csproj for the same
// note next to the <Compile Include> list.
//
// No test in this file may read a Strings member whose value depends on
// ambient culture. Only Strings.DetectChinese(tag) — which takes the tag as a
// parameter rather than reading CultureInfo.CurrentUICulture — is referenced.
public class InstallerBoundaryTests
{
    // --- SetupRunner.Quote ---------------------------------------------------
    //
    // CommandLineToArgvW is Windows-only, so these assert the produced string
    // directly (surrounding quotes, and only the trailing run of backslashes
    // doubled) rather than round-tripping through an actual argv parser.

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Install Here\")]
    [InlineData(@"C:\SyrtisTest\Syrtis")]
    [InlineData(@"\\srv\share\")]
    [InlineData(@"C:\a\b\\")]
    public void Quote_survives_round_trip(string value)
    {
        var quoted = SetupRunner.Quote(value);

        // Built independently of SetupRunner.Quote's own algorithm, so this
        // is not tautological: only the run of trailing backslashes is
        // doubled, everything before it is untouched.
        var trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }

        var expected = "\"" + value + new string('\\', trailing) + "\"";

        Assert.Equal(expected, quoted);
        Assert.StartsWith("\"", quoted);
        Assert.EndsWith("\"", quoted);
    }

    // --- SetupRunner.LogWrittenByThisRun --------------------------------------

    [Fact]
    public void LogWrittenByThisRun_absent_file_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Assert.Null(SetupRunner.LogWrittenByThisRun(path, DateTime.UtcNow));
    }

    [Fact]
    public void LogWrittenByThisRun_two_days_stale_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "stale");
        try
        {
            var start = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(path, start.AddDays(-2));

            Assert.Null(SetupRunner.LogWrittenByThisRun(path, start));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LogWrittenByThisRun_one_second_before_start_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "just before");
        try
        {
            var start = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(path, start.AddSeconds(-1));

            Assert.Null(SetupRunner.LogWrittenByThisRun(path, start));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LogWrittenByThisRun_written_after_start_returns_path()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var start = DateTime.UtcNow;
        File.WriteAllText(path, "fresh");
        try
        {
            File.SetLastWriteTimeUtc(path, start.AddSeconds(1));

            Assert.Equal(path, SetupRunner.LogWrittenByThisRun(path, start));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- InstallRequest.Parse -------------------------------------------------

    [Fact]
    public void Parse_self_check_alone_is_self_check_mode()
    {
        var request = InstallRequest.Parse(["--self-check"]);

        Assert.Equal(InstallRequestKind.SelfCheck, request.Kind);
    }

    [Fact]
    public void Parse_self_check_with_anything_else_is_a_refusal()
    {
        var request = InstallRequest.Parse(["--self-check", "extra"]);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.UnexpectedArgument, request.Reason);
        Assert.Equal("extra", request.Token);
    }

    [Theory]
    [InlineData("-s")]
    [InlineData("--silent")]
    [InlineData("-S")]
    public void Parse_recognises_every_silent_switch_spelling(string silentSwitch)
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([silentSwitch, setupPath]);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    [Fact]
    public void Parse_silent_switch_before_path_works()
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse(["-s", setupPath]);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
            Assert.Equal(Path.GetFullPath(setupPath), request.SetupPath);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    [Fact]
    public void Parse_silent_switch_after_path_works()
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath, "-s"]);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
            Assert.Equal(Path.GetFullPath(setupPath), request.SetupPath);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    [Fact]
    public void Parse_second_non_switch_argument_is_unexpected()
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath, "extra-token"]);

            Assert.Equal(InstallRequestKind.Refuse, request.Kind);
            Assert.Equal(RefusalReason.UnexpectedArgument, request.Reason);
            Assert.Equal("extra-token", request.Token);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    [Fact]
    public void Parse_no_path_at_all_is_no_setup_path()
    {
        var request = InstallRequest.Parse([]);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.NoSetupPath, request.Reason);
    }

    [Fact]
    public void Parse_no_path_with_only_silent_switch_is_no_setup_path()
    {
        var request = InstallRequest.Parse(["--silent"]);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.NoSetupPath, request.Reason);
    }

    [Fact]
    public void Parse_missing_setup_file_is_setup_not_found()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var request = InstallRequest.Parse([missing]);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.SetupNotFound, request.Reason);
        Assert.Equal(missing, request.Token);
    }

    [Fact]
    public void Parse_existing_path_without_silent_switch_is_run_wizard()
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath]);

            Assert.Equal(InstallRequestKind.RunWizard, request.Kind);
            Assert.Equal(Path.GetFullPath(setupPath), request.SetupPath);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "setup");
        return path;
    }

    // --- Strings.DetectChinese -------------------------------------------------

    [Theory]
    [InlineData("zh-Hant", true)]
    [InlineData("zh-TW", true)]
    [InlineData("zh-HK", true)]
    [InlineData("zh-MO", true)]
    [InlineData("en-US", false)]
    [InlineData("ja-JP", false)]
    [InlineData("zh", false)]
    public void DetectChinese_matches_current_implementation(string tag, bool expected)
    {
        Assert.Equal(expected, Strings.DetectChinese(tag));
    }
}
