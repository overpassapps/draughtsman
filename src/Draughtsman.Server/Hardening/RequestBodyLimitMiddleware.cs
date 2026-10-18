using Microsoft.AspNetCore.Http.Features;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Refuses a request body larger than <see cref="RequestBodyLimits"/> allows for its route, with 413 and a problem
/// body, before any handler reads it. A declared <c>Content-Length</c> over the limit is refused without reading a
/// byte; a chunked body, which declares nothing, is cut off the moment it passes the limit. It runs ahead of
/// authentication on purpose: an anonymous caller must not be able to make the server buffer megabytes either.
/// Kestrel enforces the same number natively when it hosts the request; the stream wrapper is what makes the limit hold
/// under any other host (the in-memory test server has no such feature) and gives one answer for both.
/// </summary>
public sealed class RequestBodyLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestBodyLimits _limits;
    private readonly ILogger<RequestBodyLimitMiddleware> _logger;

    public RequestBodyLimitMiddleware(RequestDelegate next, RequestBodyLimits limits, ILogger<RequestBodyLimitMiddleware> logger)
    {
        _next = next;
        _limits = limits;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            await _next(context);
            return;
        }

        long limit = _limits.For(request.Path);
        if (request.ContentLength > limit)
        {
            await Refuse(context, limit);
            return;
        }

        IHttpMaxRequestBodySizeFeature? native = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (native is { IsReadOnly: false })
        {
            native.MaxRequestBodySize = limit;
        }

        if (request.ContentLength is null)
        {
            request.Body = new LimitedReadStream(request.Body, limit);
        }

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await Refuse(context, limit);
        }
    }

    private async Task Refuse(HttpContext context, long limit)
    {
        _logger.LogWarning("Refused a {Method} {Path} body over the {Limit} byte limit for its route", context.Request.Method, context.Request.Path.Value, limit);
        context.Response.Clear();
        await Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Request body too large",
                detail: _limits.Describe(context.Request.Path))
            .ExecuteAsync(context);
    }

    /// <summary>Passes reads through until more than <c>limit</c> bytes have come, then throws the 413 Kestrel itself would.</summary>
    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Count(_inner.Read(buffer, offset, count));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return Count(await _inner.ReadAsync(buffer, cancellationToken));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            if (_read > _limit)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            return read;
        }
    }
}
