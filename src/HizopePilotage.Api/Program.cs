using HizopePilotage.Api;

var builder = WebApplication.CreateBuilder(args);

// Pas d'auth applicative ici : le conteneur n'est jamais exposé directement (expose,
// pas ports, cf. docker-compose.prod.yml), seul Caddy l'atteint, et c'est Caddy qui
// protège tout le site (basic_auth) — voir shared-vps/Caddyfile, repo
// hizope-scaleway-deploy. Ce service ne sert qu'à agréger des routes api/platform/*
// déjà elles-mêmes protégées par clé partagée côté chaque produit (X-Platform-Key).
builder.Services.AddHttpClient<CmicrolocksReconciliationClient>(client =>
{
    var baseUrl = builder.Configuration["CMicrolocks:BaseUrl"] ?? "https://api.cmicrolocks.fr";
    client.BaseAddress = new Uri(baseUrl);
});

const string WebOrigin = "web";
builder.Services.AddCors(options =>
{
    // Même origine en prod (Caddy sert le site + proxifie /api dessus) : CORS n'est là
    // que pour le dev local (site sur :5175, api sur :5080 par ex.).
    options.AddPolicy(WebOrigin, policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors(WebOrigin);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/reconciliation/cmicrolocks", async (CmicrolocksReconciliationClient client, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await client.GetReconciliationAsync(ct));
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            title: "CMicrolocks injoignable ou route non configurée",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/reconciliation/cmicrolocks/reversals", async (
    RecordReversalRequest request, CmicrolocksReconciliationClient client, CancellationToken ct) =>
{
    if (request.Amount <= 0)
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["amount"] = ["Le montant doit être supérieur à 0."],
        });

    try
    {
        await client.RecordReversalAsync(request.Amount, request.ReversedAt, request.Note, ct);
        return Results.Ok();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            title: "CMicrolocks injoignable ou route non configurée",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

public record RecordReversalRequest(decimal Amount, DateTime ReversedAt, string? Note);

// Nécessaire pour que WebApplicationFactory<Program> (tests d'intégration) trouve le
// point d'entrée avec Program.cs en top-level statements.
public partial class Program;
