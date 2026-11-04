using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HizopePilotage.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HizopePilotage.Api.Tests;

public class LoveListEndpointsTests
{
    private static (WebApplicationFactory<Program> factory, FakeUpstreamHandler handler) CreateFactory(
        Func<HttpRequestMessage, HttpResponseMessage> respond, bool withKeys = true)
    {
        var handler = new FakeUpstreamHandler(respond);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (withKeys)
            {
                builder.UseSetting("LoveList:PlatformKey", "lovelist-key");
                builder.UseSetting("LoveList:Stripe:SecretKey", "sk_live_lovelist");
                builder.UseSetting("LoveList:Stripe:TestSecretKey", "sk_test_lovelist");
            }
            // Clé CMicrolocks différente : vérifie qu'on n'utilise jamais la mauvaise.
            builder.UseSetting("Stripe:SecretKey", "sk_live_cmicrolocks");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<StripeReconciliationClient>(client =>
                        client.BaseAddress = new Uri("https://fake-stripe.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddHttpClient<LoveListPlatformClient>(client =>
                        client.BaseAddress = new Uri("https://fake-lovelist.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

        return (factory, handler);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetSummary_KeepsAllRevenue_NoCommissionNoPayoutDue()
    {
        var (factory, handler) = CreateFactory(_ => Json("""
        {
            "data": [
                {
                    "id": "ch_1", "currency": "eur", "amount": 499, "amount_refunded": 0,
                    "status": "succeeded", "disputed": false,
                    "balance_transaction": { "fee": 32, "net": 467 }
                },
                {
                    "id": "ch_2", "currency": "eur", "amount": 4990, "amount_refunded": 0,
                    "status": "succeeded", "disputed": false,
                    "balance_transaction": { "fee": 100, "net": 4890 }
                }
            ],
            "has_more": false
        }
        """));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/lovelist/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var eur = body.GetProperty("totals").GetProperty("eur");
        Assert.Equal(100m, body.GetProperty("serviceFeePercent").GetDecimal());
        Assert.Equal(54.89m, eur.GetProperty("grossAmount").GetDecimal());
        Assert.Equal(1.32m, eur.GetProperty("stripeFeeAmount").GetDecimal());
        // Tout revient à Hizope : net = brut - frais Stripe, rien à reverser.
        Assert.Equal(53.57m, eur.GetProperty("netMargin").GetDecimal());
        Assert.Equal(0m, eur.GetProperty("payoutDue").GetDecimal());
        Assert.Equal("Bearer sk_live_lovelist", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetSummary_UsesLoveListTestKey_WhenEnvironmentIsTest()
    {
        var (factory, handler) = CreateFactory(_ => Json("""{ "data": [], "has_more": false }"""));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/lovelist/summary?environment=test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer sk_test_lovelist", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetSummary_WhenKeyMissing_Returns503_WithoutCallingStripe()
    {
        var called = false;
        var (factory, _) = CreateFactory(_ => { called = true; return Json("{}"); }, withKeys: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/lovelist/summary");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task GetSummary_UnknownProduct_Returns404()
    {
        var (factory, _) = CreateFactory(_ => Json("{}"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/gaia/summary");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPayouts_ReturnsBalanceAndPayouts()
    {
        var (factory, handler) = CreateFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/balance" => Json("""
                {
                    "available": [{ "amount": 1250, "currency": "eur" }],
                    "pending": [{ "amount": 499, "currency": "eur" }]
                }
                """),
            "/v1/payouts" => Json("""
                {
                    "data": [
                        { "id": "po_2", "amount": 3000, "currency": "eur", "status": "in_transit",
                          "created": 1790000000, "arrival_date": 1790172800, "failure_message": null },
                        { "id": "po_1", "amount": 5000, "currency": "eur", "status": "paid",
                          "created": 1789000000, "arrival_date": 1789172800, "failure_message": null }
                    ],
                    "has_more": false
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/lovelist/payouts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12.5m, body.GetProperty("available").GetProperty("eur").GetDecimal());
        Assert.Equal(4.99m, body.GetProperty("pending").GetProperty("eur").GetDecimal());
        var payouts = body.GetProperty("payouts").EnumerateArray().ToList();
        Assert.Equal(2, payouts.Count);
        Assert.Equal("in_transit", payouts[0].GetProperty("status").GetString());
        Assert.Equal(30m, payouts[0].GetProperty("amount").GetDecimal());
        Assert.Equal(50m, body.GetProperty("totalPaid").GetDecimal());
        Assert.Equal("Bearer sk_live_lovelist", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetPayouts_WhenStripeUnreachable_Returns502()
    {
        var (factory, _) = CreateFactory(_ => throw new HttpRequestException("connexion refusée"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/stripe/lovelist/payouts");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_RelaysUpstreamBody_WithLoveListPlatformKey()
    {
        var (factory, handler) = CreateFactory(_ => Json(
            """[{"timestamp":"2026-10-05T10:00:00Z","level":"Error","category":"LoveList.Api.X","message":"boom","exception":null}]"""));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/logs/lovelist?take=50&level=Error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("boom", body[0].GetProperty("message").GetString());
        Assert.Equal("lovelist-key", handler.LastRequest!.Headers.GetValues("X-Platform-Key").Single());
        Assert.Equal("fake-lovelist.test", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal("/api/platform/logs", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("level=Error", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task GetLogs_WhenUpstreamUnreachable_Returns502()
    {
        var (factory, _) = CreateFactory(_ => throw new HttpRequestException("connexion refusée"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/logs/lovelist");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_WhenPlatformKeyMissing_Returns503()
    {
        var (factory, _) = CreateFactory(_ => Json("[]"), withKeys: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/logs/lovelist");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
