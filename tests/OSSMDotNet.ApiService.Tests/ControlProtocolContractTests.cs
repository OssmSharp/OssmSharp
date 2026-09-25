using System.Text.Json;
using OSSMDotNet.Models;

namespace OSSMDotNet.ApiService.Tests;

/// <summary>
/// Protects guest-safe hub wire fields and the validation limits of protocol text.
/// </summary>
public sealed class ControlProtocolContractTests
{
    private const int CorrelationMaximumLength = 64;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object?[]> TextBoundaries =>
    [
        [null, false],
        ["", false],
        [" \t", false],
        [new string('c', CorrelationMaximumLength), true],
        [new string('c', CorrelationMaximumLength + 1), false]
    ];

    [Theory]
    [MemberData(nameof(TextBoundaries))]
    public void ValidTextAcceptsOnlyNonWhitespaceTextAtOrBelowTheCurrentCorrelationLimit(
        string? value, bool expected)
    {
        Assert.Equal(expected, ControlProtocol.ValidText(value, CorrelationMaximumLength));
    }

    [Fact]
    public void GuestStatusSerializesOnlyTheCurrentGuestSafeHubFieldsWithStableWireNames()
    {
        var status = new GuestStatus(
            "Ready", "armed", 17, 2, true, true, new(0, 25, 10, 40, 50), 42.5, -1.5,
            new(25, 80), 3, "Approved", "connection-visible-to-guest", 9,
            new("grant", "epoch", 3, 750))
        {
            Current = new(0, 20, 10, 40, 50),
            VerifiedSeq = 9,
            DeviceEnabled = true,
            IsSimulator = true,
            SnapshotRevision = 12
        };

        var json = JsonSerializer.Serialize(status, SerializerOptions);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
        [
            "mode", "reason", "generation", "lockRevision", "bridgeReady", "deviceMoving", "desired",
            "position", "velocity", "limits", "policyVersion", "sessionMode", "connectionId", "ackSeq",
            "grant", "current", "verifiedSeq", "deviceEnabled", "isSimulator", "snapshotRevision"
        ], properties);
        Assert.Equal("Ready", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal(12, document.RootElement.GetProperty("snapshotRevision").GetInt64());
        Assert.Equal("grant", document.RootElement.GetProperty("grant").GetProperty("id").GetString());
        Assert.DoesNotContain("guestToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invitationProof", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileId", json, StringComparison.OrdinalIgnoreCase);
    }
}
