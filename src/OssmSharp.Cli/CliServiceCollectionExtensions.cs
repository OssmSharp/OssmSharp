using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal static class CliServiceCollectionExtensions
{
    public static IServiceCollection AddCliCommands(this IServiceCollection services, TextWriter output, TextWriter error)
    {
        services.AddLogging();
        services.AddKeyedSingleton("output", output);
        services.AddKeyedSingleton("error", error);
        services.TryAddSingleton(provider => PromptConsole.FromConsole(
            provider.GetRequiredKeyedService<TextWriter>("output"),
            provider.GetRequiredKeyedService<TextWriter>("error")));
        services.TryAddSingleton<IPromptInput>(_ => PrettyPromptInput.Create(error));
        services.TryAddSingleton<PromptInterruptState>();
        services.TryAddSingleton<PromptHandlerFactory>();

        services.TryAddSingleton<Func<string, IOssmDevice>>(provider => provider.GetRequiredService<Func<string, DeviceBase>>());

        services.AddTransient<ListCommand>();
        services.AddTransient(provider => new PromptCommand(
            provider.GetRequiredService<IDeviceDiscovery>(),
            provider.GetRequiredService<Func<string, IOssmDevice>>(),
            provider.GetRequiredService<PromptHandlerFactory>(),
            provider.GetRequiredKeyedService<TextWriter>("error"),
            provider.GetRequiredService<ILogger<PromptCommand>>()));
        return services;
    }
}
