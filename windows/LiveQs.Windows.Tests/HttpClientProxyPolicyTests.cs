using System.Reflection;
using LiveQs.Windows.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace LiveQs.Windows.Tests;

/// <summary>
/// Proxy policy of the collector's outbound HTTP, asserted against the real
/// DI registration. Clients that talk to the Owner-configured server URL
/// must bypass the system proxy entirely (a local accelerator would otherwise
/// intercept the LAN traffic and answer on the server's behalf), while the
/// update channel deliberately keeps the system default so release fetches
/// from GitHub can still benefit from such accelerators.
/// </summary>
public sealed class HttpClientProxyPolicyTests
{
    [Fact]
    public void CloudSyncClient_BypassesSystemProxy()
    {
        using var provider = BuildProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("cloud-sync");

        var handler = Assert.IsAssignableFrom<SocketsHttpHandler>(TerminalHandler(client));

        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("update-check")]
    [InlineData("update-download")]
    public void UpdateClients_KeepDefaultSystemProxy(string clientName)
    {
        using var provider = BuildProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        var handler = Assert.IsAssignableFrom<SocketsHttpHandler>(TerminalHandler(client));

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddLiveQsInfrastructure(new UpdateTestPaths()).BuildServiceProvider();

    /// <summary>Walks the factory pipeline down to the primary handler.</summary>
    private static HttpMessageHandler TerminalHandler(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (HttpMessageHandler?)field?.GetValue(client)
            ?? throw new InvalidOperationException("HttpClient has no handler.");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler
                ?? throw new InvalidOperationException("Handler chain has no terminal handler.");
        }
        return handler;
    }
}
