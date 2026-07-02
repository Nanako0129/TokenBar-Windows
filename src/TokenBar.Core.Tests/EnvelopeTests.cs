using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Envelope contract checks, ported from the macOS repo's
// TBCore.envelopeContractChecks (run there via `swift run TokenBar --selftest`).
public class EnvelopeTests
{
    private sealed record Payload(int Value);

    [Fact]
    public void OkEnvelopeReturnsData()
    {
        var result = TbCore.DecodeEnvelope<Payload>("""{"ok":true,"data":{"value":42}}""");
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void ErrEnvelopeThrowsWithMessage()
    {
        var ex = Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<Payload>("""{"ok":false,"err":"boom"}"""));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void ErrEnvelopeWithoutMessageStillThrows()
    {
        Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<Payload>("""{"ok":false}"""));
    }

    [Fact]
    public void OkEnvelopeMissingDataThrows()
    {
        Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<Payload>("""{"ok":true}"""));
    }

    [Fact]
    public void MissingOkThrows()
    {
        Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<Payload>("""{"data":{"value":1}}"""));
    }

    [Fact]
    public void MalformedJsonThrows()
    {
        Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<Payload>("{not json"));
    }

    [Fact]
    public void NonObjectRootThrows()
    {
        Assert.Throws<TbCoreException>(() => TbCore.DecodeEnvelope<Payload>("[1,2,3]"));
    }
}
