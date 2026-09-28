namespace OssmSharp.Cli;

internal static class CommandError
{
    internal static async Task<bool> TryWriteAsync(TextWriter error, string message)
    {
        try
        {
            await error.WriteLineAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
