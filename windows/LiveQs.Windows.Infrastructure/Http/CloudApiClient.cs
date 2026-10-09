using System.Net.Http.Headers;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Infrastructure.Http;

internal static class CloudApiClient
{
    public const string ClientName = "cloud-sync";

    public static HttpClient Create(IHttpClientFactory factory, AppSettings settings)
    {
        var client = factory.CreateClient(ClientName);
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        return client;
    }
}
