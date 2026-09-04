using System.Security.Cryptography;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Update;

namespace LiveQs.Windows.Infrastructure.Update;

/// <summary>
/// HTTP surface of the component release channel. Manifest fetches and
/// artifact downloads are plain GETs against the configured channel URL;
/// the download is only returned after its streamed SHA-256 matches the
/// manifest digest, so a tampered or truncated artifact never becomes a
/// "verified" package.
/// </summary>
public sealed class UpdateCheckClient(IHttpClientFactory httpClientFactory) : IUpdateCheckClient
{
    public async Task<string> FetchManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("update-check");
        using var response = await client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<UpdateDownloadResult> DownloadArtifactAsync(
        string downloadUrl, string destinationDirectory, string expectedSha256, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("update-download");
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(destinationDirectory);
        var fileName = ArtifactFileName(downloadUrl);
        var temporaryPath = Path.Combine(destinationDirectory, $"{fileName}.download");
        var finalPath = Path.Combine(destinationDirectory, fileName);

        try
        {
            using var sha256 = SHA256.Create();
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = File.Create(temporaryPath))
            await using (var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write))
            {
                await contentStream.CopyToAsync(hashingStream, cancellationToken);
            }
            var actualSha256 = Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new UpdateCheckException(
                    UpdateCodes.ArtifactHashMismatch,
                    $"下载文件的 SHA-256（{actualSha256}）与清单声明（{expectedSha256}）不一致，已拒绝该更新。");
            }
            File.Move(temporaryPath, finalPath, overwrite: true);
            return new UpdateDownloadResult(finalPath, actualSha256);
        }
        catch
        {
            // A refused or interrupted download never leaves a partial file behind.
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static string ArtifactFileName(string downloadUrl)
    {
        var raw = Uri.UnescapeDataString(new Uri(downloadUrl).AbsolutePath.Split('/')[^1]);
        var clean = new string(raw.Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        return clean.Length > 0 ? clean : "artifact";
    }
}
