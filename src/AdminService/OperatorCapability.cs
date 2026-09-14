using Microsoft.Extensions.Options;
using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Web.HostRuntime;

namespace OSSMDotNet.Web.AdminService;

public sealed class OperatorCapability
{
    private readonly byte[] _hash;
    private readonly TimeProvider _time;
    private readonly long _created;
    public string ScopeId { get; } = Guid.NewGuid().ToString("N");

    public OperatorCapability(IOptions<AdminOptions> options, TimeProvider time)
    {
        _hash = CapabilityTokens.Hash(options.Value.OperatorToken);
        _time = time;
        _created = time.GetTimestamp();
    }

    public void Demand(string? token)
    {
        if (_time.GetElapsedTime(_created) >= TimeSpan.FromMinutes(30) || !CapabilityTokens.Matches(_hash, token))
            throw new ControlRejectedException("operator-capability-required-or-expired");
    }
}
