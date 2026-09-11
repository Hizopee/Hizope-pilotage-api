using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HizopePilotage.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HizopePilotage.Api.Tests;

/// <summary>
/// Fausse le HttpMessageHandler du HttpClient typé CmicrolocksReconciliationClient pour
/// ne jamais appeler le vrai api.cmicrolocks.fr pendant les tests — on vérifie juste que
/// Hizope-pilotage-api relaie correctement (en-tête X-Platform-Key envoyé, réponse
/// upstream propagée, erreurs upstream traduites en 502).
/// </summary>
internal class FakeUpstreamHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(respond(request));
    }
}

public class ReconciliationEndpointsTests
{
    private static (WebApplicationFactory<Program> factory, FakeUpstreamHandler handler) CreateFactory(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeUpstreamHandler(respond);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CMicrolocks:PlatformKey", "test-key");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<CmicrolocksReconciliationClient>(client =>
                        client.BaseAddress = new Uri("https://fake-cmicrolocks.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

        return (factory, handler);
    }

    [Fact]
    public async Task Health_Returns200()
    {
        var (factory, _) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReconciliation_RelaysUpstreamBody_WithPlatformKeyHeader()
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"amountDue": 42.5}""", Encoding.UTF8, "application/json"),
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/reconciliation/cmicrolocks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(42.5m, body.GetProperty("amountDue").GetDecimal());
        Assert.Equal("test-key", handler.LastRequest!.Headers.GetValues("X-Platform-Key").Single());
    }

    [Fact]
    public async Task GetReconciliation_WhenUpstreamUnreachable_Returns502()
    {
        var (factory, _) = CreateFactory(_ => throw new HttpRequestException("connexion refusée"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/reconciliation/cmicrolocks");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task RecordReversal_WithZeroAmount_Returns400_WithoutCallingUpstream()
    {
        var called = false;
        var (factory, _) = CreateFactory(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/reconciliation/cmicrolocks/reversals",
            new { amount = 0m, reversedAt = DateTime.UtcNow, note = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task RecordReversal_WithValidAmount_Returns200()
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/reconciliation/cmicrolocks/reversals",
            new { amount = 100m, reversedAt = DateTime.UtcNow, note = "Virement test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }
}
