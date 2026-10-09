namespace LiveQs.Windows.Infrastructure.Http;

internal static class HttpRequestDeadline
{
    public static CancellationTokenSource Create(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // ResponseHeadersRead ends HttpClient's timeout at the headers; keep the body under the same deadline.
        if (client.Timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(client.Timeout);
        }
        return deadline;
    }
}
