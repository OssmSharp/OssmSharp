using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class DeviceContractTests
{
    [Fact]
    public void ObservedStateRequiresTimestampAndSettings()
    {
        Assert.Empty(Assert.Single(typeof(DeviceState).GetConstructors()).GetParameters());
        var timestamp = typeof(DeviceState).GetProperty(nameof(DeviceState.Timestamp))!;
        var settings = typeof(DeviceState).GetProperty(nameof(DeviceState.MotionSettings))!;

        Assert.Equal(typeof(DateTime), timestamp.PropertyType);
        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(settings).ReadState);
        Assert.Equal(typeof(double?), typeof(DeviceState).GetProperty(nameof(DeviceState.VelocityMillimetresPerSecond))!.PropertyType);
    }

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
            ["Connected", "Connecting", "Disconnected"],
            Enum.GetNames<DeviceConnectionState>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StateReplacesSnapshotWithoutRetainingDeviceErrors()
    {
        var assembly = typeof(DeviceState).Assembly;

        Assert.Null(assembly.GetType("OSSMDotNet.Abstractions.DeviceSnapshot"));
        Assert.Null(typeof(DeviceState).GetProperty("Fault"));
        Assert.Null(assembly.GetType("OSSMDotNet.Abstractions.DeviceFault"));
    }

    [Fact]
    public void StateContainsOnlySampleFieldsRequestedMotionAndFreshness()
    {
        var stateType = typeof(DeviceState);
        var constructor = Assert.Single(stateType.GetConstructors());
        var properties = stateType.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        var nullability = new NullabilityInfoContext();

        Assert.True(stateType.IsPublic);
        Assert.True(stateType.IsSealed);
        Assert.Empty(constructor.GetParameters());
        Assert.Null(stateType.GetMethod("Deconstruct"));
        Assert.Equal(
            ["MotionSettings", "ObservationFreshness", "RequestedMotion", "SessionId", "Timestamp", "VelocityMillimetresPerSecond"],
            properties.Select(property => property.Name));
        Assert.Equal(
            [typeof(MotionSettings), typeof(ObservationFreshness), typeof(MotionSettings), typeof(Guid), typeof(DateTime), typeof(double?)],
            properties.Select(property => property.PropertyType));
        Assert.Equal(
            ["MotionSettings", "SessionId", "Timestamp"],
            properties.Where(property => property.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
                .Select(property => property.Name));
        Assert.Equal(NullabilityState.NotNull, nullability.Create(stateType.GetProperty(nameof(DeviceState.MotionSettings))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(stateType.GetProperty(nameof(DeviceState.RequestedMotion))!).ReadState);
        Assert.All(properties, property =>
        {
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod.IsPublic);
            var setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter.IsPublic);
            Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        });

        Assert.Null(stateType.GetProperty("Observation"));
        Assert.Null(stateType.GetProperty("CapturedAtUtc"));
        Assert.Null(stateType.GetProperty("Device"));
        Assert.Null(stateType.GetProperty("ConnectionState"));
    }

    [Fact]
    public void NoObservationTypeIsAbsent()
    {
        Assert.Null(typeof(DeviceState).Assembly.GetType("OSSMDotNet.Abstractions.NoObservation"));
    }

    [Fact]
    public void NoDeviceDataExceptionConstructorsPreserveMessageAndCause()
    {
        const string message = "The device has not reported an observation.";
        var cause = new IOException("Transport unavailable.");

        var defaultError = new NoDeviceDataException();
        var customError = new NoDeviceDataException(message);
        var causedError = new NoDeviceDataException(message, cause);

        Assert.Equal(typeof(InvalidOperationException), typeof(NoDeviceDataException).BaseType);
        Assert.Equal("No device observation is available.", defaultError.Message);
        Assert.Null(defaultError.InnerException);
        Assert.Equal(message, customError.Message);
        Assert.Null(customError.InnerException);
        Assert.Equal(message, causedError.Message);
        Assert.Same(cause, causedError.InnerException);
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
        Assert.Equal(
            expected,
            DeviceState.IsSpeedMatch(
                previousRequested is { } previousRequestedSpeed ? ValidSettings(previousRequestedSpeed) : null,
                previousObserved is { } previousObservedSpeed ? State(previousObservedSpeed) : null,
                ValidSettings(requested),
                State(observed)));
    }

    [Theory]
    [InlineData(60, 30, true)]
    [InlineData(60, 61, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    public void SpeedMatchAcceptsRequestedAndObservedStates(
        int requested,
        int observed,
        bool expected) =>
        Assert.Equal(expected, DeviceState.IsSpeedMatch(State(requested), State(observed)));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void SpeedMatchRejectsStatesWithMissingRequiredSettings(bool requestedHasSettings, bool observedHasSettings)
    {
        var invalid = new DeviceState
        {
            Timestamp = DateTime.UnixEpoch,
            MotionSettings = null!,
            SessionId = Guid.Empty,
            RequestedMotion = ValidSettings(0),
        };
        var requested = requestedHasSettings ? State(0) : invalid;
        var observed = observedHasSettings ? State(0) : invalid;

        Assert.False(DeviceState.IsSpeedMatch(requested, observed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void SpeedMatchWithPriorFeedbackRejectsMissingObservedSettings(int requestedSpeed)
    {
        var requested = ValidSettings(requestedSpeed);
        var invalid = new DeviceState
        {
            Timestamp = DateTime.UnixEpoch,
            MotionSettings = null!,
            SessionId = Guid.Empty,
            RequestedMotion = requested,
        };

        Assert.False(DeviceState.IsSpeedMatch(null, null, requested, invalid));
        Assert.False(DeviceState.IsSpeedMatch(ValidSettings(60), State(30), requested, invalid));
    }

    [Fact]
    public void InterfaceExposesStateWriteAndOneOfNotification()
    {
        var state = typeof(IOssmDevice).GetMethod(nameof(IOssmDevice.GetStateAsync));
        var write = typeof(IOssmDevice).GetMethod(nameof(IOssmDevice.SetMotionAsync));
        var changed = typeof(IOssmDevice).GetEvent(nameof(IOssmDevice.DeviceStateChanged));
        var nullability = new NullabilityInfoContext();

        Assert.NotNull(state);
        Assert.Equal(typeof(Task<DeviceState>), state.ReturnType);
        var returnType = nullability.Create(state.ReturnParameter);
        Assert.Equal(NullabilityState.NotNull, returnType.ReadState);
        Assert.Equal(NullabilityState.NotNull, Assert.Single(returnType.GenericTypeArguments).ReadState);
        Assert.Equal([typeof(CancellationToken)], state.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(write);
        Assert.Equal(typeof(Task), write.ReturnType);
        Assert.Equal(
            [typeof(MotionSettings), typeof(CancellationToken)],
            write.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(changed);
        Assert.Equal(typeof(Action<OneOf<DeviceState, Exception>>), changed.EventHandlerType);
        Assert.Null(typeof(IOssmDevice).GetMethod("GetSnapshotAsync"));
        Assert.Null(typeof(IOssmDevice).GetEvent("DeviceChanged"));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IOssmDevice)));
    }

    [Fact]
    public void InterfaceExposesReadOnlyConnectionStatusAndNonNullableDeviceInformation()
    {
        var connectionState = typeof(IOssmDevice).GetProperty(nameof(IOssmDevice.ConnectionState));
        var device = typeof(IOssmDevice).GetProperty(nameof(IOssmDevice.Device));
        var changed = typeof(IOssmDevice).GetEvent(nameof(IOssmDevice.ConnectionStateChanged));
        var nullability = new NullabilityInfoContext();

        Assert.NotNull(connectionState);
        Assert.Equal(typeof(DeviceConnectionState), connectionState.PropertyType);
        Assert.NotNull(connectionState.GetMethod);
        Assert.Null(connectionState.SetMethod);
        Assert.NotNull(device);
        Assert.Equal(typeof(DeviceInformation), device.PropertyType);
        Assert.NotNull(device.GetMethod);
        Assert.Null(device.SetMethod);
        Assert.Equal(NullabilityState.NotNull, nullability.Create(device).ReadState);
        Assert.NotNull(changed);
        Assert.Equal(typeof(Action<DeviceConnectionState>), changed.EventHandlerType);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(changed).ReadState);
    }

    [Fact]
    public void StateRetainsReportedZeroAndDefaultsOptionalValues()
    {
        var reportedZero = State(0);

        Assert.Equal(DateTime.UnixEpoch, reportedZero.Timestamp);
        Assert.Equal(0, reportedZero.MotionSettings.Speed);
        Assert.Equal(Guid.Empty, reportedZero.SessionId);
        Assert.Null(reportedZero.VelocityMillimetresPerSecond);
        Assert.Null(reportedZero.RequestedMotion);
        Assert.Equal(ObservationFreshness.Unavailable, reportedZero.ObservationFreshness);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(100)]
    public void ObservationVelocityDefaultsToUnknownRegardlessOfSpeed(int speed)
    {
        var observation = State(speed);

        Assert.Null(observation.VelocityMillimetresPerSecond);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-12.5d)]
    [InlineData(12.5d)]
    public void StateRetainsSampleContextAndReportedVelocityWhenRequestAndFreshnessChange(double? velocity)
    {
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
        var sessionId = Guid.Parse("a8d3e997-8ccd-42be-bb46-2c96684024a6");
        var requested = ValidSettings(60);
        var observed = ValidSettings(30);
        var state = new DeviceState
        {
            Timestamp = timestamp,
            MotionSettings = observed,
            SessionId = sessionId,
            VelocityMillimetresPerSecond = velocity,
        };

        var retained = state with
        {
            RequestedMotion = requested,
            ObservationFreshness = ObservationFreshness.LastKnown,
        };

        Assert.Same(requested, retained.RequestedMotion);
        Assert.Equal(ObservationFreshness.LastKnown, retained.ObservationFreshness);
        Assert.Same(observed, retained.MotionSettings);
        Assert.Equal(timestamp, retained.Timestamp);
        Assert.Equal(DateTimeKind.Utc, retained.Timestamp.Kind);
        Assert.Equal(sessionId, retained.SessionId);
        Assert.Equal(velocity, retained.VelocityMillimetresPerSecond);
        Assert.Null(state.RequestedMotion);
        Assert.Equal(ObservationFreshness.Unavailable, state.ObservationFreshness);
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

    private static MotionSettings ValidSettings(int speed = 10) => new(
        MotionDeviceState.StrokeEngineIdle,
        speed,
        20,
        50,
        40,
        0,
        MotionPattern.SimpleStroke,
        0);

    private static DeviceState State(int speed) => new()
    {
        Timestamp = DateTime.UnixEpoch,
        MotionSettings = ValidSettings(speed),
        SessionId = Guid.Empty,
    };

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }
}
