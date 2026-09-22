using System.Net;
using System.Net.Http;
using AiGateway.Configuration;
using AiGateway.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiGateway.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task CheckBackends_ReturnsHealthyForEveryBackend()
    {
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var backends = new BackendOptions
        {
            [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://openrouter.example/api" },
            ["lmstudio"] = new BackendConfig { BaseUrl = "http://localhost:1234" },
        };

        var results = await HealthEndpoints.CheckBackendsAsync(
            factory,
            backends,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("healthy", result.Status));
        Assert.Equal(
            ["backend-openrouter", "backend-lmstudio"],
            factory.RequestedClientNames);
    }

    [Fact]
    public async Task CheckBackends_TreatsNotFoundAtBaseUrlAsHealthy()
    {
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var backends = new BackendOptions
        {
            ["openrouter"] = new BackendConfig { BaseUrl = "https://openrouter.example/api" },
        };

        var results = await HealthEndpoints.CheckBackendsAsync(
            factory,
            backends,
            NullLogger.Instance,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("healthy", result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckBackends_ReturnsAllResultsAndTreatsHttpErrorResponsesAsReachable()
    {
        var factory = new RecordingHttpClientFactory(clientName =>
            clientName == "backend-lmstudio"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK));
        var backends = new BackendOptions
        {
            [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://openrouter.example/api" },
            ["lmstudio"] = new BackendConfig { BaseUrl = "http://localhost:1234" },
        };

        var results = await HealthEndpoints.CheckBackendsAsync(
            factory,
            backends,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.All(results, result => Assert.Equal("healthy", result.Status));
        Assert.Null(Assert.Single(results, result => result.Backend == "lmstudio").Error);
    }

    [Fact]
    public async Task CheckBackends_UsesBackendUrlForEachRequest()
    {
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var backends = new BackendOptions
        {
            ["first"] = new BackendConfig { BaseUrl = "https://first.example/api" },
            ["second"] = new BackendConfig { BaseUrl = "https://second.example/api" },
        };

        await HealthEndpoints.CheckBackendsAsync(
            factory,
            backends,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(
            [
                ("backend-first", "https://first.example/api"),
                ("backend-second", "https://second.example/api"),
            ],
            factory.Requests);
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<string, HttpResponseMessage> _responseFactory;
        private readonly List<string> _requestedClientNames = [];
        private readonly List<(string ClientName, string Url)> _requests = [];

        public RecordingHttpClientFactory(Func<string, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public IReadOnlyList<string> RequestedClientNames => _requestedClientNames;
        public IReadOnlyList<(string ClientName, string Url)> Requests => _requests;

        public HttpClient CreateClient(string name)
        {
            _requestedClientNames.Add(name);
            return new HttpClient(new RecordingHandler(name, _responseFactory, _requests));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _clientName;
        private readonly Func<string, HttpResponseMessage> _responseFactory;
        private readonly List<(string ClientName, string Url)> _requests;

        public RecordingHandler(
            string clientName,
            Func<string, HttpResponseMessage> responseFactory,
            List<(string ClientName, string Url)> requests)
        {
            _clientName = clientName;
            _responseFactory = responseFactory;
            _requests = requests;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add((_clientName, request.RequestUri!.ToString()));
            return Task.FromResult(_responseFactory(_clientName));
        }
    }
}
