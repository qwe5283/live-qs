using System.Net;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Classification;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class HttpTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateBodyThatNeverArrives_TimesOutAndRemovesPartialDownload(bool download)
    {
        using var paths = new TestDataPaths();
        using var cancellation = new CancellationTokenSource();
        using var factory = new StalledBodyClientFactory();
        var client = new UpdateCheckClient(factory);
        Task? request = null;
        try
        {
            request = download
                ? (Task)client.DownloadArtifactAsync("http://localhost/package.zip", paths.DataDirectory, new string('0', 64), cancellation.Token)
                : client.FetchManifestAsync("http://localhost/manifest", cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.download"));
        }
        finally
        {
            cancellation.Cancel();
            if (request is not null)
            {
                try
                {
                    await request;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task RuleBodyThatNeverArrives_TimesOutAndKeepsTheCachedRules()
    {
        using var paths = new TestDataPaths();
        using var cancellation = new CancellationTokenSource();
        using var factory = new StalledBodyClientFactory();
        var clock = new TestClock();
        var repository = new SqliteActivityRepository(paths, clock);
        await repository.InitializeAsync();
        var cached = new ClassificationRuleSet
        {
            RuleSetVersion = 7,
            Rules = [],
            Entities = [],
            UpdatedAt = clock.GetUtcNow().ToString("O"),
        };
        await repository.SaveCachedRuleSetAsync(cached, clock.GetUtcNow());
        var refresh = new ClassificationRuleSync(factory, repository, clock, NullLogger<ClassificationRuleSync>.Instance);
        try
        {
            var rules = await refresh.RefreshAsync(new AppSettings(), cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(rules);
            Assert.Equal(cached.RuleSetVersion, rules.RuleSetVersion);
            var persisted = await repository.GetCachedRuleSetAsync();
            Assert.NotNull(persisted);
            Assert.Equal(cached.RuleSetVersion, persisted.RuleSetVersion);
            Assert.Equal(cached.UpdatedAt, persisted.UpdatedAt);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private sealed class StalledBodyClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new StalledBodyHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledBodyStream()),
            });
    }

    private sealed class StalledBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
