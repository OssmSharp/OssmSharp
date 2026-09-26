namespace OssmSharp.Web.Application.Tests.Helpers;

/// <summary>
/// Creates valid baseline motion settings for control tests.
/// </summary>
internal static class TestMotion
{
    /// <summary>
    /// Creates idle stroke-engine settings with caller-selected control values.
    /// </summary>
    /// <param name="pattern">The numeric motion pattern identifier.</param>
    /// <param name="speed">The requested speed.</param>
    /// <param name="stroke">The requested stroke.</param>
    /// <param name="depth">The requested depth.</param>
    /// <param name="sensation">The requested sensation.</param>
    /// <returns>Settings with zero buffer and position.</returns>
    public static MotionSettings Settings(
        int pattern = 0,
        int speed = 0,
        int stroke = 10,
        int depth = 20,
        int sensation = 50) => new(
            MotionDeviceState.StrokeEngineIdle,
            speed,
            stroke,
            sensation,
            depth,
            0,
            (MotionPattern)pattern,
            0);
}
