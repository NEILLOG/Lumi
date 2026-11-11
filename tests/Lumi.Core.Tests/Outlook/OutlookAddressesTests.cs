using Lumi.Core.Outlook;

namespace Lumi.Core.Tests.Outlook;

public class OutlookAddressesTests
{
    [Theory]
    [InlineData("ming@example.com", "example.com")]
    [InlineData("Ming.Wang+tag@Mail.Example.COM", "mail.example.com")]
    public void Addresses_have_a_lowercase_domain(string value, string domain)
    {
        Assert.True(OutlookAddresses.IsEmailAddress(value));
        Assert.Equal(domain, OutlookAddresses.DomainOf(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("王小明")]
    [InlineData("ming@")]
    [InlineData("@example.com")]
    [InlineData("ming@example")]
    [InlineData("ming@.com")]
    [InlineData("ming@example.")]
    [InlineData("ming@a..com")]
    [InlineData("a@b@example.com")]
    [InlineData("ming @example.com")]
    [InlineData("王小明 <ming@example.com>")]
    [InlineData("a@example.com;b@example.com")]
    public void Names_and_malformed_values_are_not_addresses(string? value)
    {
        Assert.False(OutlookAddresses.IsEmailAddress(value));
        Assert.Null(OutlookAddresses.DomainOf(value));
    }
}
