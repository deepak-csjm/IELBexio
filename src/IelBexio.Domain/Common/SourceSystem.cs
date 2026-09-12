namespace IelBexio.Domain.Common;

/// <summary>Origin of a record. Deliberately a small closed set; adding a source is a deliberate act.</summary>
public enum SourceSystem
{
    Unknown = 0,
    Shopify = 1,
    Amazon = 2,
    DocumentUpload = 3,
    Manual = 4,
}

/// <summary>How a canonical field's value came to hold its current value (§22).</summary>
public enum ValueOrigin
{
    Unknown = 0,

    /// <summary>Copied or deterministically derived from a structured source API payload.</summary>
    SourceApi = 1,

    /// <summary>Produced by deterministic code (arithmetic, rule table, normalisation).</summary>
    Deterministic = 2,

    /// <summary>Extracted from a document by a deterministic parser (CSV/JSON).</summary>
    DocumentParser = 3,

    /// <summary>Originated as an AI proposal that a human subsequently accepted.</summary>
    AiProposalAccepted = 4,

    /// <summary>Entered or corrected by a human.</summary>
    Human = 5,

    /// <summary>Seeded demo/default value.</summary>
    Seed = 6,
}
