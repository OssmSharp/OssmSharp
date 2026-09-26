using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Web.Api.IntegrationTests;

/// <summary>
/// Verifies exclusive device ownership across API processes and rejection of extra listeners.
/// </summary>
public sealed class ApiProcessOwnershipTests
{
    private const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";
    private const uint CreateNewProcessGroup = 0x200;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CtrlBreakEvent = 1;
    private const int Sigterm = 15;

    [Fact]
    public async Task CompetingApiCannotAcquireDuringManagedShutdownButCanStartAfterRelease()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var simulator = new SimulatorBridgeFixture();
        await simulator.InitializeAsync();
        var ownerPort = AvailablePort();
        using var owner = StartApi(ownerPort, simulator.Endpoint, managedShutdown: true);
        try
        {
            await WaitForStartAsync(owner, ownerPort, timeout.Token);
            var (competitorId, rejected) = await RunToExitAsync(AvailablePort(), simulator.Endpoint, timeout.Token);

            Assert.NotEqual(Environment.ProcessId, owner.Id);
            Assert.NotEqual(owner.Id, competitorId);
            Assert.Equal(2, rejected);

            SendShutdown(owner);
            await WaitForListenerStopAsync(owner, ownerPort, timeout.Token);
            Assert.False(owner.HasExited);
            using var heldDuringShutdown = DeviceOwnershipLock.TryAcquire();
            Assert.Null(heldDuringShutdown);

            var (shutdownCompetitorId, rejectedDuringShutdown) = await RunToExitAsync(AvailablePort(), simulator.Endpoint, timeout.Token);
            Assert.NotEqual(owner.Id, shutdownCompetitorId);
            Assert.Equal(2, rejectedDuringShutdown);
            Assert.False(owner.HasExited);

            await owner.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, owner.ExitCode);

            var replacementPort = AvailablePort();
            using var replacement = StartApi(replacementPort, simulator.Endpoint);
            try
            {
                await WaitForStartAsync(replacement, replacementPort, timeout.Token);
                Assert.False(replacement.HasExited);
            }
            finally
            {
                await StopIfRunningAsync(replacement);
            }
        }
        finally
        {
            await StopIfRunningAsync(owner);
        }
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_URLS")]
    public async Task HostingUrlOverrideCannotExposeAnAdditionalListener(string setting)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var simulator = new SimulatorBridgeFixture();
        await simulator.InitializeAsync();
        var configuredPort = AvailablePort();
        var overridePort = AvailablePort();
        using var process = StartApi(configuredPort, simulator.Endpoint, overridePort: overridePort, overrideSetting: setting);
        try
        {
            await WaitForStartAsync(process, configuredPort, timeout.Token);
            using var probe = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await probe.ConnectAsync(IPAddress.Loopback, overridePort, timeout.Token));
            Assert.False(process.HasExited);
        }
        finally
        {
            await StopIfRunningAsync(process);
        }
    }

    private static async Task<(int ProcessId, int ExitCode)> RunToExitAsync(
        int port, string simulatorEndpoint, CancellationToken cancellationToken)
    {
        using var process = StartApi(port, simulatorEndpoint);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (process.Id, process.ExitCode);
        }
        finally
        {
            await StopIfRunningAsync(process);
        }
    }

    private static Process StartApi(int port, string simulatorEndpoint, bool managedShutdown = false,
        int? overridePort = null, string? overrideSetting = null)
    {
        if (managedShutdown && OperatingSystem.IsWindows())
            return StartWindowsProcessGroup(port, simulatorEndpoint);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            },
        };
        process.StartInfo.ArgumentList.Add(ApiAssemblyPath());
        SetEnvironment(process.StartInfo.Environment, port, simulatorEndpoint);
        if (overridePort is { } portValue && overrideSetting is not null)
            process.StartInfo.Environment[overrideSetting] = $"http://0.0.0.0:{portValue}";
        Assert.True(process.Start());
        return process;
    }

    private static async Task WaitForStartAsync(Process process, int port, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        while (!process.HasExited)
        {
            if (await IsHealthyAsync(client, port, cancellationToken))
                return;
            await Task.Delay(50, cancellationToken);
        }
        throw new Xunit.Sdk.XunitException($"Api exited before startup. Exit code: {process.ExitCode}.");
    }

    private static async Task WaitForListenerStopAsync(Process process, int port, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        while (!process.HasExited)
        {
            if (!await IsHealthyAsync(client, port, cancellationToken))
                return;
            await Task.Delay(50, cancellationToken);
        }
        throw new Xunit.Sdk.XunitException($"Api exited before its managed shutdown could be observed. Exit code: {process.ExitCode}.");
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void SendShutdown(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)process.Id))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        else if (Kill(process.Id, Sigterm) != 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static async Task StopIfRunningAsync(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cleanup.Token);
    }

    private static Process StartWindowsProcessGroup(int port, string simulatorEndpoint)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"dotnet \"{ApiAssemblyPath()}\"");
        var environment = new ProcessStartInfo().Environment;
        SetEnvironment(environment, port, simulatorEndpoint);
        var variables = string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        var block = Marshal.StringToHGlobalUni(variables);
        bool started;
        int error;
        ProcessInformation info;
        try
        {
            started = CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
                CreateNewProcessGroup | CreateUnicodeEnvironment,
                block, AppContext.BaseDirectory, ref startup, out info);
            error = Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
        if (!started)
            throw new System.ComponentModel.Win32Exception(error);

        try
        {
            return Process.GetProcessById((int)info.ProcessId);
        }
        finally
        {
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private static void SetEnvironment(IDictionary<string, string?> environment, int port, string simulatorEndpoint)
    {
        environment["DOTNET_ENVIRONMENT"] = Environments.Development;
        environment["OSSM__Control__BindAddress"] = "127.0.0.1";
        environment["OSSM__Control__HostName"] = "127.0.0.1";
        environment["OSSM__Control__ControlPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["OSSM__Control__GuestOrigin"] = $"http://127.0.0.1:{AvailablePort()}";
        environment["OSSM__Control__SimulatorEndpoint"] = simulatorEndpoint;
        environment["OSSM__Control__AdminWebServiceKey"] = AdminKey;
        environment["OSSM__Control__GuestWebServiceKey"] = GuestKey;
    }

    private static string ApiAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "OssmSharp.Web.Api.dll");
        Assert.True(File.Exists(path), "The Web.Api project output must be available to this integration test.");
        return path;
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Matches the Windows process-startup information layout used by the shutdown test.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    /// <summary>
    /// Receives the Windows process and thread handles created for the shutdown test.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
