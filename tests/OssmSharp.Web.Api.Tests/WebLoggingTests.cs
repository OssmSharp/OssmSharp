using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults;
using Serilog.Extensions.Logging;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class WebLoggingTests
{
    [Theory]
    [InlineData("OssmSharp.Web.Api", false)]
    [InlineData("OssmSharp.Web.AdminWeb", false)]
    [InlineData("OssmSharp.Web.GuestWeb", false)]
    [InlineData("OssmSharp.Web.Api", true)]
    [InlineData("OssmSharp.Web.AdminWeb", true)]
    [InlineData("OssmSharp.Web.GuestWeb", true)]
    [Trait("Category", "Integration")]
    public void FileLevelsStayAtInformationWhileLauncherRaisesConsoleToWarning(string applicationName, bool standalone)
    {
        var directory = Directory.CreateTempSubdirectory("ossm-web-logging-").FullName;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = applicationName,
                ContentRootPath = directory,
                Args = standalone ? [
                    "--Logging:LogLevel:Default=Trace",
                    "--Logging:Console:LogLevel:Default=Warning",
                    $"--OSSM:FileLogging:Directory={Path.Combine(directory, "logs")}",
                ] : ["--Logging:LogLevel:Default=Information"],
            });
            builder.AddServiceDefaults();
            using (var host = builder.Build())
            {
                var rules = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
                if (standalone)
                {
                    var consoleRule = Assert.Single(rules, rule =>
                        rule.ProviderName == "Console" && rule.CategoryName is null);
                    Assert.Equal(LogLevel.Warning, consoleRule.LogLevel);
                }
                else
                {
                    var defaultRule = Assert.Single(rules, rule =>
                        rule.ProviderName is null && rule.CategoryName is null);
                    Assert.Equal(LogLevel.Information, defaultRule.LogLevel);
                    Assert.DoesNotContain(rules, rule =>
                        rule.ProviderName is "Console" || rule.ProviderName == typeof(ConsoleLoggerProvider).FullName);
                }
                var fileRule = Assert.Single(rules, rule =>
                    rule.ProviderName == typeof(SerilogLoggerProvider).FullName && rule.CategoryName is null);
                Assert.Equal(LogLevel.Information, fileRule.LogLevel);

                var factory = host.Services.GetRequiredService<ILoggerFactory>();
                var logger = factory.CreateLogger("OssmSharp.LoggingTest");
                logger.Log(LogLevel.Trace, new EventId(1), "Trace sentinel", null, (state, _) => state);
                logger.Log(LogLevel.Debug, new EventId(2), "Debug sentinel", null, (state, _) => state);
                LoggerMessage.Define<string>(
                    LogLevel.Information, new EventId(3), "Information sentinel {Application}")(logger, applicationName, null);
                logger.Log(LogLevel.Warning, new EventId(4), "Warning sentinel", null, (state, _) => state);
                logger.Log(LogLevel.Error, new EventId(5), "Error sentinel", null, (state, _) => state);
                logger.Log(LogLevel.Critical, new EventId(6), "Critical sentinel", null, (state, _) => state);

                var frameworkLogger = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
                frameworkLogger.Log(LogLevel.Information, new EventId(7), "Request URL sentinel", null, (state, _) => state);
                frameworkLogger.Log(LogLevel.Warning, new EventId(8), "Framework warning sentinel", null, (state, _) => state);
            }

            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "logs"), "web-*.log"));
            var events = File.ReadAllLines(file).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(
                ["Information", "Warning", "Error", "Fatal", "Warning"],
                events.Select(entry => entry.GetProperty("Level").GetString()));
            Assert.Equal(applicationName, events[0].GetProperty("Properties").GetProperty("Application").GetString());
            Assert.All(events[..4], entry => Assert.Equal(
                "OssmSharp.LoggingTest", entry.GetProperty("Properties").GetProperty("SourceContext").GetString()));
            Assert.Equal(
                "Microsoft.AspNetCore.Hosting.Diagnostics",
                events[4].GetProperty("Properties").GetProperty("SourceContext").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfiguredFileLoggingRequiresANonemptyDirectory()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = ["--OSSM:FileLogging:Directory="],
        });
        Assert.Same(builder, builder.AddFileLogging());

        Assert.Throws<OptionsValidationException>(() => builder.Build());
    }
}
