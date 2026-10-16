using LabRoster.Api.Infrastructure;

namespace LabRoster.Api.Tests.Unit;

public sealed class CsvRosterTests
{
    [Fact]
    public void Reads_a_simple_export()
    {
        var result = CsvRoster.Parse("email,fullName\nasha@example.com,Asha Rao\nben@example.com,Ben Ortiz\n");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Learners.Count);
        Assert.Equal("asha@example.com", result.Learners[0].Email);
        Assert.Equal("Ben Ortiz", result.Learners[1].FullName);
    }

    [Theory]
    [InlineData("Email,Full Name")]
    [InlineData("name,email")]
    [InlineData(" EMAIL , NAME ")]
    public void Accepts_common_header_spellings_in_any_order(string header)
    {
        var row = header.Trim().StartsWith("name", StringComparison.OrdinalIgnoreCase)
            ? "Asha Rao,asha@example.com"
            : "asha@example.com,Asha Rao";

        var result = CsvRoster.Parse($"{header}\n{row}");

        Assert.Empty(result.Errors);
        var learner = Assert.Single(result.Learners);
        Assert.Equal("asha@example.com", learner.Email);
        Assert.Equal("Asha Rao", learner.FullName);
    }

    [Fact]
    public void Handles_quotes_commas_crlf_and_a_byte_order_mark()
    {
        var csv = "﻿email,fullName\r\n\"rao@example.com\",\"Rao, Asha \"\"AR\"\"\"\r\n\r\nben@example.com,Ben\r\n";

        var result = CsvRoster.Parse(csv);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Learners.Count);
        Assert.Equal("Rao, Asha \"AR\"", result.Learners[0].FullName);
    }

    [Fact]
    public void A_missing_header_is_an_error()
    {
        var result = CsvRoster.Parse("asha@example.com,Asha Rao");

        Assert.Empty(result.Learners);
        Assert.Contains(result.Errors, e => e.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unclosed_quote_is_reported_with_its_line()
    {
        var result = CsvRoster.Parse("email,name\nasha@example.com,\"Asha Rao\n");

        Assert.Contains(result.Errors, e => e.StartsWith("Line 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Stops_at_the_row_limit()
    {
        var rows = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"user{i}@example.com,User {i}"));

        var result = CsvRoster.Parse("email,name\n" + rows, maxRows: 5);

        Assert.Equal(5, result.Learners.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void An_empty_file_is_an_error()
    {
        Assert.Single(CsvRoster.Parse(string.Empty).Errors);
    }
}
