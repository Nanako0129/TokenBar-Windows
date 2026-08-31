namespace Syrtis.Installer;

/// <summary>Choosing what the Welcome page says it is about to install.
///
/// <para>Split out from <see cref="WizardForm"/> for one reason: reading a
/// PE file's version resource cannot be tested from
/// <c>TokenBar.Core.Tests</c>, but deciding what to do with what comes back
/// can be, and the deciding is where the mistake was. The wizard read both
/// halves of "will install {product} version {version}" from its own assembly,
/// which is a claim about the wrapper dressed as a claim about the
/// payload.</para></summary>
internal static class PayloadIdentity
{
    /// <summary>The payload's own answer when it has one, this wrapper's when
    /// it does not, and a constant when neither does.
    ///
    /// <para>Whitespace counts as absent. A version resource that exists but
    /// is blank is common enough in stub executables, and " version  " on the
    /// Welcome page reads as a bug rather than as a missing value.</para>
    /// </summary>
    internal static string Choose(string? fromPayload, string? fromWrapper, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(fromPayload))
        {
            return fromPayload!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fromWrapper))
        {
            return fromWrapper!.Trim();
        }

        return fallback;
    }
}
