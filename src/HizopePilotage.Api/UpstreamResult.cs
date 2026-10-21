namespace HizopePilotage.Api;

/// <summary>
/// Renvoie telle quelle la réponse d'un service amont (statut, en-têtes utiles, corps) en
/// streamant le corps : indispensable pour les audio/vidéos de vérification, qu'on ne veut ni
/// charger en mémoire ni bufferiser. La réponse amont est libérée en fin d'envoi.
/// </summary>
public sealed class UpstreamResult(HttpResponseMessage upstream) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        using (upstream)
        {
            var response = httpContext.Response;
            response.StatusCode = (int)upstream.StatusCode;

            foreach (var name in GaiaModerationClient.ResponseHeadersToForward)
            {
                if (upstream.Content.Headers.TryGetValues(name, out var contentValues))
                    response.Headers[name] = contentValues.ToArray();
                else if (upstream.Headers.TryGetValues(name, out var headerValues))
                    response.Headers[name] = headerValues.ToArray();
            }

            await upstream.Content.CopyToAsync(response.Body, httpContext.RequestAborted);
        }
    }
}
