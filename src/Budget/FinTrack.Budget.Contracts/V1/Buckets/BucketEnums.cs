namespace FinTrack.Budget.Contracts.V1.Buckets;

/// <summary>What a bucket is for. JSON: the member's name.</summary>
public enum BucketKind
{
    /// <summary>Money set aside; never below zero.</summary>
    Savings,

    /// <summary>One investment portfolio.</summary>
    Investment,
}

/// <summary>What an investment bucket holds. JSON: the member's name.</summary>
public enum InvestmentProfile
{
    /// <summary>Crypto assets.</summary>
    Crypto,

    /// <summary>Stocks and ETFs.</summary>
    RealValueAssets,
}

/// <summary>Whether a bucket is in use. JSON: the member's name.</summary>
public enum BucketStatus
{
    /// <summary>In use.</summary>
    Active,

    /// <summary>Put away; accepts no new entries.</summary>
    Archived,
}

/// <summary>Whether the system created and protects a bucket. JSON: the member's name.</summary>
public enum BucketSystemRole
{
    /// <summary>A bucket the user created.</summary>
    None,

    /// <summary>Money that arrived but is not distributed yet; one per currency.</summary>
    Unallocated,
}
