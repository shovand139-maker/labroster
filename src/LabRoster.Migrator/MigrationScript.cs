using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LabRoster.Migrator;

public enum ScriptKind
{
    /// <summary>V###__name.sql: applied once, in order, and never edited afterwards.</summary>
    Versioned,

    /// <summary>R__name.sql: CREATE OR ALTER objects, re-applied whenever the file changes.</summary>
    Repeatable,

    /// <summary>S###__name.sql: demo data, applied once and only when seeding is requested.</summary>
    Seed,
}

public sealed partial record MigrationScript(string Name, ScriptKind Kind, string Sql)
{
    public string Checksum { get; } = ComputeChecksum(Sql);

    public static string ComputeChecksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static bool TryGetKind(string fileName, out ScriptKind kind)
    {
        if (VersionedName().IsMatch(fileName))
        {
            kind = ScriptKind.Versioned;
            return true;
        }

        if (RepeatableName().IsMatch(fileName))
        {
            kind = ScriptKind.Repeatable;
            return true;
        }

        if (SeedName().IsMatch(fileName))
        {
            kind = ScriptKind.Seed;
            return true;
        }

        kind = default;
        return false;
    }

    [GeneratedRegex(@"^V\d{3}__[a-z0-9_]+\.sql$")]
    private static partial Regex VersionedName();

    [GeneratedRegex(@"^R__[a-z0-9_]+\.sql$")]
    private static partial Regex RepeatableName();

    [GeneratedRegex(@"^S\d{3}__[a-z0-9_]+\.sql$")]
    private static partial Regex SeedName();
}

public static class ScriptCatalog
{
    /// <summary>Scripts embedded in this assembly, in the order they must run.</summary>
    public static IReadOnlyList<MigrationScript> LoadEmbedded() => Load(typeof(ScriptCatalog).Assembly);

    public static IReadOnlyList<MigrationScript> Load(Assembly assembly)
    {
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!MigrationScript.TryGetKind(resource, out var kind))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded script {resource} could not be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(new MigrationScript(resource, kind, reader.ReadToEnd()));
        }

        return Order(scripts);
    }

    public static IReadOnlyList<MigrationScript> Order(IEnumerable<MigrationScript> scripts) =>
        scripts
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
}

public static partial class SqlBatchSplitter
{
    /// <summary>
    /// Splits a script on lines that contain only GO, the way sqlcmd and SSMS do.
    /// GO inside a block comment is ignored.
    /// </summary>
    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var commentDepth = 0;

        foreach (var line in script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (commentDepth == 0 && GoLine().IsMatch(line))
            {
                Flush();
                continue;
            }

            commentDepth = Math.Max(0, commentDepth + CountOf(line, "/*") - CountOf(line, "*/"));
            current.Append(line).Append('\n');
        }

        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            if (batch.Length > 0)
            {
                batches.Add(batch);
            }

            current.Clear();
        }
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
