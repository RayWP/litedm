using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LiteDM.Bridge;
using LiteDM.Core;

namespace LiteDM.Tests;

public sealed class NativeMessagingTests
{
    [Fact]
    public async Task ReadsChromeFrameLittleEndianEvenWhenStreamReturnsShortReads()
    {
        var payload = Encoding.UTF8.GetBytes("{\"action\":\"addDownload\",\"url\":\"https://example.test/file.zip\"}");
        var frame = Frame(payload);
        await using var stream = new ShortReadStream(frame, 2);

        var result = await NativeMessaging.ReadMessageAsync(stream);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task EmptyInputHasNoMessage()
    {
        var result = await NativeMessaging.ReadMessageAsync(new MemoryStream());
        Assert.Null(result);
    }

    [Fact]
    public void CallerOriginMustExactlyMatchAWellFormedExtensionOrigin()
    {
        const string allowed = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
        IReadOnlySet<string> allowlist = new HashSet<string>([allowed], StringComparer.Ordinal);

        Assert.True(NativeMessaging.IsAllowedOrigin(allowed, allowlist));
        Assert.False(NativeMessaging.IsAllowedOrigin(null, allowlist));
        Assert.False(NativeMessaging.IsAllowedOrigin("chrome-extension://abcdefghijklmnopabcdefghijklmnop/extra", allowlist));
        Assert.False(NativeMessaging.IsAllowedOrigin("chrome-extension://*", allowlist));
        Assert.False(NativeMessaging.IsAllowedOrigin("https://abcdefghijklmnopabcdefghijklmnop/", allowlist));
    }

    [Fact]
    public async Task TruncatedHeaderAndBodyAreRejected()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadMessageAsync(new MemoryStream([0x01, 0x00])));
        var truncatedBody = new byte[] { 3, 0, 0, 0, (byte)'{' };
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadMessageAsync(new MemoryStream(truncatedBody)));
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforePayloadAllocation()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, NativeMessaging.MaxMessageBytes + 1u);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadMessageAsync(new MemoryStream(header)));
    }

    [Fact]
    public async Task WritesLittleEndianLengthAndJsonPayload()
    {
        using var output = new MemoryStream();
        var response = new NativeResponse(false, "No connection");
        await NativeMessaging.WriteMessageAsync(output, response);

        var bytes = output.ToArray();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.Equal((uint)(bytes.Length - 4), size);
        Assert.Equal(response, JsonSerializer.Deserialize<NativeResponse>(bytes.AsSpan(4), NativeProtocol.JsonOptions));
    }

    [Fact]
    public async Task RejectsInvalidJsonUnknownActionAndUnsupportedSchemes()
    {
        var invalidJson = await NativeMessaging.HandleMessageAsync(Encoding.UTF8.GetBytes("{"));
        var unknownAction = await NativeMessaging.HandleMessageAsync(Encoding.UTF8.GetBytes("{\"action\":\"remove\",\"url\":\"https://example.test\"}"));
        var unsupportedScheme = await NativeMessaging.HandleMessageAsync(Encoding.UTF8.GetBytes("{\"action\":\"addDownload\",\"url\":\"file:///C:/secret.txt\"}"));

        Assert.False(invalidJson.Accepted);
        Assert.Contains("invalid JSON", invalidJson.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(unknownAction.Accepted);
        Assert.Contains("action", unknownAction.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(unsupportedScheme.Accepted);
        Assert.Contains("HTTP", unsupportedScheme.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardsValidRequestToDesktopPipeAndReturnsItsAcknowledgement()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            Assert.NotNull(line);
            var forwarded = JsonSerializer.Deserialize<NativeRequest>(line!, NativeProtocol.JsonOptions);
            Assert.Equal("addDownload", forwarded!.Action);
            Assert.Equal("https://example.test/file.zip", forwarded.Url);
            var bytes = Encoding.UTF8.GetBytes("{\"accepted\":true}\n");
            await server.WriteAsync(bytes);
            await server.FlushAsync();
        });

        var response = await NativeMessaging.HandleMessageAsync(Encoding.UTF8.GetBytes(
            "{\"action\":\"addDownload\",\"url\":\"https://example.test/file.zip\"}"), pipeName: pipeName);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(response.Accepted);
    }

    [Fact]
    public async Task HangingDesktopReplyIsBoundedByCallerCancellation()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync());
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await NativeMessaging.HandleMessageAsync(ValidRequest(), timeout.Token, pipeName);
        watch.Stop();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(response.Accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Forwarding took {watch.Elapsed}.");
    }

    [Fact]
    public async Task MalformedDesktopReplyReturnsFailureInsteadOfHanging()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync());
            await server.WriteAsync(Encoding.UTF8.GetBytes("not-json\n"));
            await server.FlushAsync();
        });

        var response = await NativeMessaging.HandleMessageAsync(ValidRequest(), pipeName: pipeName);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(response.Accepted);
        Assert.Contains("connect", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewPipeName() => "LiteDMTest-" + Guid.NewGuid().ToString("N");

    private static byte[] ValidRequest() => Encoding.UTF8.GetBytes(
        "{\"action\":\"addDownload\",\"url\":\"https://example.test/file.zip\"}");

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private sealed class ShortReadStream(byte[] bytes, int maxRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
    }
}
