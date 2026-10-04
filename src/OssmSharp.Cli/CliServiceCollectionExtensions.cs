using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime;
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

        // TODO: these probably should be interfaces, not concrete singletons.
        services.TryAddSingleton<PromptInterruptState>();
        services.TryAddSingleton<PromptHandlerFactory>();

        services.AddTransient<ListCommand>();
        services.AddTransient<PromptCommand>();

        return services;
    }
}
