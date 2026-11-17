namespace FinTrack.Budget.Domain.Buckets;

/// <summary>What a bucket is for (ARCHITECTURE 4.1). Stored by name: never rename a member.</summary>
public enum BucketKind
{
    /// <summary>Money set aside; its <c>Available</c> is never negative.</summary>
    Savings,

    /// <summary>One investment portfolio (4.9); in phase 1a it holds what was invested, without market value.</summary>
    Investment,
}

/// <summary>What an investment bucket holds (ARCHITECTURE 4.1). Stored by name: never rename a member.</summary>
public enum InvestmentProfile
{
    /// <summary>Crypto assets.</summary>
    Crypto,

    /// <summary>Stocks and ETFs.</summary>
    RealValueAssets,
}

/// <summary>Whether a bucket is in use. Stored by name: never rename a member.</summary>
public enum BucketStatus
{
    /// <summary>In use.</summary>
    Active,

    /// <summary>Put away (plan 1a step 9): accepts no new entries and frees its name.</summary>
    Archived,
}

/// <summary>Whether a bucket is created and protected by the system. Stored by name: never rename a member.</summary>
public enum BucketSystemRole
{
    /// <summary>A bucket the user created.</summary>
    None,

    /// <summary>Money that arrived but is not distributed yet; one per currency, created on first use (D5).</summary>
    Unallocated,
}
