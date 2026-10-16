using LabRoster.Migrator;

namespace LabRoster.Api.Tests.Unit;

public sealed class MigrationScriptTests
{
    [Fact]
    public void Splits_batches_on_go_lines_only()
    {
        var batches = SqlBatchSplitter.Split("CREATE TABLE a (id int);\nGO\nSELECT 'GO' AS word;\n  go  \nSELECT 2;\nGO -- trailing comment\n");

        Assert.Equal(new[] { "CREATE TABLE a (id int);", "SELECT 'GO' AS word;", "SELECT 2;" }, batches);
    }

    [Fact]
    public void Ignores_go_inside_a_block_comment()
    {
        var batches = SqlBatchSplitter.Split("/* setup\nGO\n*/\nSELECT 1;\nGO\nSELECT 2;");

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("/* setup", batches[0]);
    }

    [Fact]
    public void Embedded_scripts_load_in_execution_order()
    {
        var scripts = ScriptCatalog.LoadEmbedded();

        Assert.NotEmpty(scripts);
        Assert.Equal(scripts.OrderBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.Ordinal).Select(s => s.Name), scripts.Select(s => s.Name));
        Assert.Equal("V001__core_schema.sql", scripts[0].Name);
        Assert.Contains(scripts, s => s.Kind == ScriptKind.Repeatable);
        Assert.Contains(scripts, s => s.Kind == ScriptKind.Seed);
    }

    [Fact]
    public void Every_procedure_and_view_script_is_rerunnable()
    {
        foreach (var script in ScriptCatalog.LoadEmbedded().Where(s => s.Kind == ScriptKind.Repeatable))
        {
            foreach (var batch in SqlBatchSplitter.Split(script.Sql))
            {
                var firstStatement = string.Join('\n', batch.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))).TrimStart();
                Assert.StartsWith("CREATE OR ALTER", firstStatement, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Checksums_ignore_line_endings()
    {
        Assert.Equal(MigrationScript.ComputeChecksum("SELECT 1;\nGO\n"), MigrationScript.ComputeChecksum("SELECT 1;\r\nGO\r\n"));
        Assert.NotEqual(MigrationScript.ComputeChecksum("SELECT 1;"), MigrationScript.ComputeChecksum("SELECT 2;"));
    }

    [Theory]
    [InlineData("V003__add_waitlist_index.sql", true)]
    [InlineData("R__050_reports.sql", true)]
    [InlineData("S002__more_demo_data.sql", true)]
    [InlineData("V3__missing_padding.sql", false)]
    [InlineData("v003__lowercase_prefix.sql", false)]
    [InlineData("V003__Has-Dashes.sql", false)]
    public void Only_well_named_scripts_are_picked_up(string name, bool recognised)
    {
        Assert.Equal(recognised, MigrationScript.TryGetKind(name, out _));
    }
}
