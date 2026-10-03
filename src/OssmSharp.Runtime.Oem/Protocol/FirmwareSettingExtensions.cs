namespace OssmSharp.Runtime.Oem.Protocol;

internal static class FirmwareSettingExtensions
{
    internal static DeviceSetting ToDeviceSetting(this FirmwareSetting setting) => setting switch
    {
        FirmwareSetting.Pattern => DeviceSetting.Pattern,
        FirmwareSetting.Speed => DeviceSetting.Speed,
        FirmwareSetting.Stroke => DeviceSetting.Stroke,
        FirmwareSetting.Depth => DeviceSetting.Depth,
        FirmwareSetting.Sensation => DeviceSetting.Sensation,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown firmware setting."),
    };

    internal static FirmwareSetting ToFirmwareSetting(this DeviceSetting setting) => setting switch
    {
        DeviceSetting.Pattern => FirmwareSetting.Pattern,
        DeviceSetting.Speed => FirmwareSetting.Speed,
        DeviceSetting.Stroke => FirmwareSetting.Stroke,
        DeviceSetting.Depth => FirmwareSetting.Depth,
        DeviceSetting.Sensation => FirmwareSetting.Sensation,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown device setting."),
    };
}
