using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiteDM.Tests;

internal sealed class HttpFixture : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly byte[] _body;

    public HttpFixture(byte[] body, bool supportsRanges = true, bool chunked = false)
    {
        _body = body;
        SupportsRanges = supportsRanges;
        Chunked = chunked;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = ServeAsync();
    }

    public string BaseUrl { get; }
    public bool SupportsRanges { get; }
    public bool Chunked { get; }
    public string? ETag { get; set; } = "\"v1\"";
    public string LastModified { get; set; } = "Wed, 21 Oct 2015 07:28:00 GMT";
    public string? RedirectLocation { get; set; }
    public HttpStatusCode? ForcedStatus { get; set; }
    public int DropFirstFullResponseAfterBytes;
    public int RequestCount;
    public int RangeCount;
    public int ActiveRequests;
    public int MaximumActiveRequests;
    public int DelayPerChunkMs { get; set; }
    public bool IgnoreRanges => !SupportsRanges;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            _ = RespondAsync(context);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref RequestCount);
        var countedTransfer = false;
        try
        {
            var request = context.Request;
            var response = context.Response;
            if (RedirectLocation is not null)
            {
                response.RedirectLocation = RedirectLocation;
                response.StatusCode = (int)HttpStatusCode.Redirect;
                response.Close();
                return;
            }
            if (ForcedStatus is { } forced)
            {
                response.StatusCode = (int)forced;
                response.Close();
                return;
            }
            response.Headers[HttpResponseHeader.ETag] = ETag;
            response.Headers[HttpResponseHeader.LastModified] = LastModified;
            var range = request.Headers["Range"];
            if (SupportsRanges && range?.StartsWith("bytes=", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref RangeCount);
                if (_body.Length == 0)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.Headers[HttpResponseHeader.ContentRange] = "bytes */0";
                    response.Close();
                    return;
                }
                var parts = range[6..].Split('-');
                var from = long.Parse(parts[0]);
                var to = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : _body.Length - 1L;
                to = Math.Min(to, _body.Length - 1L);
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.Headers[HttpResponseHeader.ContentRange] = $"bytes {from}-{to}/{_body.Length}";
                response.ContentLength64 = to - from + 1;
                if (!request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteBodyAsync(response, from, to - from + 1);
                }
                else
                {
                    response.Close();
                }
            }
            else
            {
                countedTransfer = true;
                UpdateMaximum(Interlocked.Increment(ref ActiveRequests));
                response.StatusCode = (int)HttpStatusCode.OK;
                if (Chunked)
                {
                    response.SendChunked = true;
                }
                else
                {
                    response.ContentLength64 = _body.Length;
                }
                if (!request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    var drop = Interlocked.Exchange(ref DropFirstFullResponseAfterBytes, 0);
                    if (drop > 0)
                    {
                        await response.OutputStream.WriteAsync(_body.AsMemory(0, Math.Min(drop, _body.Length)), _stop.Token);
                        response.Abort();
                    }
                    else
                    {
                        await WriteBodyAsync(response, 0, _body.Length);
                    }
                }
                else
                {
                    response.Close();
                }
            }
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
        finally
        {
            if (countedTransfer)
            {
                Interlocked.Decrement(ref ActiveRequests);
            }
        }
    }

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref MaximumActiveRequests);
            if (value <= current || Interlocked.CompareExchange(ref MaximumActiveRequests, value, current) == current)
            {
                return;
            }
        }
    }

    private async Task WriteBodyAsync(HttpListenerResponse response, long start, long count)
    {
        const int chunk = 65536;
        while (count > 0)
        {
            var n = (int)Math.Min(chunk, count);
            await response.OutputStream.WriteAsync(_body.AsMemory((int)start, n), _stop.Token);
            await response.OutputStream.FlushAsync(_stop.Token);
            start += n;
            count -= n;
            if (DelayPerChunkMs > 0)
            {
                await Task.Delay(DelayPerChunkMs, _stop.Token);
            }
        }
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
