using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Premagentic.Core.Storage;

/// <summary>
/// One numbered schema change, read from <c>Storage/Migrations/NNNN_name.sql</c>.
/// <para>
/// A file is split into sections by lines of the form <c>-- schema: prem_config</c>
/// and <c>-- schema: prem_index</c>. Each section is applied, checksummed and
/// recorded in its own schema's <c>schema_migration</c> table. That is what makes
/// the index disposable: dropping <c>prem_index</c> drops the record of its
/// sections with it, and the next run applies them again, while the config
/// sections, already recorded in <c>prem_config</c>, are left alone.
/// </para>
/// <para>
/// Rules a section must follow: it touches only its own schema's objects;
/// an index section may reference config (a tenant foreign key), never the
/// reverse, because a rebuild drops the index with CASCADE.
/// </para>
/// </summary>
public sealed record Migration(int Version, string Name, IReadOnlyList<MigrationSection> Sections)
{
    public const string ConfigSchema = "prem_config";
    public const string IndexSchema = "prem_index";

    /// <summary>Every schema a migration may target, in the order sections are applied.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [ConfigSchema, IndexSchema];

    private const string ResourcePrefix = "Premagentic.Migrations.";

    private static readonly Regex FileNamePattern = new(@"^(\d{4})_([a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant);
    private static readonly Regex SectionMarker = new(@"^--\s*schema:\s*(\S+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>The migrations compiled into this build, in version order.</summary>
    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(Migration).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(Parse(resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return Ordered(migrations);
    }

    /// <summary>Orders by version and refuses a duplicate number.</summary>
    public static IReadOnlyList<Migration> Ordered(IEnumerable<Migration> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Version == ordered[i - 1].Version)
                throw new InvalidOperationException(
                    $"Two migrations share version {ordered[i].Version:D4}: '{ordered[i - 1].Name}' and '{ordered[i].Name}'.");
        return ordered;
    }

    /// <summary>
    /// Parses one migration file. Line endings are normalized before anything
    /// else, so a checkout that converts them (a Windows clone, say) produces the
    /// same checksum as one that does not.
    /// </summary>
    public static Migration Parse(string fileName, string content)
    {
        var match = FileNamePattern.Match(fileName);
        if (!match.Success)
            throw new InvalidOperationException(
                $"Migration file '{fileName}' is not named NNNN_lower_case_name.sql.");

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var sections = new List<MigrationSection>();
        string? schema = null;
        var body = new StringBuilder();

        void Close()
        {
            if (schema is null) return;
            var sql = body.ToString().Trim('\n');
            if (sql.Trim().Length == 0)
                throw new InvalidOperationException($"Migration '{fileName}' has an empty '{schema}' section.");
            sections.Add(new MigrationSection(schema, sql, Checksum(sql)));
            body.Clear();
        }

        foreach (var line in lines)
        {
            var marker = SectionMarker.Match(line);
            if (marker.Success)
            {
                Close();
                schema = marker.Groups[1].Value;
                if (!Schemas.Contains(schema))
                    throw new InvalidOperationException(
                        $"Migration '{fileName}' names unknown schema '{schema}'. Known: {string.Join(", ", Schemas)}.");
                if (sections.Any(s => s.Schema == schema))
                    throw new InvalidOperationException($"Migration '{fileName}' has two '{schema}' sections.");
                continue;
            }

            if (schema is null)
            {
                // Before the first marker only comments and blank lines are
                // allowed, so no statement can run without a schema to record it.
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Migration '{fileName}' has SQL before its first '-- schema:' line.");
                continue;
            }

            body.Append(line).Append('\n');
        }
        Close();

        if (sections.Count == 0)
            throw new InvalidOperationException($"Migration '{fileName}' has no '-- schema:' section.");

        // Config first, whatever order the file wrote them in: an index section
        // may depend on config, never the reverse.
        var ordered = sections.OrderBy(s => s.Schema == ConfigSchema ? 0 : 1).ToArray();
        return new Migration(int.Parse(match.Groups[1].Value), match.Groups[2].Value, ordered);
    }

    private static string Checksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
}

/// <param name="Checksum">SHA-256 of the section text after line endings are normalized to LF.</param>
public sealed record MigrationSection(string Schema, string Sql, string Checksum);

/// <summary>What a pending or applied entry stands for.</summary>
public enum MigrationWork
{
    /// <summary>One section of a numbered migration.</summary>
    Section,

    /// <summary>
    /// The generated body of <c>prem_index.text_matches</c>, which migrate installs
    /// when the one in the database differs from this build's. It has no version.
    /// </summary>
    TextMatchBody,
}

/// <summary>One section the runner applied on this call, or would apply, or the text match body it would install.</summary>
public sealed record AppliedMigration(int Version, string Name, string Schema, MigrationWork Kind = MigrationWork.Section)
{
    /// <summary>The pending entry for a text match body that is not this build's.</summary>
    public static AppliedMigration TextMatchBody { get; } = new(0, "text_match_body", Migration.IndexSchema, MigrationWork.TextMatchBody);

    /// <summary>How the entry reads in a plan: <c>0007_name (prem_config)</c>, or what the body is.</summary>
    public string Label => Kind == MigrationWork.TextMatchBody
        ? $"the generated body of {Retrieval.TextMatchFunction.Name} ({Schema})"
        : $"{Version:D4}_{Name} ({Schema})";
}
