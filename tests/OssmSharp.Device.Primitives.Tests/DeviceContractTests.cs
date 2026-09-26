using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using OneOf;

namespace OssmSharp.Device.Primitives.Tests;

/// <summary>
/// Verifies the public device contract and validation of reported motion state.
/// </summary>
public sealed class DeviceContractTests
{
    [Fact]
    public void ReportedStateRequiresTimestampAndSettings()
    {
        Assert.Empty(Assert.Single(typeof(DeviceState).GetConstructors()).GetParameters());
        var timestamp = typeof(DeviceState).GetProperty(nameof(DeviceState.Timestamp))!;
        var settings = typeof(DeviceState).GetProperty(nameof(DeviceState.MotionSettings))!;

        Assert.Equal(typeof(DateTime), timestamp.PropertyType);
        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(settings).ReadState);
        Assert.Equal(typeof(double?), typeof(DeviceState).GetProperty(nameof(DeviceState.VelocityMillimetresPerSecond))!.PropertyType);
    }

    [Fact]
    public void PrimitivesDoNotEnumerateDeviceImplementations()
    {
        var assembly = typeof(DeviceInformation).Assembly;

        Assert.Null(assembly.GetType("OssmSharp.Device.Primitives.DeviceKind"));
        Assert.Null(typeof(DeviceInformation).GetProperty("Kind"));
    }

    [Fact]
    public void ConnectionStateContainsOnlyConnectionConditions()
    {
        Assert.Equal(
            ["Connected", "Connecting", "Disconnected"],
            Enum.GetNames<DeviceConnectionState>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StateContainsOnlyReportedSampleFields()
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
            ["MotionSettings", "SessionId", "Timestamp", "VelocityMillimetresPerSecond"],
            properties.Select(property => property.Name));
        Assert.Equal(
            [typeof(MotionSettings), typeof(Guid), typeof(DateTime), typeof(double?)],
            properties.Select(property => property.PropertyType));
        Assert.Equal(
            ["MotionSettings", "SessionId", "Timestamp"],
            properties.Where(property => property.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
                .Select(property => property.Name));
        Assert.Equal(NullabilityState.NotNull, nullability.Create(stateType.GetProperty(nameof(DeviceState.MotionSettings))!).ReadState);
        Assert.All(properties, property =>
        {
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod.IsPublic);
            var setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter.IsPublic);
            Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        });
    }

    [Fact]
    public void StateOwnsReportedSampleValidation()
    {
        var invalidTimestamp = State(10) with { Timestamp = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Local) };
        var missingSettings = State(10) with { MotionSettings = null! };
        var invalidSettings = State(10) with { MotionSettings = ValidSettings(10) with { Stroke = 0 } };
        var invalidVelocity = State(10) with { VelocityMillimetresPerSecond = double.NaN };

        Assert.IsAssignableFrom<IValidatableObject>(invalidTimestamp);
        Assert.Contains(Validate(invalidTimestamp), result => result.MemberNames.Contains(nameof(DeviceState.Timestamp)));
        Assert.Contains(Validate(missingSettings), result => result.MemberNames.Contains(nameof(DeviceState.MotionSettings)));
        Assert.Contains(Validate(invalidSettings), result => result.MemberNames.Contains(nameof(DeviceState.MotionSettings)));
        Assert.Contains(
            Validate(invalidVelocity),
            result => result.MemberNames.Contains(nameof(DeviceState.VelocityMillimetresPerSecond)));
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

    [Fact]
    public void InterfaceExposesWriteAndStateNotification()
    {
        var write = Assert.Single(typeof(IOssmDevice).GetMethods(), method => !method.IsSpecialName);
        var changed = typeof(IOssmDevice).GetEvent(nameof(IOssmDevice.DeviceStateChanged));

        Assert.Equal(nameof(IOssmDevice.SetMotionAsync), write.Name);
        Assert.Equal(typeof(Task), write.ReturnType);
        Assert.Equal(
            [typeof(MotionSettings), typeof(CancellationToken)],
            write.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(changed);
        Assert.Equal(typeof(Action<OneOf<DeviceState, Exception>>), changed.EventHandlerType);
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
    public void StateRetainsReportedZeroAndDefaultsVelocity()
    {
        var reported = State(0);

        Assert.Equal(DateTime.UnixEpoch, reported.Timestamp);
        Assert.Equal(0, reported.MotionSettings.Speed);
        Assert.Equal(Guid.Empty, reported.SessionId);
        Assert.Null(reported.VelocityMillimetresPerSecond);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-12.5d)]
    [InlineData(12.5d)]
    public void StateRetainsReportedSampleContext(double? velocity)
    {
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
        var sessionId = Guid.Parse("a8d3e997-8ccd-42be-bb46-2c96684024a6");
        var settings = ValidSettings(30);
        var state = new DeviceState
        {
            Timestamp = timestamp,
            MotionSettings = settings,
            SessionId = sessionId,
            VelocityMillimetresPerSecond = velocity,
        };

        Assert.Same(settings, state.MotionSettings);
        Assert.Equal(timestamp, state.Timestamp);
        Assert.Equal(DateTimeKind.Utc, state.Timestamp.Kind);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(velocity, state.VelocityMillimetresPerSecond);
    }

    [Fact]
    public void UnexpectedSpeedExceptionRetainsBoundedStateContextAndCause()
    {
        var cause = new InvalidDataException("provider failure");
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);

        var error = new UnexpectedSpeedChangeException(12, 25, 40, timestamp, cause);

        Assert.Equal(12, error.PreviousStateSpeed);
        Assert.Equal(25, error.StateSpeed);
        Assert.Equal(40, error.PendingRequestedSpeed);
        Assert.Equal(timestamp, error.StateTimestamp);
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
