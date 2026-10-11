using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.Contracts;

/// <summary>
/// Contains shared illustration dimensions, not device calibration or motion settings.
/// </summary>
/// <param name="RailLengthCm">Usable rail travel in centimetres, from 1 to 500.</param>
/// <param name="AttachmentLengthCm">Attachment length in centimetres, from 0 to 500.</param>
public sealed record PreviewDimensions(
    [property: Range(PreviewDimensions.MinimumRailLengthCm, PreviewDimensions.MaximumLengthCm)] double RailLengthCm = 40,
    [property: Range(0, PreviewDimensions.MaximumLengthCm)] double AttachmentLengthCm = 24)
{
    /// <summary>
    /// The smallest supported illustrative rail length, in centimetres.
    /// </summary>
    public const double MinimumRailLengthCm = 1;

    /// <summary>
    /// The largest supported illustrative rail or attachment length, in centimetres.
    /// </summary>
    public const double MaximumLengthCm = 500;
}
