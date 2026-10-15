using Xunit;

public class WhatsAppOptionsTests
{
    private static IReadOnlyList<string> Recipients(string to, string? country = null)
    {
        using var env = new EnvScope(("CCTV_WA_TO", to), ("CCTV_WA_COUNTRY", country));
        return new WhatsAppOptions().Recipients;
    }

    [Theory]
    [InlineData("081234567890", "6281234567890")] 
    [InlineData("+62 812-3456-7890", "6281234567890")]       
    [InlineData("0062 812 3456 7890", "6281234567890")]  
    [InlineData("6281234567890", "6281234567890")]        
    public void Numbers_are_normalised_to_digits_with_the_country_code(string input, string expected) =>
        Assert.Equal(new[] { expected }, Recipients(input));

    [Fact]
    public void A_group_id_is_passed_through_untouched() =>
        Assert.Equal(new[] { "120363025246125@g.us" }, Recipients("120363025246125@g.us"));

    [Fact]
    public void Numbers_that_are_too_short_are_dropped()
    {
        Assert.Empty(Recipients("123"));
        Assert.Equal(new[] { "6281234567890" }, Recipients("123, 081234567890"));
    }

    [Fact]
    public void The_same_number_written_two_ways_is_listed_once() =>
        Assert.Equal(new[] { "62812345678" }, Recipients("0812345678, +62812345678"));

    [Fact]
    public void The_country_code_can_be_changed()
    {
        Assert.Equal(new[] { "1555123456" }, Recipients("0555123456", country: "+1"));
        Assert.Equal(new[] { "6281234567890" }, Recipients("081234567890", country: "abc"));   // unusable value falls back to 62
    }

    [Theory]
    [InlineData("fonnte", "token", null, "081234567890", true)]
    [InlineData("cloud", "token", "1234", "081234567890", true)]
    [InlineData("cloud", "token", null, "081234567890", false)]    // Cloud API also needs the phone id
    [InlineData("fonnte", null, null, "081234567890", false)]      // no token
    [InlineData("fonnte", "token", null, "", false)]               // no recipients
    [InlineData(null, "token", null, "081234567890", false)]       // no provider
    public void Configured_needs_a_provider_a_token_and_a_recipient(string? provider, string? token, string? phoneId, string to, bool expected)
    {
        using var env = new EnvScope(("CCTV_WA_PROVIDER", provider), ("CCTV_WA_TOKEN", token), ("CCTV_WA_PHONE_ID", phoneId), ("CCTV_WA_TO", to), ("CCTV_WA_COUNTRY", null));
        Assert.Equal(expected, new WhatsAppOptions().Configured);
    }
}