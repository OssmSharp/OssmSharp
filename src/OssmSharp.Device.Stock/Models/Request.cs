using System.Globalization;

namespace OssmSharp.Device.Stock.Models;

/// <summary>
/// Represents one stock-firmware setting command derived from validated motion settings.
/// </summary>
internal sealed record Request
{
    private Request(string setting, int value)
    {
        Setting = setting;
        Value = value;
    }

    /// <summary>
    /// Gets the stock-firmware setting name.
    /// </summary>
    public string Setting { get; }

    /// <summary>
    /// Gets the setting value sent to the firmware.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Converts motion settings into the ordered stock-firmware setting commands.
    /// </summary>
    /// <remarks>Changing the motion envelope requires a confirmed stop and a validated intermediate sequence before these commands are dispatched.</remarks>
    /// <param name="mapped">The connection-qualified exact motion settings to encode.</param>
    /// <returns>Commands for stroke, sensation, depth, buffer and pattern, with speed first when zero and last otherwise.</returns>
    /// <exception cref="ArgumentNullException">No validated tuple was supplied.</exception>
    public static IReadOnlyList<Request> FromValidatedMotion(StockValidatedMotionTuple mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        var settings = mapped.Settings;

        var requests = new List<Request>(6);
        if (settings.Speed == 0)
        {
            requests.Add(new("speed", 0));
        }

        requests.Add(new("stroke", settings.Stroke));
        requests.Add(new("sensation", settings.Sensation));
        requests.Add(new("depth", settings.Depth));
        requests.Add(new("buffer", settings.Buffer));
        requests.Add(new("pattern", (int)settings.Pattern));

        if (settings.Speed != 0)
        {
            requests.Add(new("speed", settings.Speed));
        }

        return requests;
    }

    /// <summary>
    /// Formats this setting as a stock-firmware command payload.
    /// </summary>
    /// <returns>The command text using invariant numeric formatting.</returns>
    public string ToPayload() =>
        $"set:{Setting}:{Value.ToString(CultureInfo.InvariantCulture)}";

    internal static Request ZeroSpeed() => new("speed", 0);

    internal static Request Speed(StockValidatedMotionTuple mapped) =>
        new("speed", (mapped ?? throw new ArgumentNullException(nameof(mapped))).Settings.Speed);

    internal static IReadOnlyList<Request> ChangedSettings(
        MotionSettings previous,
        MotionSettings next)
    {
        var changed = new List<Request>(5);
        if (previous.Stroke != next.Stroke) changed.Add(new("stroke", next.Stroke));
        if (previous.Sensation != next.Sensation) changed.Add(new("sensation", next.Sensation));
        if (previous.Depth != next.Depth) changed.Add(new("depth", next.Depth));
        if (previous.Buffer != next.Buffer) changed.Add(new("buffer", next.Buffer));
        if (previous.Pattern != next.Pattern) changed.Add(new("pattern", (int)next.Pattern));
        return changed;
    }

    internal static Request? ChangedSetting(MotionSettings previous, StockValidatedMotionTuple mapped) =>
        ChangedSettings(previous, mapped.Settings).SingleOrDefault();
}
