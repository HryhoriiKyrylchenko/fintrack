using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Buckets;

/// <summary>One bucket's definition, version 1 of the contract. Balances are part of the overview (<c>GetBudgetOverview</c>), not of this list.</summary>
/// <param name="Id">The bucket's ID (a GUID string in JSON).</param>
/// <param name="Name">The name; <c>null</c> for a system bucket, whose name comes from the UI resources.</param>
/// <param name="Currency">Every amount in the bucket is in this currency; JSON: its code.</param>
/// <param name="Kind">Savings or investment.</param>
/// <param name="Profile">The profile of an investment bucket; <c>null</c> for a savings bucket.</param>
/// <param name="Status">Active or archived.</param>
/// <param name="SystemRole"><c>None</c> for the user's buckets, <c>Unallocated</c> for the system bucket of its currency.</param>
public sealed record BucketDto(
    Guid Id,
    string? Name,
    Currency Currency,
    BucketKind Kind,
    InvestmentProfile? Profile,
    BucketStatus Status,
    BucketSystemRole SystemRole);
