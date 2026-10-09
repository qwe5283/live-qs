using System.Globalization;
using System.Net.Http.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Device-side HTTP surface of explicit historical reclassification: polling
/// the open task assignment and reporting the outcome counts of a completed
/// pass. Everything here is metadata; the raw context that reclassification
/// re-evaluates never leaves the device.
/// </summary>
public sealed class ReclassificationClient(
    IHttpClientFactory httpClientFactory,
    ILogger<ReclassificationClient> logger) : IReclassificationClient
{
    public async Task<ReclassificationAssignment?> GetAssignmentAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CloudApiClient.Create(httpClientFactory, settings);
            using var deadline = HttpRequestDeadline.Create(client, cancellationToken);
            using var response = await client.GetAsync(
                "api/v1/classification/reclassification/tasks/assignment", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode == 204)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Reclassification assignment poll returned {StatusCode}.", (int)response.StatusCode);
                return null;
            }
            var assignment = await response.Content.ReadFromJsonAsync<Core.Contracts.ReclassificationTaskAssignment>(
                Core.Contracts.ContractJson.Options, deadline.Token);
            if (assignment is null)
            {
                return null;
            }

            return new ReclassificationAssignment(
                assignment.TaskId,
                assignment.TargetRuleSetVersion,
                ParseInstant(assignment.From),
                ParseInstant(assignment.To));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(exception, "Reclassification assignment poll failed; the task stays deferred.");
            return null;
        }
    }

    public async Task ReportAsync(
        Guid taskId,
        ReclassificationReport report,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = CloudApiClient.Create(httpClientFactory, settings);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/classification/reclassification/tasks/{taskId:D}/device-reports",
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
            if (body.Length > 300)
            {
                body = body[..300];
            }

            throw new HttpRequestException($"重分类进度上报失败 {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }
    }

    private static DateTimeOffset? ParseInstant(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
