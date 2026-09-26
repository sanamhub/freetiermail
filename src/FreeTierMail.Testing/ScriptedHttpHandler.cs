using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail.Testing;

/// <summary>One request seen by a <see cref="ScriptedHttpHandler"/>.</summary>
/// <param name="Request">The request. Its content has been read; use <paramref name="Body"/>.</param>
/// <param name="Body">The request body, buffered as text. Empty when there was none.</param>
/// <param name="ReceivedAt">When the handler received it, on the handler's clock.</param>
public sealed record RecordedRequest(HttpRequestMessage Request, string Body, DateTimeOffset ReceivedAt);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script, in order, and records every
/// request with its body. For provider tests that must not touch the network (ADR-0006).
/// Thread-safe.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly TimeProvider _clock;
    private readonly bool _throwOnAnyRequest;

    /// <summary>Creates a handler with an empty script.</summary>
    /// <param name="clock">Clock for <see cref="RecordedRequest.ReceivedAt"/>. Null means the system clock.</param>
    public ScriptedHttpHandler(TimeProvider? clock = null)
        : this(clock, throwOnAnyRequest: false)
    {
    }

    private ScriptedHttpHandler(TimeProvider? clock, bool throwOnAnyRequest)
    {
        _clock = clock ?? TimeProvider.System;
        _throwOnAnyRequest = throwOnAnyRequest;
    }

    /// <summary>Every request received, in order, including those answered by an exception.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Creates a handler that fails the test on any request, to prove code does no I/O.</summary>
    /// <returns>The handler.</returns>
    public static ScriptedHttpHandler ThrowOnAnyRequest() => new(clock: null, throwOnAnyRequest: true);

    /// <summary>Queues a response with a text body.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The body.</param>
    /// <param name="mediaType">The content type. Defaults to <c>application/json</c>.</param>
    /// <returns>This handler, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="mediaType"/> is null.</exception>
    public ScriptedHttpHandler Respond(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(mediaType);
        _script.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        return this;
    }

    /// <summary>Queues a response built from the request.</summary>
    /// <param name="respond">Builds the response.</param>
    /// <returns>This handler, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="respond"/> is null.</exception>
    public ScriptedHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _script.Enqueue(respond);
        return this;
    }

    /// <summary>Queues an exception, for example an <see cref="HttpRequestException"/> with a <see cref="HttpRequestError"/>.</summary>
    /// <param name="exception">Thrown when the next request arrives.</param>
    /// <returns>This handler, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public ScriptedHttpHandler Throw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _script.Enqueue(_ => throw exception);
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_throwOnAnyRequest)
        {
            throw new InvalidOperationException("This handler allows no request: the code under test did I/O where it must not.");
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(new RecordedRequest(request, body, _clock.GetUtcNow()));
        return _script.TryDequeue(out var next)
            ? next(request)
            : throw new InvalidOperationException("The script has no response left.");
    }
}
