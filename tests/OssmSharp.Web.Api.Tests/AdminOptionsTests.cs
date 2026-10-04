using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults;
using OssmSharp.Web.ServiceDefaults.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class AdminOptionsTests
{
    [Theory]
    [InlineData("https://guest.example", true)]
    [InlineData("https://guest.example:8443/", true)]
    [InlineData("http://127.0.0.1:5001", true)]
    [InlineData("http://[::1]:5001", true)]
    [InlineData("http://localhost:5001", true)]
    [InlineData("http://guest.example", false)]
    [InlineData("ftp://guest.example", false)]
    [InlineData("https://guest.example/join", false)]
    [InlineData("https://guest.example/?invite=secret", false)]
    [InlineData("https://guest.example/#invite=secret", false)]
    [InlineData("https://user:password@guest.example", false)]
    [InlineData("https://*.example", false)]
    [InlineData("http://127.0.0.1.evil.example:5001", false)]
    [InlineData("http://localhost.evil.example:5001", false)]
    [InlineData("", false)]
    public void GuestHostnameRequiresHttpsExceptForLoopback(string origin, bool valid)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:Admin:ListenPort"] = "5002",
            ["OSSM:Admin:GuestHostname"] = origin,
        });
        builder.Services.AddAdminConfiguration();
        using var provider = builder.Services.BuildServiceProvider();

        if (valid)
            Assert.Equal(origin, provider.GetRequiredService<IOptions<AdminOptions>>().Value.GuestHostname);
        else
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminOptions>>().Value);
    }
}
