namespace Syrtis.Installer;

/// <summary>Which of the four things this process was asked to do.</summary>
internal enum InstallRequestKind
{
    RunWizard,
    RunSilent,
    SelfCheck,
    Refuse,
}

/// <summary>Why a Refuse request was refused. An enum plus the offending
/// token, never a rendered sentence — see InstallRequest's own remarks for
/// why. The message is rendered at the call site (Program.RenderRefusal),
/// from this pair.</summary>
internal enum RefusalReason
{
    /// <summary>No non-switch argument was given at all.</summary>
    NoSetupPath,

    /// <summary>A candidate setup path was given but does not exist on disk.</summary>
    SetupNotFound,

    /// <summary>An argument was present that this parser does not recognise:
    /// a second non-switch argument, or anything alongside --self-check.</summary>
    UnexpectedArgument,

    /// <summary>A switch this wrapper does not support — anything beginning
    /// with '-' that is not -s, --silent or --self-check.
    ///
    /// <para>Separate from UnexpectedArgument because the refusal path exists
    /// to protect exactly the case where slice 1b puts this under Setup.exe's
    /// own filename and an existing script arrives carrying Setup's options.
    /// Without this, "--installto D:\X" refused while naming "D:\X" — both
    /// tokens are non-switches to a parser that only knows -s, so the second
    /// one looked like the surplus — and a lone "-v" was reported as a missing
    /// setup file. The right token was refused for the wrong stated reason,
    /// which is the least useful place to be precise.</para></summary>
    UnsupportedSwitch,
}

/// <summary>The single parsed shape of the process's command line, produced
/// once by <see cref="Parse"/> and read by every consumer (Main, RunSilent,
/// RunGui, MissingSetupMessage, Fail) instead of each re-deriving its own
/// answer from <c>args</c>. That re-derivation is exactly how finding round 8
/// happened: Program took the first non-switch argument as the setup path,
/// SetupLocator parsed the same array again, and Fail re-ran
/// <c>args.Any(IsSilentSwitch)</c> a fourth time to choose its surface.
///
/// <para><b>--self-check is a mode, not a path.</b> Without this being its own
/// case, the parser above would take it as the first non-switch argument and
/// report "Setup file not found: --self-check" — the exact collision this
/// slice's own new switch would otherwise create with this slice's own
/// refusal logic.</para>
///
/// <para><b>A refusal carries a reason, not a rendered sentence.</b> If it
/// carried only a string, no test could tell NoSetupPath, SetupNotFound and
/// UnexpectedArgument apart without reading a culture-dependent
/// <see cref="Strings"/> member — which the project's own testing constraint
/// forbids. The three are exactly what findings 4, 5 and 7 turned on.</para></summary>
internal readonly struct InstallRequest
{
    private const string SelfCheckSwitch = "--self-check";

    private InstallRequest(InstallRequestKind kind, string? setupPath, RefusalReason reason, string? token, bool silentRequested)
    {
        Kind = kind;
        SetupPath = setupPath;
        Reason = reason;
        Token = token;
        SilentRequested = silentRequested;
    }

    internal InstallRequestKind Kind { get; }

    /// <summary>The resolved, existing setup path. Non-null exactly when
    /// Kind is RunWizard or RunSilent.</summary>
    internal string? SetupPath { get; }

    /// <summary>Meaningful only when Kind is Refuse.</summary>
    internal RefusalReason Reason { get; }

    /// <summary>The offending argument, when Reason names one
    /// (SetupNotFound, UnexpectedArgument, UnsupportedSwitch). Null for
    /// NoSetupPath.</summary>
    internal string? Token { get; }

    /// <summary>Whether -s / --silent / -S was present anywhere in the
    /// original arguments, independent of Kind. Fail reads this to choose its
    /// surface (dialog vs stderr) even for a Refuse request, without
    /// re-deriving it from args a fourth time.</summary>
    internal bool SilentRequested { get; }

    private static InstallRequest Refuse(RefusalReason reason, string? token, bool silentRequested) =>
        new(InstallRequestKind.Refuse, null, reason, token, silentRequested);

    internal static InstallRequest Parse(string[] args)
    {
        var silentRequested = args.Any(SetupLocator.IsSilentSwitch);

        if (args.Any(a => a.Equals(SelfCheckSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 1)
            {
                return new InstallRequest(InstallRequestKind.SelfCheck, null, default, null, silentRequested);
            }

            var otherToken = args.FirstOrDefault(a => !a.Equals(SelfCheckSwitch, StringComparison.OrdinalIgnoreCase))
                ?? SelfCheckSwitch;
            return Refuse(RefusalReason.UnexpectedArgument, otherToken, silentRequested);
        }

        // Before anything positional. A Windows path never begins with '-',
        // so this is unambiguous, and it is the only test that can tell
        // "--installto D:\X" (an unsupported switch and its value) from
        // "setup.exe extra.exe" (two paths). Only '-' is treated as a switch
        // marker: '/x' is a valid rooted Windows path, and Velopack's own
        // options are all '-' or '--'.
        var unsupportedSwitch = args.FirstOrDefault(a =>
            a.StartsWith("-", StringComparison.Ordinal)
            && !SetupLocator.IsSilentSwitch(a)
            && !a.Equals(SelfCheckSwitch, StringComparison.OrdinalIgnoreCase));
        if (unsupportedSwitch != null)
        {
            return Refuse(RefusalReason.UnsupportedSwitch, unsupportedSwitch, silentRequested);
        }

        var nonSwitchArgs = args.Where(a => !SetupLocator.IsSilentSwitch(a)).ToArray();
        if (nonSwitchArgs.Length > 1)
        {
            return Refuse(RefusalReason.UnexpectedArgument, nonSwitchArgs[1], silentRequested);
        }

        var candidate = nonSwitchArgs.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Refuse(RefusalReason.NoSetupPath, null, silentRequested);
        }

        // Reuses SetupLocator's own resolution (existence check + GetFullPath)
        // rather than repeating it — the exact duplication this slice exists
        // to remove.
        var setupPath = SetupLocator.Locate(args);
        if (setupPath == null)
        {
            return Refuse(RefusalReason.SetupNotFound, candidate, silentRequested);
        }

        var kind = silentRequested ? InstallRequestKind.RunSilent : InstallRequestKind.RunWizard;
        return new InstallRequest(kind, setupPath, default, null, silentRequested);
    }
}
