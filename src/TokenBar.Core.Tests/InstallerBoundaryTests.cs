using Syrtis.Installer;

namespace TokenBar.Core.Tests;

// Covers the installer's boundary logic that is honestly testable from a net10
// suite that also runs on macOS: SetupRunner.Quote (pure string), Install-
// Request.Parse (pure argument parsing), SetupRunner.WrittenLog
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
//
// InstallRequest.Parse takes "does a payload exist" as a bool parameter
// rather than reading EmbeddedPayload.HasPayload ambiently, precisely so this
// suite can cover both worlds — every InlineData/Theory pair marked "payload
// world" below asserts against a literal true/false, not against this
// assembly's own resources (which never carries the payload). That makes
// `dotnet test` green here necessary but NOT evidence that the shipping
// wrapper's payload is present, correct, or the one this pack run produced:
// EmbeddedPayload.ExtractToTemp() and the resource itself are proven only on
// the built artifact, by loading it with Assembly.LoadFrom and comparing its
// SHA-256 against the Velopack Setup from the same pack invocation (see
// package-velopack.ps1's post-pack verification step).
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

    // --- PayloadIdentity.Choose ------------------------------------------------

    // The Welcome page's sentence — "will install {product} version {version}"
    // — is entirely about the payload, and both halves used to be read from
    // this wrapper's own assembly. In slice 1a the setup file is whatever path
    // was passed in, so the repo version stamped on the wrapper says nothing
    // about it; the two matched during acceptance only because the same bumped
    // tree produced both.
    [Fact]
    public void Choose_prefers_the_payload()
    {
        Assert.Equal("0.2.1", PayloadIdentity.Choose("0.2.1", "0.2.2", "?"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Choose_falls_back_to_the_wrapper_when_the_payload_is_silent(string? payload)
    {
        Assert.Equal("0.2.2", PayloadIdentity.Choose(payload, "0.2.2", "?"));
    }

    [Fact]
    public void Choose_falls_back_to_the_constant_when_neither_answers()
    {
        Assert.Equal("?", PayloadIdentity.Choose(null, "  ", "?"));
    }

    // A blank version resource is ordinary in stub executables, and rendering
    // it would put "version  " on screen, which reads as a bug rather than as
    // a missing value.
    [Fact]
    public void Choose_trims_what_it_returns()
    {
        Assert.Equal("Syrtis", PayloadIdentity.Choose("  Syrtis  ", null, "?"));
    }

    // --- SetupRunner.NewLogPath -----------------------------------------------

    // The log path used to be one fixed name, and two defects came out of that:
    // a leftover from an earlier install passed an existence check, and two
    // overlapping wrapper instances wrote the same file, where a timestamp
    // proves "written after I started" but never "written by my child". No
    // inference separates those; a name that cannot collide does.
    [Fact]
    public void NewLogPath_differs_between_runs()
    {
        var first = SetupRunner.NewLogPath();
        var second = SetupRunner.NewLogPath();

        // Same second, same process: whatever makes these differ has to be
        // more than the timestamp, or two instances started together collide
        // exactly when it matters.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewLogPath_is_under_temp_and_recognisable()
    {
        var path = SetupRunner.NewLogPath();

        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar);
        Assert.StartsWith("syrtis-install-", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.EndsWith(".log", path, StringComparison.Ordinal);
    }

    // --- SetupRunner.WrittenLog -----------------------------------------------

    // This used to compare the file's last write against the moment the run
    // started, to tell a leftover from a fresh log when every run shared one
    // filename. NewLogPath removed that need, but the comparison was kept as a
    // second line of defence — and against a name that cannot collide it
    // carries no information and can still be false: %TEMP% on FAT or exFAT
    // records write times to two-second granularity, and the clock can step
    // backwards mid-install. Either would have made the Done page claim no log
    // exists while the log explaining the failure sat right there.

    [Fact]
    public void WrittenLog_absent_file_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Assert.Null(SetupRunner.WrittenLog(path));
    }

    [Fact]
    public void WrittenLog_present_file_is_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "written");
        try
        {
            Assert.Equal(path, SetupRunner.WrittenLog(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The false negative the timestamp comparison could produce, pinned from
    // the other side: a log whose stamp predates the run is still this run's
    // log, because no other run could have been given this name.
    [Fact]
    public void WrittenLog_ignores_the_timestamp_entirely()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "written, but with an older stamp");
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

            Assert.Equal(path, SetupRunner.WrittenLog(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- InstallRequest.Parse -------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_self_check_alone_is_self_check_mode(bool hasEmbeddedPayload)
    {
        var request = InstallRequest.Parse(["--self-check"], hasEmbeddedPayload);

        Assert.Equal(InstallRequestKind.SelfCheck, request.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_self_check_with_anything_else_is_a_refusal(bool hasEmbeddedPayload)
    {
        var request = InstallRequest.Parse(["--self-check", "extra"], hasEmbeddedPayload);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.UnexpectedArgument, request.Reason);
        Assert.Equal("extra", request.Token);
    }

    // The refusal path exists to protect the case where slice 1b puts this
    // under Setup.exe's own filename and a script arrives carrying Setup's own
    // options. It used to refuse those correctly but name the wrong token:
    // "--installto D:\X" reported "D:\X" as the surplus argument, because both
    // tokens are non-switches to a parser that only knows -s. A lone "-v" was
    // reported as a missing setup file.
    [Theory]
    [InlineData(new[] { "--installto", "D:\\X" }, "--installto", false)]
    [InlineData(new[] { "-v" }, "-v", false)]
    [InlineData(new[] { "-l", "C:\\some.log" }, "-l", false)]
    [InlineData(new[] { "--silent", "--installto", "D:\\X" }, "--installto", false)]
    [InlineData(new[] { "setup.exe", "-t", "D:\\X" }, "-t", false)]
    [InlineData(new[] { "--installto", "D:\\X" }, "--installto", true)]
    [InlineData(new[] { "-v" }, "-v", true)]
    [InlineData(new[] { "-l", "C:\\some.log" }, "-l", true)]
    [InlineData(new[] { "--silent", "--installto", "D:\\X" }, "--installto", true)]
    [InlineData(new[] { "setup.exe", "-t", "D:\\X" }, "-t", true)]
    public void Parse_names_the_unsupported_switch_not_its_value(string[] args, string expected, bool hasEmbeddedPayload)
    {
        var request = InstallRequest.Parse(args, hasEmbeddedPayload);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.UnsupportedSwitch, request.Reason);
        Assert.Equal(expected, request.Token);
    }

    // A Windows path never begins with '-', so the switch test is unambiguous;
    // '/x' is a rooted path and must NOT be mistaken for a switch. An explicit
    // path that does not exist is still SetupNotFound in both worlds — it is
    // never silently replaced by the payload (precedence table row 3).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_does_not_treat_a_slash_rooted_path_as_a_switch(bool hasEmbeddedPayload)
    {
        var request = InstallRequest.Parse(["/nonexistent/setup.exe"], hasEmbeddedPayload);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.SetupNotFound, request.Reason);
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
            var request = InstallRequest.Parse([silentSwitch, setupPath], hasEmbeddedPayload: false);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    // Precedence table row 2: an explicit existing path wins in both worlds,
    // and the payload plays no part when one is given.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_silent_switch_before_path_works(bool hasEmbeddedPayload)
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse(["-s", setupPath], hasEmbeddedPayload);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
            Assert.Equal(Path.GetFullPath(setupPath), request.SetupPath);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_silent_switch_after_path_works(bool hasEmbeddedPayload)
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath, "-s"], hasEmbeddedPayload);

            Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
            Assert.Equal(Path.GetFullPath(setupPath), request.SetupPath);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    // Precedence table row 4: two positional arguments is always
    // UnexpectedArgument, regardless of whether a payload is embedded.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_second_non_switch_argument_is_unexpected(bool hasEmbeddedPayload)
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath, "extra-token"], hasEmbeddedPayload);

            Assert.Equal(InstallRequestKind.Refuse, request.Kind);
            Assert.Equal(RefusalReason.UnexpectedArgument, request.Reason);
            Assert.Equal("extra-token", request.Token);
        }
        finally
        {
            File.Delete(setupPath);
        }
    }

    // Precedence table row 1, "Without" column: no positional argument and no
    // embedded payload is still NoSetupPath — the 1a-style build (a plain
    // `dotnet build`, and the entire net10 test assembly) refuses exactly as
    // slice 1a specified.
    [Fact]
    public void Parse_no_path_at_all_is_no_setup_path_without_a_payload()
    {
        var request = InstallRequest.Parse([], hasEmbeddedPayload: false);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.NoSetupPath, request.Reason);
    }

    [Fact]
    public void Parse_no_path_with_only_silent_switch_is_no_setup_path_without_a_payload()
    {
        var request = InstallRequest.Parse(["--silent"], hasEmbeddedPayload: false);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.NoSetupPath, request.Reason);
    }

    // Precedence table row 1, "With a payload embedded" column: this is the
    // row that changes behaviour, and it is what makes `--silent` with no
    // arguments at all work. SetupPath is null here — Program resolves it via
    // EmbeddedPayload.ExtractToTemp(), which this net10 suite cannot exercise
    // (see the class remarks: no embedded resource exists in this assembly).
    [Fact]
    public void Parse_no_path_at_all_runs_the_wizard_from_the_payload_when_one_is_embedded()
    {
        var request = InstallRequest.Parse([], hasEmbeddedPayload: true);

        Assert.Equal(InstallRequestKind.RunWizard, request.Kind);
        Assert.Null(request.SetupPath);
    }

    [Fact]
    public void Parse_no_path_with_silent_switch_runs_silently_from_the_payload_when_one_is_embedded()
    {
        var request = InstallRequest.Parse(["--silent"], hasEmbeddedPayload: true);

        Assert.Equal(InstallRequestKind.RunSilent, request.Kind);
        Assert.Null(request.SetupPath);
    }

    // Precedence table row 3: a path that does not exist is still an error in
    // both worlds — never silently replaced by the payload.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_missing_setup_file_is_setup_not_found(bool hasEmbeddedPayload)
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var request = InstallRequest.Parse([missing], hasEmbeddedPayload);

        Assert.Equal(InstallRequestKind.Refuse, request.Kind);
        Assert.Equal(RefusalReason.SetupNotFound, request.Reason);
        Assert.Equal(missing, request.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_existing_path_without_silent_switch_is_run_wizard(bool hasEmbeddedPayload)
    {
        var setupPath = CreateTempFile();
        try
        {
            var request = InstallRequest.Parse([setupPath], hasEmbeddedPayload);

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
