using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LiteDM.Core;

namespace LiteDM.App;

internal static class InstanceCoordinator
{
    private static Mutex? _mutex;
    public static bool TryOwn()
    {
        _mutex = new Mutex(true, "Local\\LiteDM.Desktop." + NativeProtocol.PipeName, out var created);
        return created;
    }

    public static async Task SendAsync(NativeRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", NativeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(request, NativeProtocol.JsonOptions));
                _ = await reader.ReadLineAsync(timeout.Token);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                await Task.Delay(250, timeout.Token);
            }
        }
    }
}
