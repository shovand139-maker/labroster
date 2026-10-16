using System.Data;
using System.Data.Common;
using LabRoster.Api.Auth;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Data;

public interface ITenantConnectionFactory
{
    /// <summary>Opens a connection scoped to the caller's tenant.</summary>
    Task<SqlConnection> OpenAsync(CurrentUser user, CancellationToken cancellationToken);
}

/// <summary>
/// Every connection the API uses is bound to one tenant before any procedure runs.
/// SESSION_CONTEXT is set read-only, so nothing later on the connection can switch
/// tenant, and row-level security filters every table by it. Pooled connections are
/// reset by SQL Server before reuse, which clears the context.
/// </summary>
public sealed class TenantConnectionFactory(IConfiguration configuration) : ITenantConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("LabRoster");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:LabRoster is not configured.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 1;
                EXEC sys.sp_set_session_context @key = N'UserId', @value = @userId, @read_only = 1;
                """;
            command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier) { Value = user.TenantId });
            command.Parameters.Add(new SqlParameter("@userId", SqlDbType.NVarChar, 64) { Value = user.UserId });
            await command.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal static class Sql
{
    public static SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };

    public static SqlCommand With(this SqlCommand command, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size > 0)
        {
            parameter.Size = size;
        }

        parameter.Value = value ?? DBNull.Value;
        return command;
    }

    public static Guid GetGuid(this DbDataReader reader, string column) => reader.GetGuid(reader.GetOrdinal(column));

    public static Guid? GetNullableGuid(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    public static string GetString(this DbDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    public static string? GetNullableString(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static int GetInt32(this DbDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    public static long GetInt64(this DbDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    public static int? GetNullableInt32(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool GetBoolean(this DbDataReader reader, string column) => reader.GetBoolean(reader.GetOrdinal(column));

    public static byte[] GetRowVersion(this DbDataReader reader, string column) => reader.GetFieldValue<byte[]>(reader.GetOrdinal(column));

    /// <summary>datetime2 columns hold UTC; give them an explicit offset so JSON carries a Z.</summary>
    public static DateTimeOffset GetUtc(this DbDataReader reader, string column) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal(column)), DateTimeKind.Utc));

    /// <summary>UTC, truncated to whole seconds to match datetime2(0) columns.</summary>
    public static DateTime ToSqlUtc(this DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
