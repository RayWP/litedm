using System.Diagnostics;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using LiteDM.Core;

namespace LiteDM.Bridge;

/// <summary>Chrome/Edge native messaging framing and forwarding to the LiteDM desktop process.</summary>
public static class NativeMessaging
{
    public const int MaxMessageBytes = 64 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PipeConnectAttempt = TimeSpan.FromMilliseconds(300);

    public static async Task<byte[]?> ReadMessageAsync(Stream input, int maximumBytes = MaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        var header = new byte[sizeof(uint)];
        var first = await input.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0)
        {
            return null;
        }
        await ReadExactlyAsync(input, header.AsMemory(1), cancellationToken);
        var length = BitConverter.ToUInt32(header, 0);
        if (length > maximumBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {maximumBytes}-byte limit.");
        }
        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken);
        return payload;
    }

    /// <summary>Loads exact extension origins from the browser manifests installed beside the bridge.</summary>
    public static IReadOnlySet<string> LoadAllowedOrigins()
    {
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in Directory.EnumerateFiles(AppContext.BaseDirectory, "com.litedm.bridge.*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                if (!document.RootElement.TryGetProperty("allowed_origins", out var property)
                    || property.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var origin in property.EnumerateArray())
                {
                    if (origin.ValueKind == JsonValueKind.String && IsWellFormedExtensionOrigin(origin.GetString()))
                    {
                        origins.Add(origin.GetString()!);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged registration manifest contributes no trusted origin.
            }
        }
        return origins;
    }

    /// <summary>Checks the browser-provided origin against exact registered extension origins.</summary>
    public static bool IsAllowedOrigin(string? callerOrigin, IReadOnlySet<string> allowedOrigins) =>
        IsWellFormedExtensionOrigin(callerOrigin) && allowedOrigins.Contains(callerOrigin!);

    private static bool IsWellFormedExtensionOrigin(string? origin) =>
        origin is not null && Regex.IsMatch(origin, @"^chrome-extension://[a-p]{32}/$", RegexOptions.CultureInvariant);

    public static async Task WriteMessageAsync(Stream output, object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, NativeProtocol.JsonOptions);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Native response exceeds the message limit.");
        }
        var header = BitConverter.GetBytes((uint)payload.Length);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>Validates one browser request and returns a user-facing bridge response.</summary>
    public static async Task<NativeResponse> HandleMessageAsync(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default, string? pipeName = null)
    {
        if (payload.Length > MaxMessageBytes)
        {
            return new(false, "The browser message is too large.");
        }
        NativeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<NativeRequest>(payload.Span, NativeProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return new(false, "The browser sent invalid JSON.");
        }

        if (request is null)
        {
            return new(false, "The browser sent an empty request.");
        }
        var action = request.Action?.Trim();
        if (action is not ("addDownload" or "show"))
        {
            return new(false, "LiteDM does not recognize this browser action.");
        }
        if (action == "addDownload" && !NativeProtocol.IsSupportedUrl(request.Url))
        {
            return new(false, "Only valid HTTP and HTTPS links can be sent to LiteDM.");
        }
        if (action == "show" && request.Url is not ("" or null) && !NativeProtocol.IsSupportedUrl(request.Url))
        {
            return new(false, "The browser sent an invalid link.");
        }

        try
        {
            return await ForwardToDesktopAsync(new NativeRequest(action, request.Url ?? ""),
                pipeName ?? NativeProtocol.PipeName, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new(false, "LiteDM did not start in time. Open LiteDM and try again.");
        }
        catch (FileNotFoundException)
        {
            return new(false, "LiteDM is not installed beside the browser bridge. Reinstall LiteDM and try again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return new(false, "Could not connect to LiteDM. Open the app and try again.");
        }
    }

    private static async Task<NativeResponse> ForwardToDesktopAsync(NativeRequest request, string pipeName,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "LiteDM.App.exe");
        var deadline = DateTime.UtcNow + StartupTimeout;
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(StartupTimeout);
        var launched = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
                attempt.CancelAfter(PipeConnectAttempt);
                await pipe.ConnectAsync(attempt.Token);
                var requestLine = JsonSerializer.Serialize(request, NativeProtocol.JsonOptions) + "\n";
                var bytes = Encoding.UTF8.GetBytes(requestLine);
                await pipe.WriteAsync(bytes, totalTimeout.Token);
                await pipe.FlushAsync(totalTimeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                var responseLine = await reader.ReadLineAsync(totalTimeout.Token);
                if (responseLine is null)
                {
                    throw new IOException("LiteDM closed the pipe without replying.");
                }
                return JsonSerializer.Deserialize<NativeResponse>(responseLine, NativeProtocol.JsonOptions)
                    ?? new NativeResponse(false, "LiteDM returned an empty response.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (totalTimeout.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
            catch (OperationCanceledException)
            {
                // The desktop app is not listening yet; start it once and keep trying until the deadline.
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // A just-started app can briefly have its pipe claimed by another request.
            }

            if (!launched)
            {
                if (!File.Exists(executable))
                {
                    throw new FileNotFoundException("LiteDM.App.exe was not found.", executable);
                }
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                Process.Start(start);
                launched = true;
            }
            await Task.Delay(100, totalTimeout.Token);
        }
        throw new TimeoutException();
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The browser closed a truncated native message.");
            }
            offset += read;
        }
    }
}
