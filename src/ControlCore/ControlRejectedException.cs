namespace OSSMDotNet.Web.ControlCore;

public sealed class ControlRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
