using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Device-side HTTP surface of explicit historical reclassification: polling
/// the open task assignment and reporting the outcome counts of a completed
/// pass. Everything here is metadata; the raw context that reclassification
/// re-evaluates never leaves the device.
/// </summary>
public sealed class ReclassificationClient(IHttpClientFactory httpClientFactory, ILogger<ReclassificationClient> logger) : IReclassificationClient
{
    public async Task<ReclassificationAssignment?> GetAssignmentAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(settings);
            using var response = await client.GetAsync(
                "api/v1/classification/reclassification/tasks/assignment", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode == 204) return null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Reclassification assignment poll returned {StatusCode}.", (int)response.StatusCode);
                return null;
            }
            var assignment = await response.Content.ReadFromJsonAsync<Core.Contracts.ReclassificationTaskAssignment>(
                Core.Contracts.ContractJson.Options, cancellationToken);
            if (assignment is null) return null;
            return new ReclassificationAssignment(
                assignment.TaskId,
                assignment.TargetRuleSetVersion,
                ParseInstant(assignment.From),
                ParseInstant(assignment.To));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(exception, "Reclassification assignment poll failed; the task stays deferred.");
            return null;
        }
    }

    public async Task ReportAsync(Guid taskId, ReclassificationReport report, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(settings);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/classification/reclassification/tasks/{taskId.ToString("D", System.Globalization.CultureInfo.InvariantCulture)}/device-reports",
            new Core.Contracts.ReclassificationDeviceReportRequest
            {
                Platform = Core.Contracts.Platform.Windows,
                Scanned = report.Scanned,
                Reclassified = report.Reclassified,
                Unchanged = report.Unchanged,
                Failed = report.Failed,
            },
            Core.Contracts.ContractJson.Options,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 300) body = body[..300];
            throw new HttpRequestException($"重分类进度上报失败 {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }
    }

    private HttpClient CreateClient(AppSettings settings)
    {
        var client = httpClientFactory.CreateClient("cloud-sync");
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        return client;
    }

    private static DateTimeOffset? ParseInstant(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
