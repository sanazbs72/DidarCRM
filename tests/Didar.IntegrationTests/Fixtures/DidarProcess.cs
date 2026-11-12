using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Didar.IntegrationTests.Fixtures;

public sealed class DidarProcess : IAsyncDisposable
{
    private readonly Process _process;
    private DidarProcess(Process process, Uri address) => (_process, Address) = (process, address);
    public Uri Address { get; }

    public static async Task<DidarProcess> StartAsync(
        string connectionString,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var assembly = typeof(global::Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ConnectionStrings__Didar"] = connectionString;
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Didar API process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var address = new Uri($"http://127.0.0.1:{port}");
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        try
        {
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    using var response = await client.GetAsync($"/requests/{Guid.NewGuid()}");
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new DidarProcess(process, address);
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(100);
                }
            }

            throw new InvalidOperationException(
                $"Didar API process failed to become ready. Exit: {process.ExitCode}. {await error} {await output}");
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            process.Dispose();
            throw;
        }
        finally
        {
            client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(true);
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
