namespace FinTrack.Web.Api;

/// <summary>The answer of <c>GET /api/v1/health</c> when the Api and its database work.</summary>
/// <param name="Status">Always <c>healthy</c>.</param>
/// <param name="CheckedAt">When the Api checked.</param>
public sealed record HealthStatus(string Status, DateTimeOffset CheckedAt);
