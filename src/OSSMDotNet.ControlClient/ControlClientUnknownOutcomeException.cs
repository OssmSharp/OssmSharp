namespace OSSMDotNet.ControlClient;

/// <summary>Indicates a command deadline elapsed after dispatch, so service acceptance is unknown.</summary>
public sealed class ControlClientUnknownOutcomeException(Exception innerException)
    : Exception("The control command outcome is unknown. Wait for the next service state update; do not resend it.", innerException);
