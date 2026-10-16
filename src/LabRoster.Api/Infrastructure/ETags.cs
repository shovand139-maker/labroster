namespace LabRoster.Api.Infrastructure;

/// <summary>
/// Strong ETags built from SQL Server rowversion values. Writes must send the ETag
/// back in If-Match; the procedure rejects the write if the row changed in between.
/// </summary>
public static class ETags
{
    public static string Format(byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        return $"\"{Convert.ToBase64String(rowVersion)}\"";
    }

    public static bool TryParse(string? header, out byte[] rowVersion)
    {
        rowVersion = [];
        var value = header?.Trim();
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return false;
        }

        // A weak validator cannot guarantee byte-for-byte equality, so it is not accepted.
        if (value.StartsWith("W/", StringComparison.Ordinal) || value.Length < 3 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var buffer = new byte[16];
        if (!Convert.TryFromBase64String(value[1..^1], buffer, out var written) || written != 8)
        {
            return false;
        }

        rowVersion = buffer[..8];
        return true;
    }
}
