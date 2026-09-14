using System.Text;
using OSSMDotNet.Web.SessionContracts;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class GuestProtocolTests
{
    [Fact]
    public void VersionTwoRunInputIsAcceptedAndLegacyHoldInputIsRejected()
    {
        const string input = """{"v":2,"epoch":"e","policyVersion":1,"grantId":"g","seq":1,"run":true,"desired":{"pattern":0,"speed":10,"stroke":10,"depth":20,"sensation":50}}""";
        var frame = Valid.Replace("\"op\":\"status\"", "\"op\":\"input\"").Replace("null", input);
        var call = GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(frame));
        Assert.True(call.Input!.Run);
        Assert.Equal(2, call.Input.V);
        Assert.Throws<InvalidDataException>(() => GuestProtocol.DecodeCall(
            Encoding.UTF8.GetBytes(frame.Replace("\"v\":2", "\"v\":1").Replace("\"run\":true", "\"hold\":true"))));
    }

    private const string Valid = """{"v":1,"requestId":"r1","op":"status","sessionId":"sid","proof":"secret","connectionId":"connection","input":null}""";

    [Fact]
    public void ValidScopedStatusRequestIsDecoded()
    {
        var request = GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid));
        Assert.Equal("status", request.Op);
        Assert.Equal("sid", request.SessionId);
        Assert.Equal("connection", request.ConnectionId);
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("unlock")]
    [InlineData("create")]
    [InlineData("set-limits")]
    [InlineData("external")]
    public void ForwardingContractHasNoPrivilegedOperation(string operation)
    {
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid.Replace("status", operation))));
    }

    [Fact]
    public void UnknownPropertiesCannotSmuggleRoleOrDevice()
    {
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid[..^1] + ""","role":"operator"}""")));
    }

    [Fact]
    public void VersionsAndRequiredProofAreValidated()
    {
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid.Replace("\"v\":1", "\"v\":2"))));
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid.Replace("\"secret\"", "\"\""))));
    }

    [Fact]
    public void PayloadForDifferentOperationIsRejected()
    {
        const string input = """{"v":2,"epoch":"e","policyVersion":1,"grantId":"g","seq":1,"run":true,"desired":{"pattern":0,"speed":10,"stroke":10,"depth":20,"sensation":50}}""";
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid.Replace("null", input))));
    }

    [Fact]
    public void DuplicatePropertiesAndTrailingObjectsCannotOverrideValidation()
    {
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid[..^1] + ""","v":2}""")));
        Assert.Throws<InvalidDataException>(() =>
            GuestProtocol.DecodeCall(Encoding.UTF8.GetBytes(Valid + "{}")));
    }
}
