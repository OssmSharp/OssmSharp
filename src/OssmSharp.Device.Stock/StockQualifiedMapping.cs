using System.ComponentModel.DataAnnotations;
using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock;

/// <summary>Bounds one verified connection's Simple Stroke settings in the firmware's engine coordinates.</summary>
internal sealed class StockQualifiedMapping
{
    private const int StepsPerMillimeter = 20;
    private const int KeepoutSteps = 12 * StepsPerMillimeter;
    private const int MaximumMeasuredSteps = 500 * StepsPerMillimeter;

    private readonly DeviceInformation _verified;
    private readonly PairingIdentity _identity;
    private readonly int _measuredTravelSteps;
    private readonly decimal _minimumMillimeters;
    private readonly decimal _maximumMillimeters;
    private readonly int _maximumSpeedPercent;

    /// <summary>Creates a mapping from calibrated travel and the qualified engine-relative envelope.</summary>
    /// <param name="verified">The verified stock profile and supported patterns.</param>
    /// <param name="identity">The verified pairing and firmware identity.</param>
    /// <param name="travelMillimeters">The measured home-to-far-switch span in millimetres, on the 0.05 mm step grid.</param>
    /// <param name="minimumMillimeters">The minimum permitted endpoint in millimetres from engine zero, 6 mm inward from home.</param>
    /// <param name="maximumMillimeters">The maximum permitted endpoint in the same engine-relative millimetres.</param>
    /// <param name="maximumSpeedPercent">The qualified speed ceiling, from 1 through 100 percent.</param>
    internal StockQualifiedMapping(
        DeviceInformation verified,
        PairingIdentity identity,
        decimal travelMillimeters,
        decimal minimumMillimeters,
        decimal maximumMillimeters,
        int maximumSpeedPercent)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(identity);
        var measuredSteps = travelMillimeters * StepsPerMillimeter;
        if (measuredSteps <= KeepoutSteps || measuredSteps > MaximumMeasuredSteps
            || measuredSteps != decimal.Truncate(measuredSteps)
            || minimumMillimeters < 0
            || maximumMillimeters > travelMillimeters || minimumMillimeters >= maximumMillimeters)
            throw new ArgumentException("Qualified travel bounds must be ordered and inside calibrated travel.");
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSpeedPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSpeedPercent, 100);

        _verified = verified;
        _identity = identity;
        _measuredTravelSteps = (int)measuredSteps;
        _minimumMillimeters = minimumMillimeters;
        _maximumMillimeters = maximumMillimeters;
        _maximumSpeedPercent = maximumSpeedPercent;
    }

    internal void VerifyConnection(DeviceInformation connected, PairingIdentity reported)
    {
        if (!string.Equals(_verified.ProfileId, connected.ProfileId, StringComparison.Ordinal)
            || !string.Equals(_verified.Version, connected.Version, StringComparison.Ordinal)
            || !_verified.SupportedPatterns.SequenceEqual(connected.SupportedPatterns)
            || !string.Equals(_identity.WifiMac, reported.WifiMac, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_identity.Chip, reported.Chip, StringComparison.Ordinal)
            || !string.Equals(_identity.SketchMd5, reported.SketchMd5, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_identity.Version, reported.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("Stock motion qualification does not match this connection.");
    }

    internal StockValidatedMotionTuple ValidateAndMap(MotionSettings settings)
        => StockValidatedMotionTuple.Create(this, settings);

    internal void Validate(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        }
        catch (ValidationException exception)
        {
            throw new InvalidDataException("The motion settings are invalid.", exception);
        }

        if (settings.Speed > _maximumSpeedPercent
            || settings.Pattern != MotionPattern.SimpleStroke || settings.Sensation != 50 || settings.Buffer != 0
            || settings.Stroke <= 0 || settings.Stroke > settings.Depth)
            throw new InvalidOperationException("The requested stock tuple is outside the qualified profile.");

        var intendedDepthSteps = (decimal)_measuredTravelSteps * settings.Depth / 100;
        var intendedStrokeSteps = (decimal)_measuredTravelSteps * settings.Stroke / 100;
        if (intendedDepthSteps != decimal.Truncate(intendedDepthSteps)
            || intendedStrokeSteps != decimal.Truncate(intendedStrokeSteps))
            throw new InvalidOperationException("The requested stock tuple is not on the firmware step grid.");

        // The pinned firmware converts each percentage independently through float millimetres before truncating to steps.
        var measuredMillimeters = _measuredTravelSteps / (float)StepsPerMillimeter;
        var firmwareDepthSteps = (int)((0.01f * settings.Depth * measuredMillimeters) * StepsPerMillimeter);
        var firmwareStrokeSteps = (int)((0.01f * settings.Stroke * measuredMillimeters) * StepsPerMillimeter);
        var firmwareMaximumSteps = (int)(0.5f
            + (measuredMillimeters - 12f) * StepsPerMillimeter);
        var startSteps = firmwareDepthSteps - firmwareStrokeSteps;
        if (firmwareDepthSteps != intendedDepthSteps || firmwareStrokeSteps != intendedStrokeSteps
            || startSteps < 0
            || firmwareDepthSteps > Math.Min(_measuredTravelSteps - KeepoutSteps, firmwareMaximumSteps)
            || startSteps < _minimumMillimeters * StepsPerMillimeter
            || firmwareDepthSteps > _maximumMillimeters * StepsPerMillimeter)
            throw new InvalidOperationException("The requested stock envelope would require clipping or unqualified rounding.");
    }
}
