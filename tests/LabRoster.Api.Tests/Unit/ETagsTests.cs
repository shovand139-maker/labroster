using LabRoster.Api.Infrastructure;

namespace LabRoster.Api.Tests.Unit;

public sealed class ETagsTests
{
    private static readonly byte[] RowVersion = [0, 0, 0, 0, 0, 0, 0x1F, 0xA3];

    [Fact]
    public void A_formatted_etag_parses_back_to_the_same_row_version()
    {
        var etag = ETags.Format(RowVersion);

        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.True(ETags.TryParse(etag, out var parsed));
        Assert.Equal(RowVersion, parsed);
    }

    [Fact]
    public void A_missing_header_is_rejected()
    {
        Assert.False(ETags.TryParse(null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("AAAAAAAAH6M=")]
    [InlineData("W/\"AAAAAAAAH6M=\"")]
    [InlineData("\"not base64!\"")]
    [InlineData("\"AAAAAAAAAAAAAAAA\"")]
    [InlineData("\"\"")]
    public void Anything_but_a_strong_eight_byte_etag_is_rejected(string header)
    {
        Assert.False(ETags.TryParse(header, out _));
    }
}
