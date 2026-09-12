namespace TokenBar.Core;

/// <summary>
/// Whether a locally recorded model belongs to a window's declared model scope.
/// Port of TokenBarCore/ModelScope.swift.
///
/// This is a JOIN BETWEEN TWO NAMING SYSTEMS, and saying so is the point. The
/// scope arrives as the provider's display-name slug — <c>fable</c>, from a
/// limit entry whose <c>scope.model.display_name</c> is "Fable" — while the
/// model on a message is the canonical id the local transcript carried,
/// <c>claude-fable-5</c>. Nothing guarantees the two agree, and the engine
/// cannot close the gap for us: the live <c>oauth/usage</c> payload reports
/// <c>scope.model.id: null</c>, so the display name is the only identity the
/// provider actually sends.
///
/// A join like this fails silently by default — the bars simply stop matching
/// the curve, which is the one thing the card exists to explain. So the rule is
/// deliberately narrow and stated once; its failure is made visible by the
/// caller reporting an empty scoped result rather than drawing zero usage.
/// </summary>
public static class ModelScope
{
    /// <summary>Lowercase alphanumeric runs. The same shape <c>claude_slug</c>
    /// produces on the Rust side, so <c>Fable</c> and <c>claude-fable-5</c>
    /// decompose comparably.</summary>
    internal static List<string> Tokens(string value)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>True when every token of the scope appears in the model id.
    ///
    /// Subset rather than equality, because the two systems name at different
    /// granularities: the provider says "Fable" where the transcript says
    /// <c>claude-fable-5</c>. Subset rather than substring, because a substring
    /// test is not a rule anyone can state — it matches on accidents of spelling
    /// and cannot be reasoned about when a new model appears.
    ///
    /// Over-matching is bounded by the engine, not by this function: a scope
    /// naming every model (<c>all-models</c>, or a name ending in it) never
    /// reaches the wire. Under-matching — a display name carrying a word the id
    /// does not — is the failure this cannot rule out, and is exactly why the
    /// caller reports an empty scoped result instead of zero usage.</summary>
    public static bool Covers(string scope, string modelId)
    {
        var wanted = Tokens(scope);
        if (wanted.Count == 0)
        {
            return false;
        }

        var present = new HashSet<string>(Tokens(modelId), StringComparer.Ordinal);
        return wanted.All(present.Contains);
    }
}
