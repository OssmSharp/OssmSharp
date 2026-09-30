using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal static class CliServiceCollectionExtensions
{
    public static IServiceCollection AddCliCommands(this IServiceCollection services, TextWriter output, TextWriter error)
    {
        // TODO: replace with HostApplicationBuilder constructs
        services.AddLogging();
        services.AddKeyedSingleton("output", output);
        services.AddKeyedSingleton("error", error);
        services.TryAddSingleton<IPromptConsole>(provider => PromptConsole.FromConsole(
            provider.GetRequiredKeyedService<TextWriter>("output"),
            provider.GetRequiredKeyedService<TextWriter>("error")));
        services.TryAddSingleton<TextReader, ConsoleInputReader>();

        // TODO: registering delegates is bad practice.
        services.TryAddSingleton<Func<string, IOssmDevice>>(provider => provider.GetRequiredService<Func<string, DeviceBase>>());
        services.TryAddSingleton<Func<IOssmDevice, IPromptHandler>>(provider => device => new PromptHandler(
            device,
            provider.GetRequiredService<TextReader>(),
            provider.GetRequiredKeyedService<TextWriter>("output"),
            provider.GetRequiredKeyedService<TextWriter>("error"),
            provider.GetRequiredService<IPromptConsole>()));

        services.AddTransient<ListCommand>();
        services.AddTransient(provider => new PromptCommand(
            provider.GetRequiredService<IDeviceDiscovery>(),
            provider.GetRequiredService<Func<string, IOssmDevice>>(),
            provider.GetRequiredService<Func<IOssmDevice, IPromptHandler>>(),
            provider.GetRequiredKeyedService<TextWriter>("error"),
            provider.GetRequiredService<ILogger<PromptCommand>>()));
        return services;
    }
}
