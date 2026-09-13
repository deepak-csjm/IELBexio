using System.Globalization;
using System.Text;
using System.Text.Json;
using IelBexio.Connectors.Bexio.Configuration;

namespace IelBexio.Connectors.Bexio.Conformance;

/// <summary>
/// Renders a conformance report as something a person can act on.
/// <para>
/// The output is deliberately ordered worst-first and states, for each refuted assumption, exactly
/// which file to change. A report that says "7 failures" and leaves you to work out where is not much
/// better than no report.
/// </para>
/// </summary>
public static class ConformanceReportWriter
{
    public static string ToMarkdown(ConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder.AppendLine("# Bexio conformance report")
               .AppendLine()
               .AppendLine(CultureInfo.InvariantCulture, $"Run at **{report.RunAt:u}** against **{report.ClientMode}**")
               .AppendLine();

        if (!report.AgainstLiveApi)
        {
            builder.AppendLine("> **This run did not contact Bexio.** It exercised the mock, which proves the harness")
                   .AppendLine("> itself works but verifies nothing about the real API. Re-run with `Bexio:Mode=Api`")
                   .AppendLine("> against a sandbox to verify the actual assumptions.")
                   .AppendLine();
        }

        builder.AppendLine(report.Passed
            ? "## Result: **PASSED** — no assumption was refuted"
            : $"## Result: **{report.Refuted} assumption(s) REFUTED** — see the work list below");

        builder.AppendLine()
               .AppendLine(CultureInfo.InvariantCulture,
                   $"Confirmed **{report.Confirmed}** · Refuted **{report.Refuted}** · Inconclusive **{report.Inconclusive}** · Skipped **{report.Skipped}**")
               .AppendLine();

        if (report.GrantedScopes.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Granted scopes: `{string.Join(' ', report.GrantedScopes)}`").AppendLine();
        }

        // ---- The work list -------------------------------------------------------------------------
        var refuted = report.Probes.Where(p => p.Outcome == ConformanceOutcome.Refuted).ToList();
        if (refuted.Count > 0)
        {
            builder.AppendLine("## Work list — these assumptions are wrong").AppendLine();

            foreach (var probe in refuted)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"### {probe.Name}")
                       .AppendLine()
                       .AppendLine(CultureInfo.InvariantCulture, $"- **Assumed:** `{probe.Assumption}`")
                       .AppendLine(CultureInfo.InvariantCulture, $"- **Found:** {probe.Detail}")
                       .AppendLine(CultureInfo.InvariantCulture, $"- **Fix in:** {FixLocation(probe.Name)}");

                if (probe.Evidence is not null)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- **Evidence:** `{probe.Evidence}`");
                }

                builder.AppendLine();
            }
        }

        // ---- Worth a look --------------------------------------------------------------------------
        var inconclusive = report.Probes.Where(p => p.Outcome == ConformanceOutcome.Inconclusive).ToList();
        if (inconclusive.Count > 0)
        {
            builder.AppendLine("## Inconclusive — could not be determined").AppendLine();

            foreach (var probe in inconclusive)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- **{probe.Name}** — {probe.Detail}");
            }

            builder.AppendLine();
        }

        // ---- Promotions ----------------------------------------------------------------------------
        var promotable = report.Probes
            .Where(p => p.Outcome == ConformanceOutcome.Confirmed && p.DeclaredStatus != VerificationStatus.VerifiedAgainstOfficialDocs)
            .ToList();

        if (promotable.Count > 0)
        {
            builder.AppendLine(report.AgainstLiveApi
                    ? "## Confirmed — promote these markers"
                    : "## Confirmed against the mock — NOT promotable")
                   .AppendLine()
                   .AppendLine(report.AgainstLiveApi
                       ? "Each of these is currently marked as unverified and has now been observed working against a real " +
                         "Bexio account. Set its `VerificationStatus` to `VerifiedAgainstOfficialDocs` in `BexioEndpoints` / `BexioScopes`."
                       : "These passed against the mock, which says nothing about the real API. Do **not** promote any marker " +
                         "on the strength of this run.")
                   .AppendLine();

            foreach (var probe in promotable)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- **{probe.Name}** — was `{probe.DeclaredStatus}` — {probe.Detail}");
            }

            builder.AppendLine();
        }

        // ---- Everything, for the record -------------------------------------------------------------
        builder.AppendLine("## All probes").AppendLine()
               .AppendLine("| Probe | Declared | Outcome | Detail | ms |")
               .AppendLine("|---|---|---|---|---|");

        foreach (var probe in report.Probes)
        {
            var detail = probe.Detail.Replace("|", "\\|", StringComparison.Ordinal);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {probe.Name} | {probe.DeclaredStatus} | {Symbol(probe.Outcome)} {probe.Outcome} | {detail} | {probe.ElapsedMs} |");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Web defaults (camelCase) so the JSON this writes is the same shape the API returns. Two
    /// different casings for one report would mean any script consuming it has to know which produced
    /// the file.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ToJson(ConformanceReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static string Symbol(ConformanceOutcome outcome) => outcome switch
    {
        ConformanceOutcome.Confirmed => "✅",
        ConformanceOutcome.Refuted => "❌",
        ConformanceOutcome.Inconclusive => "⚠️",
        _ => "⏭️",
    };

    /// <summary>Points at the exact file to edit, so the report is a work list rather than a verdict.</summary>
    private static string FixLocation(string probeName) => probeName switch
    {
        var n when n.StartsWith("Scope:", StringComparison.Ordinal) =>
            "`BexioScopes` in `src/IelBexio.Connectors.Bexio/Configuration/BexioEndpoints.cs`, " +
            "and the `Bexio:Scopes` configuration value",
        "Connection" =>
            "`BexioOptions` (client id/secret, authorization and token endpoints) and the Bexio developer portal app registration",
        _ =>
            "`BexioEndpoints` in `src/IelBexio.Connectors.Bexio/Configuration/BexioEndpoints.cs` for the path, " +
            "and the matching DTO in `src/IelBexio.Connectors.Bexio/Api/BexioApiClient.cs` for field names",
    };
}
