using System.Net;
using Jellyfin.Plugin.Audiobookshelf.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Jellyfin.Plugin.Audiobookshelf.Tests;

internal sealed class PluginTestContext : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "abs-tests-" + Guid.NewGuid());
    public Mock<IApplicationPaths> Paths { get; } = new();
    public Mock<IHttpClientFactory> HttpFactory { get; } = new();
    public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
    public List<string> Requests { get; } = [];
    public Plugin Plugin { get; }
    public AbsApiClientFactory ClientFactory { get; }

    public PluginTestContext(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        Directory.CreateDirectory(DirectoryPath);
        Paths.SetupGet(p => p.PluginConfigurationsPath).Returns(DirectoryPath);
        Paths.SetupGet(p => p.LogDirectoryPath).Returns(DirectoryPath);
        Paths.SetupGet(p => p.PluginsPath).Returns(DirectoryPath);
        Plugin = new Plugin(Paths.Object, Mock.Of<IXmlSerializer>());
        Plugin.UpdateConfiguration(new PluginConfiguration());
        Plugin.Configuration.AbsServerUrl = "https://abs.example.test";
        Plugin.Configuration.AdminApiToken = "test-token";
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!.PathAndQuery);
            return respond(request);
        });
        HttpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        ClientFactory = new AbsApiClientFactory(HttpFactory.Object, Cache, NullLogger<AbsApiClient>.Instance);
    }

    public static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        Cache.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
