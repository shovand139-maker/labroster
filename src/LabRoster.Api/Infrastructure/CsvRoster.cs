using System.Text;
using LabRoster.Api.Contracts;

namespace LabRoster.Api.Infrastructure;

public sealed record CsvRosterResult(IReadOnlyList<EnrollmentRequest> Learners, IReadOnlyList<string> Errors);

/// <summary>
/// Parses a roster exported from a spreadsheet: a header row with an email column and
/// a name column ("fullName", "full name" or "name"), then one learner per line.
/// Handles quoted fields, embedded commas and quotes, CRLF and a UTF-8 BOM.
/// </summary>
public static class CsvRoster
{
    private static readonly string[] NameHeaders = ["fullname", "full name", "name"];

    public static CsvRosterResult Parse(string text, int maxRows = BulkEnrollmentRequest.MaxLearners)
    {
        var errors = new List<string>();
        var learners = new List<EnrollmentRequest>();
        var records = ReadRecords(text.TrimStart('﻿'), errors);

        if (errors.Count > 0)
        {
            return new CsvRosterResult(learners, errors);
        }

        if (records.Count == 0)
        {
            return new CsvRosterResult(learners, ["The file is empty."]);
        }

        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var emailColumn = header.IndexOf("email");
        var nameColumn = header.FindIndex(h => NameHeaders.Contains(h));

        if (emailColumn < 0 || nameColumn < 0)
        {
            return new CsvRosterResult(learners, ["The first row must be a header with an email column and a fullName (or name) column."]);
        }

        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (learners.Count == maxRows)
            {
                errors.Add($"A roster can have at most {maxRows} learners.");
                break;
            }

            var email = emailColumn < record.Count ? record[emailColumn] : string.Empty;
            var name = nameColumn < record.Count ? record[nameColumn] : string.Empty;
            learners.Add(new EnrollmentRequest(email.Trim(), name.Trim()));
        }

        return new CsvRosterResult(learners, errors);
    }

    private static List<List<string>> ReadRecords(string text, List<string> errors)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    line++;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
        {
            errors.Add($"Line {line}: a quoted field is never closed.");
            return records;
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }
}
