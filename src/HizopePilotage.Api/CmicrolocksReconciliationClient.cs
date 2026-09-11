using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HizopePilotage.Api;

/// <summary>
/// Appelle les routes api/platform/* de CMicrolocks-backend (protégées par
/// X-Platform-Key, voir PlatformKeyFilter côté CMicrolocks). Premier module de
/// Hizope-pilotage — d'autres produits (LoveList, Gaia) exposeront le même genre
/// de route "api/platform/..." et auront leur propre client ici, sur ce modèle.
/// </summary>
public class CmicrolocksReconciliationClient(HttpClient httpClient, IConfiguration configuration)
{
    private string PlatformKey => configuration["CMicrolocks:PlatformKey"]
        ?? throw new InvalidOperationException("CMicrolocks:PlatformKey manquant.");

    public async Task<JsonElement> GetReconciliationAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/reconciliation");
        request.Headers.Add("X-Platform-Key", PlatformKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task RecordReversalAsync(decimal amount, DateTime reversedAt, string? note, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { amount, reversedAt, note });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/reconciliation/reversals")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Platform-Key", PlatformKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
