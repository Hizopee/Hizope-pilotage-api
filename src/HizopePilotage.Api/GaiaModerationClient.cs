using System.Net.Http.Headers;

namespace HizopePilotage.Api;

/// <summary>
/// Relais vers les routes api/platform/moderation/* de Gaia-backend (protégées par X-Platform-Key,
/// voir PlatformKeyFilter côté Gaia). Même principe que CmicrolocksReconciliationClient, mais
/// générique : la modération Gaia expose beaucoup de routes (utilisatrices, signalements, messages,
/// vérifications) et surtout des flux audio/vidéo qu'il faut streamer sans les charger en mémoire.
/// </summary>
public class GaiaModerationClient(HttpClient httpClient, IConfiguration configuration)
{
    private string PlatformKey => configuration["Gaia:PlatformKey"]
        ?? throw new InvalidOperationException("Gaia:PlatformKey manquant.");

    private static readonly string[] ForwardedResponseHeaders =
        ["Content-Type", "Content-Range", "Accept-Ranges", "Content-Length"];

    /// <summary>
    /// Seuls les chemins sous api/platform/moderation sont joignables : le « path » vient de l'URL
    /// appelée, il ne doit jamais permettre d'atteindre une autre route de Gaia (« .. », schéma, etc.).
    /// </summary>
    public static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && !path.Contains(':')
        && !path.Split('/').Any(segment => segment is ".." or ".");

    public async Task<HttpResponseMessage> ForwardAsync(
        HttpMethod method, string relativePath, string query, Stream? body, string? contentType,
        string? rangeHeader, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"/api/platform/moderation/{relativePath}{query}");
        request.Headers.Add("X-Platform-Key", PlatformKey);

        if (!string.IsNullOrEmpty(rangeHeader) && RangeHeaderValue.TryParse(rangeHeader, out var range))
            request.Headers.Range = range;

        if (body is not null && contentType is not null)
        {
            request.Content = new StreamContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        // ResponseHeadersRead : le corps (audio/vidéo) est relayé au fil de l'eau, pas mis en mémoire.
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Photos de profil (/photos/{fichier}) : Gaia ne les sert plus qu'à une personne connectée ou à la clé de plateforme.
    /// Elles sont relayées ici avec X-Platform-Key pour que le navigateur n'ait qu'une seule origine.
    /// </summary>
    public async Task<HttpResponseMessage> GetPhotoAsync(string fileName, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/photos/{Uri.EscapeDataString(fileName)}");
        request.Headers.Add("X-Platform-Key", PlatformKey);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public static IEnumerable<string> ResponseHeadersToForward => ForwardedResponseHeaders;
}
