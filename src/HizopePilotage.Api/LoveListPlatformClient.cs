using System.Text.Json;

namespace HizopePilotage.Api;

/// <summary>
/// Appelle les routes api/platform/* de LoveList-backend (protégées par X-Platform-Key,
/// même principe que CMicrolocks — clé partagée Hizope-pilotage, distincte de
/// l'X-Admin-Key de l'espace admin LoveList et du JWT des utilisateurs). Pas de
/// réconciliation ici : LoveList revient à 100 % à Hizope, rien à reverser — l'argent
/// se suit directement côté Stripe (StripeReconciliationClient, produit "lovelist").
/// </summary>
public class LoveListPlatformClient(HttpClient httpClient, IConfiguration configuration)
{
    private string PlatformKey => configuration["LoveList:PlatformKey"] is { Length: > 0 } key
        ? key
        : throw new InvalidOperationException("LoveList:PlatformKey manquant.");

    /// <summary>Derniers logs applicatifs (tampon en mémoire côté LoveList) — pour voir ce qui se passe sans accès SSH.</summary>
    public async Task<JsonElement> GetLogsAsync(int take, string? level, CancellationToken ct = default)
    {
        var query = $"take={take}" + (string.IsNullOrWhiteSpace(level) ? "" : $"&level={Uri.EscapeDataString(level)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/platform/logs?{query}");
        request.Headers.Add("X-Platform-Key", PlatformKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }
}
