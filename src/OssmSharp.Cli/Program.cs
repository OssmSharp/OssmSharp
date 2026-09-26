using OssmSharp.Cli;

if (args is ["__host", "--simulator-path", var simulatorPath, "--pipe-name", var pipeName, "--lock-file", var lockFilePath])
{
    try
    {
        return await SimulatorHost.RunAsync(
            simulatorPath,
            new SimulatorEndpoint(pipeName, lockFilePath),
            CancellationToken.None);
    }
    catch (Exception exception)
    {
        await Console.Error.WriteLineAsync(exception.Message);
        return 1;
    }
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    return await DeviceCommand.InvokeAsync(args, DeviceCommandRunner.RunAsync, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancel;
}
