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
        services.TryAddSingleton(provider => PromptConsole.FromConsole(
            provider.GetRequiredKeyedService<TextWriter>("output"),
            provider.GetRequiredKeyedService<TextWriter>("error")));
        services.TryAddSingleton<TextReader, ConsoleInputReader>();

        // TODO: registering delegates is bad practice.
        services.TryAddSingleton<Func<string, IOssmDevice>>(provider => provider.GetRequiredService<Func<string, DeviceBase>>());
        services.TryAddSingleton<Func<IOssmDevice, PromptHandler>>(provider => device => new PromptHandler(
            device,
            provider.GetRequiredService<TextReader>(),
            provider.GetRequiredKeyedService<TextWriter>("output"),
            provider.GetRequiredKeyedService<TextWriter>("error"),
            provider.GetRequiredService<PromptConsole>()));

        services.AddTransient<ListCommand>();
        services.AddTransient(provider => new PromptCommand(
            provider.GetRequiredService<DeviceDiscovery>(),
            provider.GetRequiredService<Func<string, IOssmDevice>>(),
            provider.GetRequiredService<Func<IOssmDevice, PromptHandler>>(),
            provider.GetRequiredKeyedService<TextWriter>("error"),
            provider.GetRequiredService<ILogger<PromptCommand>>()));
        return services;
    }
}
