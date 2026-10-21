using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HizopePilotage.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HizopePilotage.Api.Tests;

/// <summary>
/// Vérifie le relais vers la modération de Gaia sans jamais appeler le vrai Gaia : clé partagée
/// envoyée, chemin restreint à api/platform/moderation, méthode/corps/Range transmis, erreurs
/// amont traduites en 502.
/// </summary>
public class GaiaModerationEndpointsTests
{
    private static (WebApplicationFactory<Program> factory, FakeUpstreamHandler handler) CreateFactory(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeUpstreamHandler(respond);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gaia:PlatformKey", "gaia-test-key");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<GaiaModerationClient>(client =>
                        client.BaseAddress = new Uri("https://fake-gaia.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

        return (factory, handler);
    }

    [Fact]
    public async Task ListUsers_ForwardsToPlatformRoute_WithKeyAndQuery()
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/moderation/gaia/users?status=pending");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        Assert.Equal("/api/platform/moderation/users", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?status=pending", handler.LastRequest.RequestUri.Query);
        Assert.Equal("gaia-test-key", handler.LastRequest.Headers.GetValues("X-Platform-Key").Single());
    }

    [Fact]
    public async Task Ban_RelaysPostBodyAndMethod()
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/moderation/gaia/users/3f2c6e0e-0000-0000-0000-000000000001/ban",
            new StringContent("""{"reason":"faux profil"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/platform/moderation/users/3f2c6e0e-0000-0000-0000-000000000001/ban",
            handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Media_ForwardsRangeHeader_AndRelaysPartialContent()
    {
        var (factory, handler) = CreateFactory(_ =>
        {
            var upstream = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
            upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            upstream.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 100);
            return upstream;
        });
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/moderation/gaia/verification-requests/3f2c6e0e-0000-0000-0000-000000000002/media/video");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes=0-3", handler.LastRequest!.Headers.Range!.ToString());
    }

    [Theory]
    [InlineData("a/../../../api/health")]
    [InlineData("%2e%2e/secrets")]
    public async Task UnsafePath_IsRejected_WithoutCallingGaia(string path)
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/moderation/gaia/{path}");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task UpstreamDown_Returns502()
    {
        var (factory, _) = CreateFactory(_ => throw new HttpRequestException("connection refused"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/moderation/gaia/users");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Photo_IsRelayedFromGaiaPublicRoute()
    {
        var (factory, handler) = CreateFactory(_ =>
        {
            var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9]) };
            upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return upstream;
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/moderation/gaia-photos/abc.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/photos/abc.jpg", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.False(handler.LastRequest.Headers.Contains("X-Platform-Key"));
    }

    [Fact]
    public async Task Photo_WithPathTraversal_IsRejected()
    {
        var (factory, handler) = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/moderation/gaia-photos/..%2Fsecret.jpg");

        // Soit refusé, soit relayé comme un simple nom de fichier littéral : dans tous les cas Gaia
        // ne doit jamais recevoir un chemin qui sorte de /photos/.
        if (handler.LastRequest is not null)
        {
            var path = handler.LastRequest.RequestUri!.AbsolutePath;
            Assert.StartsWith("/photos/", path);
            Assert.DoesNotContain("../", path);
        }
    }
}
