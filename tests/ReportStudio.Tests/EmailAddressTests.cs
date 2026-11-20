using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The recipient checks of requirement 50.</summary>
public class EmailAddressTests
{
    [Theory]
    [InlineData("a@example.com")]
    [InlineData("first.last+tag@mail.example.co.in")]
    [InlineData("A_B-C%D@Example.COM")]
    [InlineData("x@a-b.example.com")]
    public void IsValid_AcceptsPlainAddresses(string address) => Assert.True(EmailAddress.IsValid(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("@example.com")]
    [InlineData("a@")]
    [InlineData("a@b")]
    [InlineData("a@@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData(" a@example.com")]
    [InlineData("a@example.com ")]
    [InlineData("a b@example.com")]
    [InlineData("a,b@example.com")]
    [InlineData("a@example.com,b@example.com")]
    [InlineData("a;b@example.com")]
    [InlineData("<a@example.com>")]
    [InlineData("\"a\"@example.com")]
    [InlineData("a\r\nBcc: x@example.com")]
    [InlineData(".a@example.com")]
    [InlineData("a.@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a@-example.com")]
    [InlineData("a@example-.com")]
    [InlineData("a@example..com")]
    [InlineData("a@exa_mple.com")]
    [InlineData("é@example.com")]
    public void IsValid_RefusesAnythingThatCouldCarryMore(string? address) => Assert.False(EmailAddress.IsValid(address));

    [Fact]
    public void IsValid_RefusesTooLong()
    {
        Assert.False(EmailAddress.IsValid(new string('a', 65) + "@example.com"));
        Assert.False(EmailAddress.IsValid("a@" + new string('b', 64) + ".com"));
        Assert.False(EmailAddress.IsValid("a@" + string.Join(".", Enumerable.Repeat(new string('b', 60), 5)) + ".com"));
    }

    [Fact]
    public void Domain_IsLowerCase_OrNull()
    {
        Assert.Equal("example.com", EmailAddress.Domain("a@Example.COM"));
        Assert.Null(EmailAddress.Domain("nonsense"));
    }

    [Fact]
    public void IsAllowed_MatchesDomainOrSubDomain_NotLookAlikes()
    {
        var allowed = new[] { "example.com", " @Corp.in " };
        Assert.True(EmailAddress.IsAllowed("a@example.com", allowed));
        Assert.True(EmailAddress.IsAllowed("a@mail.example.com", allowed));
        Assert.True(EmailAddress.IsAllowed("a@CORP.IN", allowed));
        Assert.False(EmailAddress.IsAllowed("a@badexample.com", allowed));
        Assert.False(EmailAddress.IsAllowed("a@example.com.evil.net", allowed));
        Assert.False(EmailAddress.IsAllowed("a@other.org", allowed));
    }

    [Fact]
    public void IsAllowed_EmptyOrMissingList_AllowsNothing()
    {
        Assert.False(EmailAddress.IsAllowed("a@example.com", Array.Empty<string>()));
        Assert.False(EmailAddress.IsAllowed("a@example.com", null));
        Assert.False(EmailAddress.IsAllowed("a@example.com", new[] { "", "  " }));
        Assert.False(EmailAddress.IsAllowed("not an address", new[] { "example.com" }));
    }
}
