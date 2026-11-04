using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HizopePilotage.Api;

/// <summary>
/// Appelle directement l'API Stripe de chaque produit (clé secrète en config, jamais
/// exposée côté Vue) pour le total réellement encaissé, tous canaux confondus, et les
/// virements Stripe vers le compte bancaire. Calcule aussi ce qui revient à Hizope net
/// des frais de service, et garde un court historique de synchro en mémoire (repart à
/// vide au redémarrage, comme InMemoryLogStore côté CMicrolocks — ce n'est pas un
/// historique persistant).
///
/// Produits :
/// - cmicrolocks : config "Stripe:*" — Hizope garde ServiceFeePercent (10 %), le reste est
///   dû à Cécilia.
/// - lovelist : config "LoveList:Stripe:*" — produit 100 % Hizope (abonnements Premium),
///   pas de commission ni de reversement : tout l'encaissé revient à Hizope.
/// </summary>
public class StripeReconciliationClient(HttpClient httpClient, IConfiguration configuration)
{
    private const int MaxSyncLogEntries = 20;
    private static readonly ConcurrentDictionary<string, List<SyncLogEntry>> SyncLogs = new();

    public static readonly string[] Products = ["cmicrolocks", "lovelist"];

    private static string ConfigPrefix(string product) => product switch
    {
        "cmicrolocks" => "Stripe",
        "lovelist" => "LoveList:Stripe",
        _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Produit inconnu."),
    };

    // Part de l'encaissé gardée par Hizope. LoveList : 100 % — aucun reversement à un
    // tiers, donc "frais de service" = tout le brut et "à reverser" = 0.
    private decimal ServiceFeePercentFor(string product) => product switch
    {
        "lovelist" => 100m,
        _ => configuration.GetValue<decimal?>("Stripe:ServiceFeePercent") ?? 10m,
    };

    private string KeyFor(string product, string environment)
    {
        var prefix = ConfigPrefix(product);
        var path = environment switch
        {
            "test" => $"{prefix}:TestSecretKey",
            "live" => $"{prefix}:SecretKey",
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Environnement inconnu (attendu: test | live)."),
        };
        var key = configuration[path];
        return string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException($"{path} manquant.") : key;
    }

    public async Task<StripeSummary> GetSummaryAsync(string product, string environment, CancellationToken ct = default)
    {
        var apiKey = KeyFor(product, environment);
        var timestamp = DateTimeOffset.UtcNow;
        var syncKey = $"{product}:{environment}";

        try
        {
            var (totals, failedCount, disputedCount, refundedTotalCents, chargesFetched) = await FetchChargesAsync(apiKey, ct);

            var feePercent = ServiceFeePercentFor(product);
            var totalsDto = totals.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    // Nos 10% se calculent sur le brut encaissé -- Hizope absorbe les frais Stripe
                    // sur sa propre marge, ça ne change rien à ce qui revient à Cécilia (décision
                    // du 14/09 : "Hizope absorbe les frais Stripe").
                    var serviceFeeCents = (long)Math.Round(kv.Value.GrossCents * feePercent / 100m, MidpointRounding.AwayFromZero);
                    return new StripeCurrencyTotal(
                        ChargesCount: kv.Value.Count,
                        GrossAmount: kv.Value.GrossCents / 100m,
                        ServiceFeeAmount: serviceFeeCents / 100m,
                        StripeFeeAmount: kv.Value.StripeFeeCents / 100m,
                        NetMargin: (serviceFeeCents - kv.Value.StripeFeeCents) / 100m,
                        PayoutDue: (kv.Value.GrossCents - serviceFeeCents) / 100m);
                });

            var alerts = new List<StripeAlert>();
            if (failedCount > 0)
                alerts.Add(new StripeAlert("failed_charges", "warning", $"{failedCount} paiement(s) en échec détecté(s) lors de la dernière synchro."));
            if (disputedCount > 0)
                alerts.Add(new StripeAlert("disputes", "critical", $"{disputedCount} litige(s) (dispute) détecté(s) sur des paiements."));
            if (refundedTotalCents > 0)
                alerts.Add(new StripeAlert("refunds", "info", $"{refundedTotalCents / 100m:0.00} remboursé au total (cumulatif)."));
            foreach (var (currency, total) in totalsDto)
            {
                if (total.NetMargin < 0)
                    alerts.Add(new StripeAlert("negative_margin", "warning",
                        $"Marge nette négative en {currency.ToUpperInvariant()} : les frais Stripe ({total.StripeFeeAmount:0.00}€) dépassent les frais de service retenus ({total.ServiceFeeAmount:0.00}€)."));
            }

            RecordSync(syncKey, new SyncLogEntry(timestamp, "success", chargesFetched, null));

            return new StripeSummary(
                Environment: environment,
                UpdatedAt: timestamp,
                ServiceFeePercent: feePercent,
                Totals: totalsDto,
                ChargesFetched: chargesFetched,
                FailedCount: failedCount,
                DisputedCount: disputedCount,
                RefundedTotal: refundedTotalCents / 100m,
                Alerts: alerts,
                SyncLog: RecentSyncLog(syncKey));
        }
        catch (HttpRequestException ex)
        {
            RecordSync(syncKey, new SyncLogEntry(timestamp, "error", null, ex.Message));
            throw;
        }
    }

    /// <summary>
    /// Virements Stripe -> compte bancaire (payouts) + solde Stripe disponible / en attente
    /// (ce qui partira au prochain virement). Lecture seule : la clé restreinte doit avoir
    /// "Balance: Read" et "Payouts: Read" en plus de "Charges: Read".
    /// </summary>
    public async Task<StripePayouts> GetPayoutsAsync(string product, string environment, int take = 30, CancellationToken ct = default)
    {
        var apiKey = KeyFor(product, environment);

        var balance = await GetStripeAsync(apiKey, "/v1/balance", ct);
        var available = SumByCurrency(balance.GetProperty("available"));
        var pending = SumByCurrency(balance.GetProperty("pending"));

        var limit = Math.Clamp(take, 1, 100);
        var body = await GetStripeAsync(apiKey, $"/v1/payouts?limit={limit}", ct);
        var payouts = body.GetProperty("data").EnumerateArray().Select(p => new StripePayout(
            Id: p.GetProperty("id").GetString()!,
            Amount: p.GetProperty("amount").GetInt64() / 100m,
            Currency: p.GetProperty("currency").GetString()!,
            Status: p.GetProperty("status").GetString()!,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("created").GetInt64()),
            ArrivalDate: DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("arrival_date").GetInt64()),
            FailureMessage: p.TryGetProperty("failure_message", out var fm) && fm.ValueKind == JsonValueKind.String ? fm.GetString() : null))
            .ToList();

        return new StripePayouts(
            Environment: environment,
            UpdatedAt: DateTimeOffset.UtcNow,
            Available: available,
            Pending: pending,
            Payouts: payouts,
            TotalPaid: payouts.Where(p => p.Status == "paid").Sum(p => p.Amount));
    }

    private async Task<JsonElement> GetStripeAsync(string apiKey, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static Dictionary<string, decimal> SumByCurrency(JsonElement funds)
    {
        var result = new Dictionary<string, decimal>();
        foreach (var fund in funds.EnumerateArray())
        {
            var currency = fund.GetProperty("currency").GetString()!;
            result[currency] = result.GetValueOrDefault(currency) + fund.GetProperty("amount").GetInt64() / 100m;
        }
        return result;
    }

    private async Task<(Dictionary<string, (long GrossCents, int Count, long StripeFeeCents)> Totals, int FailedCount, int DisputedCount, long RefundedCents, int ChargesFetched)>
        FetchChargesAsync(string apiKey, CancellationToken ct)
    {
        var totals = new Dictionary<string, (long GrossCents, int Count, long StripeFeeCents)>();
        var failedCount = 0;
        var disputedCount = 0;
        long refundedCents = 0;
        var chargesFetched = 0;
        string? startingAfter = null;

        for (var page = 0; page < 10; page++)
        {
            // expand[]=data.balance_transaction : nécessaire pour avoir les vrais frais Stripe
            // (balance_transaction.fee) sans un appel séparé par charge.
            var query = "limit=100&expand[]=data.balance_transaction"
                + (startingAfter is null ? "" : $"&starting_after={Uri.EscapeDataString(startingAfter)}");
            var body = await GetStripeAsync(apiKey, $"/v1/charges?{query}", ct);

            var data = body.GetProperty("data");
            if (data.GetArrayLength() == 0) break;

            foreach (var charge in data.EnumerateArray())
            {
                chargesFetched++;
                var currency = charge.GetProperty("currency").GetString()!;
                var amount = charge.GetProperty("amount").GetInt64();
                var amountRefunded = charge.GetProperty("amount_refunded").GetInt64();
                var status = charge.GetProperty("status").GetString();
                var disputed = charge.TryGetProperty("disputed", out var disputedProp) && disputedProp.GetBoolean();

                if (status == "succeeded")
                {
                    var (grossCents, count, stripeFeeCents) = totals.TryGetValue(currency, out var existing) ? existing : (0L, 0, 0L);
                    var chargeFeeCents = charge.TryGetProperty("balance_transaction", out var bt) && bt.ValueKind == JsonValueKind.Object
                        ? bt.GetProperty("fee").GetInt64()
                        : 0L;
                    totals[currency] = (grossCents + amount - amountRefunded, count + 1, stripeFeeCents + chargeFeeCents);
                }
                else if (status == "failed")
                {
                    failedCount++;
                }

                if (disputed) disputedCount++;
                if (amountRefunded > 0) refundedCents += amountRefunded;
            }

            if (!body.GetProperty("has_more").GetBoolean()) break;
            startingAfter = data[data.GetArrayLength() - 1].GetProperty("id").GetString();
        }

        return (totals, failedCount, disputedCount, refundedCents, chargesFetched);
    }

    private static void RecordSync(string syncKey, SyncLogEntry entry)
    {
        var log = SyncLogs.GetOrAdd(syncKey, _ => []);
        lock (log)
        {
            log.Add(entry);
            if (log.Count > MaxSyncLogEntries) log.RemoveAt(0);
        }
    }

    private static List<SyncLogEntry> RecentSyncLog(string syncKey)
    {
        if (!SyncLogs.TryGetValue(syncKey, out var log)) return [];
        lock (log)
        {
            return [.. log];
        }
    }
}

public record StripeCurrencyTotal(
    int ChargesCount,
    decimal GrossAmount,
    decimal ServiceFeeAmount,
    decimal StripeFeeAmount,
    decimal NetMargin,
    decimal PayoutDue);

public record StripeAlert(string Type, string Severity, string Message);

public record SyncLogEntry(DateTimeOffset Timestamp, string Status, int? ChargesFetched, string? Error);

public record StripeSummary(
    string Environment,
    DateTimeOffset UpdatedAt,
    decimal ServiceFeePercent,
    Dictionary<string, StripeCurrencyTotal> Totals,
    int ChargesFetched,
    int FailedCount,
    int DisputedCount,
    decimal RefundedTotal,
    List<StripeAlert> Alerts,
    List<SyncLogEntry> SyncLog);

public record StripePayout(
    string Id,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ArrivalDate,
    string? FailureMessage);

public record StripePayouts(
    string Environment,
    DateTimeOffset UpdatedAt,
    Dictionary<string, decimal> Available,
    Dictionary<string, decimal> Pending,
    List<StripePayout> Payouts,
    decimal TotalPaid);
