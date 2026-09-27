namespace OssmSharp.Device.Stock;

internal sealed class StockValidatedMotionTuple
{
    private StockValidatedMotionTuple(MotionSettings settings) => Settings = settings;

    internal MotionSettings Settings { get; }

    internal static StockValidatedMotionTuple Create(
        StockQualifiedMapping mapping,
        MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate(settings);
        return new StockValidatedMotionTuple(settings);
    }
}
