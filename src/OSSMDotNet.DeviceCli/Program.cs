using OSSMDotNet.DeviceCli;

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

return await DeviceCommand.InvokeAsync(args);
