using System.Text.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Update;

namespace LiveQs.Windows.Infrastructure.Update;

/// <summary>
/// Persists the verified package in a small JSON state file so a process
/// restart never re-downloads an already verified release. A corrupted file
/// degrades to "nothing verified", which only costs one re-download.
/// </summary>
public sealed class UpdateStateStore(IAppPaths paths) : IUpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly VerifiedPackage Empty = new(null, null, null);

    private string FilePath => Path.Combine(paths.DataDirectory, "update-state.json");

    public async Task<VerifiedPackage> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            return JsonSerializer.Deserialize<VerifiedPackage>(json, JsonOptions) ?? Empty;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return Empty;
        }
    }

    public async Task SaveAsync(VerifiedPackage verified, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(verified, JsonOptions);
        await File.WriteAllTextAsync(FilePath, json, cancellationToken);
    }
}
