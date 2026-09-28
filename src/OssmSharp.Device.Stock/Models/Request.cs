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
    /// Converts motion settings into the complete, ordered stock-firmware setting commands.
    /// </summary>
    /// <remarks>
    /// Every setting is always included. Depth is written before stroke only when the new stroke would otherwise
    /// exceed the current depth; the last known depth affects only this order, never which commands are sent.
    /// </remarks>
    /// <param name="mapped">The connection-qualified exact motion settings to encode.</param>
    /// <param name="currentDepth">The last known depth, or <see langword="null"/> when unknown.</param>
    /// <returns>Commands for stroke, depth, sensation, buffer and pattern, with speed first when zero and last otherwise.</returns>
    /// <exception cref="ArgumentNullException">No validated tuple was supplied.</exception>
    public static IReadOnlyList<Request> FromValidatedMotion(StockValidatedMotionTuple mapped, int? currentDepth = null)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        var settings = mapped.Settings;

        var requests = new List<Request>(6);
        if (settings.Speed == 0)
        {
            requests.Add(new("speed", 0));
        }

        Request stroke = new("stroke", settings.Stroke);
        Request depth = new("depth", settings.Depth);
        if (settings.Stroke > currentDepth)
        {
            requests.Add(depth);
            requests.Add(stroke);
        }
        else
        {
            requests.Add(stroke);
            requests.Add(depth);
        }

        requests.Add(new("sensation", settings.Sensation));
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
}
