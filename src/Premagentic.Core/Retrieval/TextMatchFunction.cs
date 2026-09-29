using System.Text.RegularExpressions;
using Premagentic.Core.Retrieval.Gates;
using Npgsql;

namespace Premagentic.Core.Retrieval;

/// <summary>
/// <c>prem_index.text_matches</c>: the text leg's match, rank, gates, exclusions
/// and limit in one function that runs as the owner. Its signature is written
/// in migration 0007; its body is generated here from <see cref="GateSet.Default"/>
/// and installed by the owner at the end of every migrate, so the gates it
/// applies are the ones every other gated read applies, from the same text.
/// <para>
/// Why it exists: under any row policy on a table, PostgreSQL will not use an
/// index for a condition whose operator is not leakproof, and the full-text
/// match is not, so a text search written in the read itself would scan every
/// chunk. As the owner the index is used. The function applies the bound
/// caller session's lists itself, whatever its caller passes, so it is the
/// second line for the text leg as the policies are for every other read.
/// </para>
/// <para>
/// Why the gates, the order and the limit are inside: otherwise a query that
/// matches most chunks hands every match out of the function to be sorted
/// afterwards, on one core. Inside, PostgreSQL sorts to the top rows in
/// parallel workers, as the read did before the policies. The query runs with
/// EXECUTE, so each call is planned with its own values.
/// </para>
/// <para>
/// Why the access lists appear once: the session's lists and the access gate's
/// lists are the same lists for every caller the product makes, and PostgreSQL
/// multiplies the selectivities of two conditions it cannot tell apart, so it
/// halved its estimate of the rows a bound caller reads and dropped the
/// parallel plan (about twice the time of an exempt caller at 100,000 chunks).
/// So the function computes the lists it enforces once: the session's lists cut
/// down to the caller's, the session's alone for a caller that claims every
/// list, or the caller's alone where the session reads the whole index. They
/// go into the session condition, whose text the generator never takes from a
/// gate, and the access gate is given its unrestricted value, so its own
/// condition folds away. Either side still restricts without the other: a
/// caller that claims every list still gets only its session's, and a session
/// that reads the whole index still gets only the caller's lists.
/// </para>
/// <para>
/// Adding a gate, or a parameter to one, changes the signature: that needs a
/// migration that drops the function and creates it with the new arguments,
/// and migrate refuses to install a body its signature does not fit. Changing
/// a gate's SQL needs no migration: the next migrate installs the new body, and
/// until it has, the application role refuses to start, as for any pending
/// migration.
/// </para>
/// </summary>
internal static partial class TextMatchFunction
{
    public const string Name = "prem_index.text_matches";

    // The query's own placeholders: $1 the text query, $2 the tenant, $3 and $4
    // the caller; the gates' parameters follow, then the exclusions and the limit.
    private const int FirstGatePosition = 5;

    /// <summary>The gates it applies. A read that calls it adds these gates' parameters.</summary>
    public static GateSet Gates => GateSet.Default;

    /// <summary>The parameters the gates read, in the order their SQL first names them.</summary>
    public static IReadOnlyList<string> GateParameters { get; } = ParametersOf(GateSet.Default);

    /// <summary>The body migrate installs.</summary>
    public static string Body { get; } = Render(GateSet.Default);

    /// <summary>
    /// The call, for a read that adds <c>@tenant</c>, the gates' parameters,
    /// <c>@exclude</c> and <c>@limit</c>. Named arguments, so the call does not
    /// depend on the order the migration declares them in.
    /// </summary>
    public static string Call(string tsquery) =>
        $"{Name}(p_tenant => @tenant, p_query => {tsquery}, " +
        string.Concat(GateParameters.Select(p => $"g_{p} => @{p}, ")) +
        "p_exclude => @exclude, p_limit => @limit)";

    internal static IReadOnlyList<string> ParametersOf(GateSet gates) =>
        Parameter().Matches(gates.Sql).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The input arguments the function must declare for <paramref name="gates"/>, in the order the body uses them.</summary>
    internal static IReadOnlyList<string> ArgumentsFor(GateSet gates) =>
        ["p_tenant", "p_query", .. ParametersOf(gates).Select(p => "g_" + p), "p_exclude", "p_limit"];

    /// <summary>
    /// The body for <paramref name="gates"/>: deterministic, with LF line ends,
    /// so a change to any gate's SQL shows as a change to this text.
    /// </summary>
    internal static string Render(GateSet gates)
    {
        // The gates' SQL goes inside a dollar-quoted string, and its parameters
        // become positions; a dollar sign of its own could end the string early.
        if (gates.Sql.Contains('$'))
            throw new InvalidOperationException("A gate's SQL contains '$', which the text match function cannot quote.");

        var parameters = ParametersOf(gates);
        if (!parameters.Contains(AccessGate.UnrestrictedParameter) || !parameters.Contains(AccessGate.PermittedParameter))
            throw new InvalidOperationException(
                "The text match function folds the access gate into its session condition, and these gates have no access gate.");
        const string unrestricted = "g_" + AccessGate.UnrestrictedParameter;
        const string permitted = "g_" + AccessGate.PermittedParameter;

        var position = parameters
            .Select((p, i) => (Name: p, Position: FirstGatePosition + i))
            .ToDictionary(x => x.Name, x => x.Position, StringComparer.Ordinal);
        var gateSql = Parameter().Replace(gates.Sql, m => "$" + position[m.Groups[1].Value]);
        var gateLines = gateSql.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var exclude = FirstGatePosition + parameters.Count;
        string[] arguments =
        [
            "p_query", "p_tenant", "reads_all", "effective",
            .. parameters.Select(p => p == AccessGate.UnrestrictedParameter ? "TRUE" : "g_" + p),
            "p_exclude", "p_limit",
        ];

        string[] lines =
        [
            "",
            "DECLARE",
            "    whole BOOLEAN := prem_config.reads_whole_index(session_user)",
            "        OR EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname = session_user AND (r.rolsuper OR r.rolbypassrls));",
            "    lists BIGINT[] := prem_config.caller_acl_sets();",
            $"    reads_all BOOLEAN := whole AND {unrestricted};",
            "    effective BIGINT[] := CASE",
            $"        WHEN whole THEN {permitted}",
            $"        WHEN {unrestricted} THEN lists",
            $"        ELSE ARRAY(SELECT l FROM unnest(lists) l WHERE l = ANY ({permitted}))",
            "    END;",
            "BEGIN",
            "    RETURN QUERY EXECUTE $query$",
            "        SELECT c.id, c.document_id, c.seq, ts_rank_cd(c.tsv, $1) AS rank, d.path",
            "        FROM prem_index.chunk c",
            "        JOIN prem_index.document d ON d.id = c.document_id",
            "        WHERE d.tenant_id = $2",
            "          AND c.tsv @@ $1",
            "          AND ($3 OR d.acl_set_id = ANY ($4))",
            .. gateLines.Select((l, i) => i == 0 ? "          AND " + l : "          " + l),
            $"          AND NOT (c.id = ANY (${exclude}))",
            "        ORDER BY rank DESC, d.path, c.seq",
            $"        LIMIT ${exclude + 1}",
            "    $query$",
            "    USING " + string.Join(", ", arguments) + ";",
            "END",
            "",
        ];
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Installs <see cref="Body"/>, as the owner inside migrate's lock, when the
    /// installed body differs. The signature stays the migration's, and CREATE
    /// OR REPLACE keeps the owner and who may execute it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The function exists more than once, or its arguments do not fit the gates.</exception>
    internal static async Task InstallAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var installed = await ReadInstalledAsync(conn, ct);
        if (installed.Count == 0) return;
        if (installed.Count > 1)
            throw new InvalidOperationException(
                $"{Name} exists {installed.Count} times. A migration changed its arguments without dropping the old " +
                "function; drop it in a new migration. Nothing was changed.");

        var function = installed[0];
        if (!Fits(function))
            throw new InvalidOperationException(
                $"The gates need {Name} to take ({string.Join(", ", ArgumentsFor(Gates))}), and it takes " +
                $"({string.Join(", ", function.Arguments)}). A gate or one of its parameters was added, removed or " +
                "renamed: add a migration that drops the function and creates it with the new arguments. Nothing was changed.");
        if (function.Source == Body) return;

        await using var cmd = new NpgsqlCommand(
            $"CREATE OR REPLACE FUNCTION {Name}({function.Signature}) RETURNS {function.Result} " +
            $"LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $body${Body}$body$",
            conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Whether the installed function is the one this build generates, readable
    /// by any role. True when there is none, as in a schema made of a test's
    /// own migrations.
    /// </summary>
    internal static async Task<bool> IsCurrentAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var installed = await ReadInstalledAsync(conn, ct);
        return installed.Count == 0 || (installed.Count == 1 && Fits(installed[0]) && installed[0].Source == Body);
    }

    private static bool Fits(Installed function) =>
        function.Arguments.Order(StringComparer.Ordinal).SequenceEqual(ArgumentsFor(Gates).Order(StringComparer.Ordinal));

    private static async Task<List<Installed>> ReadInstalledAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT p.prosrc, pg_get_function_arguments(p.oid), pg_get_function_result(p.oid),
                   coalesce(p.proargnames[1:p.pronargs], '{}')
            FROM pg_proc p
            WHERE p.pronamespace = to_regnamespace('prem_index') AND p.proname = 'text_matches'
            """, conn);
        var installed = new List<Installed>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            installed.Add(new Installed(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3)));
        return installed;
    }

    private sealed record Installed(string Source, string Signature, string Result, string[] Arguments);

    [GeneratedRegex("@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Parameter();
}
