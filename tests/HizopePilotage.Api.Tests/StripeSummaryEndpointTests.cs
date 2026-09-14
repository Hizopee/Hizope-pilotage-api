using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HizopePilotage.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HizopePilotage.Api.Tests;

public class StripeSummaryEndpointTests
{
    private static (WebApplicationFactory<Program> factory, FakeUpstreamHandler handler) CreateFactory(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeUpstreamHandler(respond);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Stripe:SecretKey", "sk_live_fake");
            builder.UseSetting("Stripe:TestSecretKey", "sk_test_fake");
            builder.UseSetting("Stripe:ServiceFeePercent", "10");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<StripeReconciliationClient>(client =>
                        client.BaseAddress = new Uri("https://fake-stripe.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

        return (factory, handler);
    }

    private static HttpResponseMessage ChargesPage(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetSummary_ComputesServiceFeeAndPayoutDue_ForLiveByDefault()
    {
        var (factory, handler) = CreateFactory(_ => ChargesPage("""
        {
            "data": [
                { "id": "ch_1", "currency": "eur", "amount": 10000, "amount_refunded": 0, "status": "succeeded", "disputed": false },
                { "id": "ch_2", "currency": "eur", "amount": 5000, "amount_refunded": 2000, "status": "succeeded", "disputed": false },
                { "id": "ch_3", "currency": "eur", "amount": 3000, "amount_refunded": 0, "status": "failed", "disputed": false }
            ],
            "has_more": false
        }
        """));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var eur = body.GetProperty("totals").GetProperty("eur");
        Assert.Equal(2, eur.GetProperty("chargesCount").GetInt32());
        Assert.Equal(130m, eur.GetProperty("grossAmount").GetDecimal());
        Assert.Equal(13m, eur.GetProperty("serviceFeeAmount").GetDecimal());
        Assert.Equal(117m, eur.GetProperty("payoutDue").GetDecimal());
        Assert.Equal(1, body.GetProperty("failedCount").GetInt32());
        Assert.Equal("live", body.GetProperty("environment").GetString());
        Assert.Equal("Bearer sk_live_fake", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetSummary_UsesTestKey_WhenEnvironmentIsTest()
    {
        var (factory, handler) = CreateFactory(_ => ChargesPage("""{ "data": [], "has_more": false }"""));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary?environment=test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer sk_test_fake", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetSummary_WithInvalidEnvironment_Returns400()
    {
        var (factory, _) = CreateFactory(_ => ChargesPage("""{ "data": [], "has_more": false }"""));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary?environment=staging");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_ProducesAlerts_ForFailuresDisputesAndRefunds()
    {
        var (factory, _) = CreateFactory(_ => ChargesPage("""
        {
            "data": [
                { "id": "ch_1", "currency": "eur", "amount": 10000, "amount_refunded": 1000, "status": "succeeded", "disputed": true },
                { "id": "ch_2", "currency": "eur", "amount": 3000, "amount_refunded": 0, "status": "failed", "disputed": false }
            ],
            "has_more": false
        }
        """));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var alerts = body.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("type").GetString()).ToList();
        Assert.Contains("failed_charges", alerts);
        Assert.Contains("disputes", alerts);
        Assert.Contains("refunds", alerts);
    }

    [Fact]
    public async Task GetSummary_RecordsSyncLogEntry_VisibleOnNextCall()
    {
        var (factory, _) = CreateFactory(_ => ChargesPage("""{ "data": [], "has_more": false }"""));
        using var client = factory.CreateClient();

        await client.GetAsync("/api/stripe/cmicrolocks/summary");
        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var syncLog = body.GetProperty("syncLog").EnumerateArray().ToList();
        Assert.True(syncLog.Count >= 2);
        Assert.Equal("success", syncLog[^1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetSummary_WhenStripeUnreachable_Returns502()
    {
        var (factory, _) = CreateFactory(_ => throw new HttpRequestException("connexion refusée"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/cmicrolocks/summary");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
