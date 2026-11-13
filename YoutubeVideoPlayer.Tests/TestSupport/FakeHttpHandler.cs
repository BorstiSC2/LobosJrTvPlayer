using System.Net;
using System.Text;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a delegate and records the requests.
/// (<c>SendAsync</c> is protected, so it cannot be substituted with NSubstitute.)
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, string Path, string Body, string? Authorization);

    public List<Request> Requests { get; } = new();

    /// <summary>Gets the request and returns status code and JSON body.</summary>
    public Func<Request, (HttpStatusCode Status, string Json)> Respond { get; set; } =
        _ => (HttpStatusCode.InternalServerError, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new Request(request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString());
        Requests.Add(recorded);

        var (status, json) = Respond(recorded);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
