namespace LabRoster.Migrator;

/// <summary>Command-line entry point. Kept out of top-level statements so the test
/// project can reference both this assembly and the API without two Program types.</summary>
internal static class MigratorCli
{
    private const string Usage = """
        Applies LabRoster database migrations.

        Usage:
          dotnet run --project src/LabRoster.Migrator -- [options]

        Options:
          --connection <string>          Admin connection string (or env LABROSTER_SQL_ADMIN)
          --create-database              Create the database if it does not exist
          --seed                         Apply demo data (local development and end-to-end tests only)
          --app-login <name>             Create or update a SQL login for the API and grant it the api schema
          --app-password <password>      Password for --app-login (or env LABROSTER_APP_PASSWORD)
          --app-entra-principal <name>   Grant an Entra managed identity instead (Azure SQL)
        """;

    public static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] valueOptions = ["--connection", "--app-login", "--app-password", "--app-entra-principal"];

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            if (valueOptions.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"{arg} needs a value.");
                    return 2;
                }

                options[arg] = args[++i];
            }
            else if (arg is "--create-database" or "--seed")
            {
                flags.Add(arg);
            }
            else
            {
                Console.Error.WriteLine($"Unknown option {arg}.{Environment.NewLine}{Usage}");
                return 2;
            }
        }

        var connection = options.GetValueOrDefault("--connection") ?? Environment.GetEnvironmentVariable("LABROSTER_SQL_ADMIN");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("No connection string. Pass --connection or set LABROSTER_SQL_ADMIN.");
            return 2;
        }

        AppPrincipal? app = null;
        if (options.GetValueOrDefault("--app-entra-principal") is { Length: > 0 } entraPrincipal)
        {
            app = new AppPrincipal(entraPrincipal, Password: null, External: true);
        }
        else if (options.GetValueOrDefault("--app-login") is { Length: > 0 } login)
        {
            var password = options.GetValueOrDefault("--app-password") ?? Environment.GetEnvironmentVariable("LABROSTER_APP_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("--app-login needs --app-password or LABROSTER_APP_PASSWORD.");
                return 2;
            }

            app = new AppPrincipal(login, password, External: false);
        }

        try
        {
            var migrator = new DatabaseMigrator(connection, Console.Out);
            await migrator.RunAsync(new MigrationOptions(
                CreateDatabase: flags.Contains("--create-database"),
                Seed: flags.Contains("--seed"),
                App: app));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            Console.Error.WriteLine($"Migration failed: {ex.Message}");
            return 1;
        }
    }
}
