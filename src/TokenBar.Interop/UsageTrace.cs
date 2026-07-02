using System.Text.Json.Serialization;

namespace TokenBar.Interop;

// Live tail payloads (Swift port: TokenBarCore/UsageTrace.swift). TraceBucket
// is the one snake_case shape in the contract (the Rust struct has no rename
// attribute), hence the explicit wire name for tokens_per_min; the other
// fields are single lowercase words the Web camelCase policy already matches.

public sealed record TraceBucket(
    string Client,
    string Agent,
    string Model,
    long Tokens,
    int Messages,
    [property: JsonPropertyName("tokens_per_min")] double TokensPerMin);

// Payload of `tb_tokens_per_min`: {"tokensPerMin": <number>}.
public sealed record TokensPerMin(
    [property: JsonPropertyName("tokensPerMin")] double Value);
