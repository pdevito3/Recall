namespace Recall.Server.Domain.ModelProviders.Clients;

/// <summary>
/// The one place that reads the bytes of each provider, so an adapter and an SDK cannot go around a limit. A wait with no
/// byte ends at <see cref="ProviderLimits.Idle"/>, and an answer above its size ends when the size passes the limit.
/// </summary>
public sealed class ProviderReadGuard(ProviderLimits limits) : DelegatingHandler
{
    private const string StreamMediaType = "text/event-stream";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(limits.Idle);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, wait.Token);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderSilentException();
        }

        var isStream = response.Content.Headers.ContentType?.MediaType == StreamMediaType;
        var byteLimit = isStream ? limits.StreamBytes : limits.BodyBytes;
        var body = new GuardedStream(await response.Content.ReadAsStreamAsync(cancellationToken), limits.Idle, byteLimit, isStream ? limits.StreamLineBytes : long.MaxValue);
        var guarded = new StreamContent(body);
        foreach (var header in response.Content.Headers)
            guarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = guarded;
        return response;
    }

    private sealed class GuardedStream(Stream inner, TimeSpan idle, long byteLimit, long lineLimit) : Stream
    {
        private long _total;
        private long _line;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(idle);
            int read;
            try
            {
                read = await inner.ReadAsync(buffer, wait.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ProviderSilentException();
            }

            Count(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private void Count(ReadOnlySpan<byte> bytes)
        {
            _total += bytes.Length;
            var lastLineEnd = bytes.LastIndexOf((byte)'\n');
            _line = lastLineEnd >= 0 ? bytes.Length - lastLineEnd - 1 : _line + bytes.Length;
            if (_total > byteLimit || _line > lineLimit)
                throw new ProviderAnswerTooLargeException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
