namespace LiveQs.Windows.Core.Sync;

/// <summary>
/// Maps sync exceptions to a stable error code plus a safe, bounded summary
/// for the diagnostics failure history. Exception text is never relayed: it
/// can embed local content (request bodies, file paths) that must not leave
/// the device. Server-side per-item rejection codes (invalid_event,
/// insufficient_scope, ...) are already stable and relayed as-is.
/// </summary>
public static class SyncErrorDescriber
{
    public static (string Code, string Message) Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } when (int)status >= 500
            => ("server_error", $"同步服务返回服务器错误（HTTP {(int)status}）。"),
        HttpRequestException { StatusCode: { } status }
            => ("server_rejected", $"同步服务拒绝了同步请求（HTTP {(int)status}）。"),
        HttpRequestException => ("network_error", "无法连接同步服务。"),
        TaskCanceledException => ("request_timeout", "同步请求超时。"),
        InvalidOperationException => ("invalid_sync_response", "同步服务响应不符合契约。"),
        _ => ("sync_failed", "同步过程中发生未知错误。"),
    };
}
