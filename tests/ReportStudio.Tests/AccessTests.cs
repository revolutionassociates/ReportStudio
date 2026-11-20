using System.Text;
using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Passwords, users, report access and login throttling (requirement 51c).</summary>
public class AccessTests
{
    private const int Fast = PasswordHash.MinIterations;   // keeps the tests quick; real hashes use the default

    private static AccessUser User(string name, string password, AccessRole role = AccessRole.Viewer, params string[] reports) => new()
    {
        Name = name,
        PasswordHash = PasswordHash.Create(password, Fast),
        Role = role,
        Reports = reports.ToList()
    };

    private sealed class ClockForTests : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- password hashes ----

    [Fact]
    public void PasswordHash_VerifiesTheRightPassword_AndNoOther()
    {
        var stored = PasswordHash.Create("correct horse", Fast);
        Assert.StartsWith("pbkdf2-sha256$100000$", stored);
        Assert.True(PasswordHash.Verify("correct horse", stored));
        Assert.False(PasswordHash.Verify("correct horsf", stored));
        Assert.False(PasswordHash.Verify("", stored));
        Assert.False(PasswordHash.Verify(null, stored));
    }

    [Fact]
    public void PasswordHash_SaltsEveryHash()
    {
        Assert.NotEqual(PasswordHash.Create("same", Fast), PasswordHash.Create("same", Fast));
    }

    [Fact]
    public void PasswordHash_KeepsNonAsciiPasswords()
    {
        var stored = PasswordHash.Create("पासवर्ड-é", Fast);
        Assert.True(PasswordHash.Verify("पासवर्ड-é", stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("pbkdf2-sha256$100000$AAAA")]
    [InlineData("md5$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("pbkdf2-sha256$99$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("pbkdf2-sha256$999999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("pbkdf2-sha256$100000$not base64!$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("pbkdf2-sha256$100000$AAAA$AAAAAAAAAAAAAAAAAAAAAA==")]
    public void PasswordHash_RefusesMalformedText(string? stored)
    {
        Assert.False(PasswordHash.IsWellFormed(stored));
        Assert.False(PasswordHash.Verify("x", stored));
    }

    [Fact]
    public void PasswordHash_Create_RefusesBadInput()
    {
        Assert.Throws<ArgumentException>(() => PasswordHash.Create("", Fast));
        Assert.Throws<ArgumentException>(() => PasswordHash.Create(new string('x', PasswordHash.MaxPasswordChars + 1), Fast));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordHash.Create("x", 10));
    }

    // ---- policy ----

    [Fact]
    public void Authenticate_ChecksNameAndPassword_IgnoringTheCaseOfTheName()
    {
        var policy = new AccessPolicy(new[] { User("Asha", "pw-1"), User("Ravi", "pw-2", AccessRole.Admin) });
        Assert.Equal("Asha", policy.Authenticate("asha", "pw-1")!.Name);
        Assert.Equal(AccessRole.Admin, policy.Authenticate(" Ravi ", "pw-2")!.Role);
        Assert.Null(policy.Authenticate("Asha", "pw-2"));
        Assert.Null(policy.Authenticate("Asha", "PW-1"));
        Assert.Null(policy.Authenticate("Nobody", "pw-1"));
        Assert.Null(policy.Authenticate(null, null));
        Assert.Equal(2, policy.Count);
    }

    [Fact]
    public void Policy_RefusesDuplicateAndBrokenUsers()
    {
        Assert.Throws<ArgumentException>(() => new AccessPolicy(new[] { User("Asha", "a"), User("ASHA", "b") }));
        Assert.Throws<ArgumentException>(() => new AccessPolicy(new[] { new AccessUser { Name = "x", PasswordHash = "plain" } }));
        Assert.Throws<ArgumentException>(() => new AccessPolicy(new[] { User("a:b", "pw") }));
        Assert.Throws<ArgumentException>(() => new AccessPolicy(new[] { User("", "pw") }));
        Assert.Equal(0, new AccessPolicy(null).Count);
    }

    [Fact]
    public void Problems_NameTheMistake()
    {
        var bad = User("Asha", "pw");
        bad.Role = (AccessRole)9;
        Assert.Contains("role", Assert.Single(AccessPolicy.Problems(bad)));

        var pattern = User("Asha", "pw", AccessRole.Viewer, "sa*les");
        Assert.Contains("not a report name", Assert.Single(AccessPolicy.Problems(pattern)));
        Assert.Contains("not a report name", Assert.Single(AccessPolicy.Problems(User("Asha", "pw", AccessRole.Viewer, "../x"))));
        Assert.Empty(AccessPolicy.Problems(User("Asha", "pw", AccessRole.Viewer, "sales", "hr-*", "*")));
        Assert.Single(AccessPolicy.Problems(null));
    }

    [Fact]
    public void CanView_FollowsTheReportList()
    {
        var some = User("Asha", "pw", AccessRole.Viewer, "payslip", "sales-*");
        Assert.True(AccessPolicy.CanView(some, "payslip"));
        Assert.True(AccessPolicy.CanView(some, "PaySlip"));
        Assert.True(AccessPolicy.CanView(some, "sales-north"));
        Assert.True(AccessPolicy.CanView(some, "Sales-"));
        Assert.False(AccessPolicy.CanView(some, "payslip2"));
        Assert.False(AccessPolicy.CanView(some, "hr"));
        Assert.False(AccessPolicy.CanView(some, ""));
        Assert.False(AccessPolicy.CanView(some, null));
    }

    [Fact]
    public void CanView_EmptyListAndAdmin_SeeEverything()
    {
        Assert.True(AccessPolicy.CanView(User("a", "pw"), "anything"));
        Assert.True(AccessPolicy.CanView(User("a", "pw", AccessRole.Admin, "only-this"), "anything"));
        Assert.True(AccessPolicy.CanView(User("a", "pw", AccessRole.Viewer, "*"), "anything"));
        Assert.True(AccessPolicy.IsAdmin(User("a", "pw", AccessRole.Admin)));
        Assert.False(AccessPolicy.IsAdmin(User("a", "pw")));
    }

    // ---- Basic header ----

    private static string Basic(string text) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void BasicCredentials_ReadsNameAndPassword_WithColonsInThePassword()
    {
        Assert.True(BasicCredentials.TryParse(Basic("asha:pa:ss"), out var name, out var password));
        Assert.Equal("asha", name);
        Assert.Equal("pa:ss", password);
        Assert.True(BasicCredentials.TryParse("basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("a:")), out name, out password));
        Assert.Equal("", password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("Basic")]
    [InlineData("Basic !!!not-base64")]
    public void BasicCredentials_RefusesOtherHeaders(string? header) => Assert.False(BasicCredentials.TryParse(header, out _, out _));

    [Fact]
    public void BasicCredentials_RefusesNoName_ControlCharacters_AndHugeHeaders()
    {
        Assert.False(BasicCredentials.TryParse(Basic(":pw"), out _, out _));
        Assert.False(BasicCredentials.TryParse(Basic("nocolon"), out _, out _));
        Assert.False(BasicCredentials.TryParse(Basic("a\nb:pw"), out _, out _));
        Assert.False(BasicCredentials.TryParse(Basic("a:" + new string('x', 2000)), out _, out _));
        Assert.False(BasicCredentials.TryParse("Basic " + Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x3A, 0x41 }), out _, out _));
    }

    // ---- throttle ----

    [Fact]
    public void Throttle_BlocksAfterTooManyFailures_UntilTheWindowPasses()
    {
        var clock = new ClockForTests();
        var throttle = new LoginThrottle(3, TimeSpan.FromMinutes(5), clock);

        for (int i = 0; i < 2; i++) throttle.Failure("asha@1.2.3.4");
        Assert.False(throttle.IsBlocked("asha@1.2.3.4"));
        throttle.Failure("asha@1.2.3.4");
        Assert.True(throttle.IsBlocked("asha@1.2.3.4"));
        Assert.True(throttle.IsBlocked("ASHA@1.2.3.4"));    // same key whatever the case
        Assert.False(throttle.IsBlocked("ravi@1.2.3.4"));   // other keys are not affected

        clock.Now = clock.Now.AddMinutes(5);
        Assert.False(throttle.IsBlocked("asha@1.2.3.4"));
        throttle.Failure("asha@1.2.3.4");                   // a new window starts at one failure
        Assert.False(throttle.IsBlocked("asha@1.2.3.4"));
    }

    [Fact]
    public void Throttle_ARightPasswordClearsTheCount()
    {
        var throttle = new LoginThrottle(2, null, new ClockForTests());
        throttle.Failure("k");
        throttle.Success("k");
        throttle.Failure("k");
        Assert.False(throttle.IsBlocked("k"));
    }

    [Fact]
    public void Throttle_TheTableDoesNotGrowWithoutLimit()
    {
        var clock = new ClockForTests();
        var throttle = new LoginThrottle(1, TimeSpan.FromMinutes(5), clock);
        for (int i = 0; i < LoginThrottle.MaxKeys + 50; i++) throttle.Failure("k" + i);
        Assert.True(throttle.IsBlocked("k0"));
        Assert.False(throttle.IsBlocked("k" + (LoginThrottle.MaxKeys + 10)));   // not counted: the table was full of live entries

        clock.Now = clock.Now.AddMinutes(6);
        throttle.Failure("fresh");   // old entries are purged to make room
        Assert.True(throttle.IsBlocked("fresh"));
    }
}
