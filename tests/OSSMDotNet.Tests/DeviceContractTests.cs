using System.ComponentModel.DataAnnotations;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class DeviceContractTests
{
    [Fact]
    public void AbstractionsDoNotEnumerateDeviceImplementations()
    {
        var assembly = typeof(DeviceInformation).Assembly;

        Assert.Null(assembly.GetType("OSSMDotNet.Abstractions.DeviceKind"));
        Assert.Null(typeof(DeviceInformation).GetProperty("Kind"));
    }

    [Fact]
    public void ConnectionStateContainsOnlyObservableConnectionConditions()
    {
        Assert.Equal(
            ["Connecting", "Connected", "Disconnected", "Faulted"],
            Enum.GetNames<DeviceConnectionState>());
    }

    [Fact]
    public void SnapshotDoesNotRetainDeviceErrors()
    {
        var assembly = typeof(DeviceSnapshot).Assembly;

        Assert.Null(typeof(DeviceSnapshot).GetProperty("Fault"));
        Assert.Null(assembly.GetType("OSSMDotNet.Abstractions.DeviceFault"));
    }

    [Fact]
    public void MotionSettingsUsePropertyAndCrossFieldValidation()
    {
        var propertyInvalid = ValidSettings() with { Speed = 101 };
        var geometryInvalid = ValidSettings() with { Speed = 1, Stroke = 0 };

        var propertyResults = Validate(propertyInvalid);
        var geometryResults = Validate(geometryInvalid);

        Assert.IsAssignableFrom<IValidatableObject>(geometryInvalid);
        Assert.Contains(propertyResults, result => result.MemberNames.Contains(nameof(MotionSettings.Speed)));
        Assert.Contains(geometryResults, result => result.MemberNames.Contains(nameof(MotionSettings.Stroke)));
    }

    [Theory]
    [InlineData(null, null, 60, 30, true)]
    [InlineData(null, null, 60, 61, false)]
    [InlineData(60, 30, 40, 20, true)]
    [InlineData(60, 30, 40, 19, false)]
    [InlineData(3, 1, 5, 2, true)]
    [InlineData(60, 0, 20, 0, true)]
    [InlineData(60, 30, 0, 0, true)]
    [InlineData(60, 30, 0, 1, false)]
    public void SpeedMatchUsesOnlyKnobValuesConsistentWithPriorTruncatedFeedback(
        int? previousRequested,
        int? previousObserved,
        int requested,
        int observed,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeviceState.IsSpeedMatch(previousRequested, previousObserved, requested, observed));
    }

    [Fact]
    public void InterfaceExposesSnapshotWriteAndOneOfNotification()
    {
        var snapshot = typeof(IOssmDevice).GetMethod(nameof(IOssmDevice.GetSnapshotAsync));
        var write = typeof(IOssmDevice).GetMethod(nameof(IOssmDevice.SetMotionAsync));
        var changed = typeof(IOssmDevice).GetEvent(nameof(IOssmDevice.DeviceChanged));

        Assert.NotNull(snapshot);
        Assert.Equal(typeof(Task<DeviceSnapshot>), snapshot.ReturnType);
        Assert.Equal([typeof(CancellationToken)], snapshot.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(write);
        Assert.Equal(typeof(Task), write.ReturnType);
        Assert.Equal(
            [typeof(MotionSettings), typeof(CancellationToken)],
            write.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(changed);
        Assert.Equal(typeof(Action<OneOf<DeviceSnapshot, Exception>>), changed.EventHandlerType);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IOssmDevice)));
    }

    [Fact]
    public void SnapshotDistinguishesUnavailableObservationFromSyntheticZero()
    {
        var snapshot = new DeviceSnapshot(
            DateTime.UnixEpoch,
            DeviceConnectionState.Disconnected,
            null,
            null,
            ObservationFreshness.Unavailable,
            null,
            null);

        Assert.Null(snapshot.Observation);
        Assert.Equal(ObservationFreshness.Unavailable, snapshot.ObservationFreshness);
        Assert.Null(snapshot.RequestedMotion);
        Assert.Null(snapshot.ObservedVelocityMillimetresPerSecond);
    }

    [Fact]
    public void UnexpectedSpeedExceptionRetainsBoundedObservationContextAndCause()
    {
        var cause = new InvalidDataException("provider failure");
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);

        var error = new UnexpectedSpeedChangeException(12, 25, 40, timestamp, cause);

        Assert.Equal(12, error.PreviousObservedSpeed);
        Assert.Equal(25, error.ObservedSpeed);
        Assert.Equal(40, error.PendingRequestedSpeed);
        Assert.Equal(timestamp, error.ObservationTimestamp);
        Assert.Same(cause, error.InnerException);
        Assert.DoesNotContain("provider failure", error.Message, StringComparison.Ordinal);
    }

    private static MotionSettings ValidSettings() => new(
        MotionDeviceState.StrokeEngineIdle,
        10,
        20,
        50,
        40,
        0,
        MotionPattern.SimpleStroke,
        0);

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }
}
