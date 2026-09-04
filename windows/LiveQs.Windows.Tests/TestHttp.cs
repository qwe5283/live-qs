using System.Net;
using System.Text;
using LiveQs.Windows.Core.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>HTTP message handler capturing the last request and body so tests can assert the wire shape.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public Func<string, string> ResponseFactory { get; set; } = _ => """{"results":[]}""";
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Add(Body ?? "");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseFactory(Body ?? ""), Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}
