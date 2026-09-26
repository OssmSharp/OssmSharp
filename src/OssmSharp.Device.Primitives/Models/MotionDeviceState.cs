using System.Runtime.Serialization;

namespace OssmSharp.Device.Primitives.Models;

/// <summary>
/// Identifies an OSSM device state.
/// </summary>
public enum MotionDeviceState
{
    /// <summary>
    /// The device is starting.
    /// </summary>
    [EnumMember(Value = "idle")]
    Idle,

    /// <summary>
    /// The device is homing.
    /// </summary>
    [EnumMember(Value = "homing")]
    Homing,

    /// <summary>
    /// The device is moving forward while homing.
    /// </summary>
    [EnumMember(Value = "homing.forward")]
    HomingForward,

    /// <summary>
    /// The device is moving backward while homing.
    /// </summary>
    [EnumMember(Value = "homing.backward")]
    HomingBackward,

    /// <summary>
    /// The device is entering the menu.
    /// </summary>
    [EnumMember(Value = "menu")]
    Menu,

    /// <summary>
    /// The device is idle in the menu.
    /// </summary>
    [EnumMember(Value = "menu.idle")]
    MenuIdle,

    /// <summary>
    /// The device is entering simple penetration mode.
    /// </summary>
    [EnumMember(Value = "simplePenetration")]
    SimplePenetration,

    /// <summary>
    /// The device is awaiting confirmation before simple penetration.
    /// </summary>
    [EnumMember(Value = "simplePenetration.preflight")]
    SimplePenetrationPreflight,

    /// <summary>
    /// The device is running simple penetration mode.
    /// </summary>
    [EnumMember(Value = "simplePenetration.idle")]
    SimplePenetrationIdle,

    /// <summary>
    /// The device is entering stroke engine mode.
    /// </summary>
    [EnumMember(Value = "strokeEngine")]
    StrokeEngine,

    /// <summary>
    /// The device is awaiting confirmation before running the stroke engine.
    /// </summary>
    [EnumMember(Value = "strokeEngine.preflight")]
    StrokeEnginePreflight,

    /// <summary>
    /// The device is running the stroke engine.
    /// </summary>
    [EnumMember(Value = "strokeEngine.idle")]
    StrokeEngineIdle,

    /// <summary>
    /// The device is selecting a stroke engine pattern.
    /// </summary>
    [EnumMember(Value = "strokeEngine.pattern")]
    StrokeEnginePattern,

    /// <summary>
    /// The device is entering streaming mode.
    /// </summary>
    [EnumMember(Value = "streaming")]
    Streaming,

    /// <summary>
    /// The device is awaiting confirmation before streaming.
    /// </summary>
    [EnumMember(Value = "streaming.preflight")]
    StreamingPreflight,

    /// <summary>
    /// The device is running streaming mode.
    /// </summary>
    [EnumMember(Value = "streaming.idle")]
    StreamingIdle,

    /// <summary>
    /// The device is entering pairing.
    /// </summary>
    [EnumMember(Value = "pairing")]
    Pairing,

    /// <summary>
    /// The device is waiting during pairing.
    /// </summary>
    [EnumMember(Value = "pairing.idle")]
    PairingIdle,

    /// <summary>
    /// The device completed pairing.
    /// </summary>
    [EnumMember(Value = "pairing.success")]
    PairingSuccess,

    /// <summary>
    /// The device is waiting after successful pairing.
    /// </summary>
    [EnumMember(Value = "pairing.success.idle")]
    PairingSuccessIdle,

    /// <summary>
    /// The device requires Wi-Fi configuration before pairing.
    /// </summary>
    [EnumMember(Value = "pairing.wifi")]
    PairingWifi,

    /// <summary>
    /// The device is waiting for Wi-Fi configuration during pairing.
    /// </summary>
    [EnumMember(Value = "pairing.wifi.idle")]
    PairingWifiIdle,

    /// <summary>
    /// The device is entering the update flow.
    /// </summary>
    [EnumMember(Value = "update")]
    Update,

    /// <summary>
    /// The device is checking for an update.
    /// </summary>
    [EnumMember(Value = "update.checking")]
    UpdateChecking,

    /// <summary>
    /// The device is waiting in the update flow.
    /// </summary>
    [EnumMember(Value = "update.idle")]
    UpdateIdle,

    /// <summary>
    /// The device is entering Wi-Fi configuration.
    /// </summary>
    [EnumMember(Value = "wifi")]
    Wifi,

    /// <summary>
    /// The device is waiting for Wi-Fi configuration.
    /// </summary>
    [EnumMember(Value = "wifi.idle")]
    WifiIdle,

    /// <summary>
    /// The device is entering help.
    /// </summary>
    [EnumMember(Value = "help")]
    Help,

    /// <summary>
    /// The device is displaying help.
    /// </summary>
    [EnumMember(Value = "help.idle")]
    HelpIdle,

    /// <summary>
    /// The device is entering an error state.
    /// </summary>
    [EnumMember(Value = "error")]
    Error,

    /// <summary>
    /// The device is displaying an error.
    /// </summary>
    [EnumMember(Value = "error.idle")]
    ErrorIdle,

    /// <summary>
    /// The device is displaying help for an error.
    /// </summary>
    [EnumMember(Value = "error.help")]
    ErrorHelp,

    /// <summary>
    /// The device is restarting.
    /// </summary>
    [EnumMember(Value = "restart")]
    Restart,
}
