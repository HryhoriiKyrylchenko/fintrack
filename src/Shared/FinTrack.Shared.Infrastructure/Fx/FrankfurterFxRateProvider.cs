using System.Globalization;
using System.Text.Json;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace FinTrack.Shared.Infrastructure.Fx;

/// <summary>
/// <see cref="IFxRateProvider"/> on the Frankfurter API (central-bank reference rates, free, no key)
/// with the rates stored in <c>ref_fx_rates</c> (ARCHITECTURE 3, plan 1a D8).
/// </summary>
/// <remarks>
/// <para>For a pair of currencies:</para>
/// <list type="number">
/// <item><description>the same currency is rate 1, without a call;</description></item>
/// <item><description>if the set fetched most recently was fetched today (UTC; rates have no owner, so no owner's time zone), it is used;</description></item>
/// <item><description>otherwise Frankfurter is asked for every registered currency against EUR, within the HTTP client's
/// time limit (5 s), and a valid answer is stored by its rate date and used;</description></item>
/// <item><description>when that fails (no connection, time-out, an error status, an answer that is not exactly the
/// expected shape), the stored set with the newest rate date is used;</description></item>
/// <item><description>when nothing is stored, the result is <c>Unavailable</c> <see cref="ErrorCodes.FxRateUnavailable"/>.</description></item>
/// </list>
/// <para>
/// On weekends and before the day's publication Frankfurter answers with an earlier rate date; that
/// answer still counts as fetched today. A cross rate goes through EUR and is the quotient of the
/// two EUR rates, unrounded. The caller's cancellation is passed on, never treated as a failure of
/// the source. Logs name the kind of failure and the rate date, never a rate or an amount.
/// </para>
/// </remarks>
public sealed partial class FrankfurterFxRateProvider : IFxRateProvider
{
    private static readonly Currency Base = Currency.Eur;
    private static readonly BsonDocument BaseFilter = new("base", Base.Code);
    private static readonly BsonDocument MostRecentlyFetched = new("fetchedAt", -1);
    private static readonly BsonDocument NewestRateDate = new("date", -1);

    private readonly HttpClient http;
    private readonly ReferenceCollection<FxRateSet> rates;
    private readonly IClock clock;
    private readonly ILogger<FrankfurterFxRateProvider> logger;

    /// <summary>
    /// A provider on <paramref name="http"/>, whose base address is Frankfurter's <c>/v1/</c> and whose
    /// time-out is the limit for one request; <see cref="FxRateServiceCollectionExtensions.AddFxRates"/> sets both.
    /// </summary>
    public FrankfurterFxRateProvider(HttpClient http, ReferenceCollection<FxRateSet> rates, IClock clock, ILogger<FrankfurterFxRateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        this.http = http;
        this.rates = rates;
        this.clock = clock;
        this.logger = logger;
    }

    // The registered currencies except the base, in a stable order: what each request asks for.
    private static IReadOnlyList<Currency> Quoted =>
        [.. Currency.All.Where(currency => currency != Base).OrderBy(currency => currency.Code, StringComparer.Ordinal)];

    /// <inheritdoc />
    public async Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var now = clock.UtcNow;
        if (from == to)
        {
            return Result.Ok(new FxRate(from, to, 1m, DateOnly.FromDateTime(now.UtcDateTime)));
        }

        var lastFetched = await rates.FindFirstAsync(BaseFilter, MostRecentlyFetched, cancellationToken).ConfigureAwait(false);
        if (lastFetched is not null && lastFetched.FetchedAt.UtcDateTime.Date == now.UtcDateTime.Date)
        {
            return Cross(lastFetched, from, to);
        }

        var fetched = await FetchAsync(now, cancellationToken).ConfigureAwait(false);
        if (fetched is not null)
        {
            await rates.UpsertAsync(KeyOf(fetched), fetched, cancellationToken).ConfigureAwait(false);
            return Cross(fetched, from, to);
        }

        var newest = await rates.FindFirstAsync(BaseFilter, NewestRateDate, cancellationToken).ConfigureAwait(false);
        if (newest is null)
        {
            LogNoStoredRates();
            return Unavailable();
        }

        LogUsingStoredRates(newest.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Cross(newest, from, to);
    }

    private static Result<FxRate> Cross(FxRateSet set, Currency from, Currency to) =>
        TryEurRate(set, from, out var fromRate) && TryEurRate(set, to, out var toRate)
            ? Result.Ok(new FxRate(from, to, toRate / fromRate, set.Date))
            : Unavailable();

    // Units of the currency per EUR; a currency registered after the set was stored has none.
    private static bool TryEurRate(FxRateSet set, Currency currency, out decimal rate)
    {
        if (currency == Base)
        {
            rate = 1m;
            return true;
        }

        return set.Rates.TryGetValue(currency.Code, out rate) && rate > 0m;
    }

    private static Result<FxRate> Unavailable() => Result.Fail<FxRate>(AppError.Unavailable(ErrorCodes.FxRateUnavailable));

    private static BsonDocument KeyOf(FxRateSet set) => new()
    {
        { "base", set.Base.Code },
        { "date", set.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
    };

    // The newest rates from Frankfurter, or null (logged) when they cannot be had.
    private async Task<FxRateSet?> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var quoted = Quoted;
        var request = $"latest?base={Base.Code}&symbols={string.Join(',', quoted.Select(currency => currency.Code))}";

        try
        {
            using var response = await http.GetAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogRequestFailed("status", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                var set = Parse(json.RootElement, quoted, now);
                if (set is null)
                {
                    LogRequestFailed("malformed answer", (int)response.StatusCode);
                }

                return set;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogRequestFailed("time-out", 0);
            return null;
        }
        catch (HttpRequestException)
        {
            LogRequestFailed("no connection", 0);
            return null;
        }
        catch (JsonException)
        {
            LogRequestFailed("malformed answer", 0);
            return null;
        }
    }

    // Exactly { base: "EUR", date: "yyyy-MM-dd", rates: { <every quoted code>: number above zero } };
    // other elements (amount, more currencies) are ignored. Anything else is no answer at all.
    private static FxRateSet? Parse(JsonElement root, IReadOnlyList<Currency> quoted, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("base", out var baseCode) || baseCode.ValueKind != JsonValueKind.String || baseCode.GetString() != Base.Code
            || !root.TryGetProperty("date", out var dateText) || dateText.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(dateText.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !root.TryGetProperty("rates", out var published) || published.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var setRates = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var currency in quoted)
        {
            if (!published.TryGetProperty(currency.Code, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDecimal(out var rate)
                || rate <= 0m)
            {
                return null;
            }

            setRates.Add(currency.Code, rate);
        }

        return new FxRateSet(Guid.CreateVersion7(now), FxRateSet.CurrentSchemaVersion, Base, date, setRates, now);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FX rate request failed ({Failure}, HTTP status {StatusCode}).")]
    private partial void LogRequestFailed(string failure, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Using the stored FX rates of {RateDate}.")]
    private partial void LogUsingStoredRates(string rateDate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No FX rates are stored; totals across currencies are left out.")]
    private partial void LogNoStoredRates();
}
