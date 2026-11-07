using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class MailServerFinderTests
{
    private sealed class FakeProbe : IMailServerProbe
    {
        public HashSet<MailServerEndpoint> Reachable { get; } = [];
        public List<string> LocalDomains { get; } = [];

        public Task<bool> CanConnectImapAsync(MailServerEndpoint server, CancellationToken cancellationToken) =>
            Task.FromResult(Reachable.Contains(server));

        public Task<bool> CanConnectSmtpAsync(MailServerEndpoint server, string localDomain, CancellationToken cancellationToken)
        {
            lock (LocalDomains) LocalDomains.Add(localDomain);
            return Task.FromResult(Reachable.Contains(server));
        }
    }

    private sealed class ThrowingProbe : IMailServerProbe
    {
        public Task<bool> CanConnectImapAsync(MailServerEndpoint server, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<bool> CanConnectSmtpAsync(MailServerEndpoint server, string localDomain, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private readonly FakeProbe _probe = new();
    private MailServerFinder Finder => new(_probe);

    // ---- Known providers (on the device only) ----

    [Theory]
    [InlineData("joe@gmail.com", MailProviderKind.Gmail)]
    [InlineData("  Joe@GMAIL.com ", MailProviderKind.Gmail)]
    [InlineData("joe@googlemail.com", MailProviderKind.Gmail)]
    [InlineData("joe@hotmail.com", MailProviderKind.MicrosoftPersonal)]
    [InlineData("joe@outlook.com", MailProviderKind.MicrosoftPersonal)]
    [InlineData("joe@live.ca", MailProviderKind.MicrosoftPersonal)]
    [InlineData("joe@icloud.com", MailProviderKind.ICloud)]
    [InlineData("joe@me.com", MailProviderKind.ICloud)]
    [InlineData("joe@yahoo.fr", MailProviderKind.Yahoo)]
    [InlineData("joe@company.com", MailProviderKind.Unknown)]
    [InlineData("joe@gmail.com.evil.net", MailProviderKind.Unknown)]
    [InlineData("joe@gmail", MailProviderKind.Unknown)]
    [InlineData("joe@", MailProviderKind.Unknown)]
    [InlineData("", MailProviderKind.Unknown)]
    [InlineData(null, MailProviderKind.Unknown)]
    public void FromAddress_RecognizesTheBigProviders(string? email, MailProviderKind expected) =>
        Assert.Equal(expected, MailProviders.FromAddress(email));

    [Theory]
    [InlineData("joe")]
    [InlineData("@company.com")]
    [InlineData("joe@localhost")]
    [InlineData("jo e@company.com")]
    public void DomainOf_NotAnAddress_IsNull(string email) =>
        Assert.Null(MailProviders.DomainOf(email));

    [Fact]
    public void DomainOf_ConvertsInternationalNames() =>
        Assert.Equal("xn--bcher-kva.example", MailProviders.DomainOf("joe@Bücher.example"));

    [Theory]
    [InlineData(" Mail.Example.com. ", "mail.example.com")]
    [InlineData("imap.example.co.uk", "imap.example.co.uk")]
    [InlineData("localhost", null)]
    [InlineData("mail.example.com:993", null)]
    [InlineData("https://mail.example.com", null)]
    [InlineData("", null)]
    public void NormalizeHost_Works(string host, string? expected) =>
        Assert.Equal(expected, MailProviders.NormalizeHost(host));

    // ---- IMAP server: ports and security ----

    [Fact]
    public async Task Imap_Prefers993Ssl()
    {
        _probe.Reachable.Add(new("mail.example.com", 993, MailSecurity.Ssl));
        _probe.Reachable.Add(new("mail.example.com", 143, MailSecurity.Tls));

        var found = await Finder.FindImapAsync("mail.example.com", CancellationToken.None);

        Assert.Equal(new MailServerEndpoint("mail.example.com", 993, MailSecurity.Ssl), found);
    }

    [Fact]
    public async Task Imap_FallsBackTo143StartTls()
    {
        _probe.Reachable.Add(new("mail.example.com", 143, MailSecurity.Tls));

        var found = await Finder.FindImapAsync("mail.example.com", CancellationToken.None);

        Assert.Equal(new MailServerEndpoint("mail.example.com", 143, MailSecurity.Tls), found);
    }

    [Fact]
    public async Task Imap_NothingAnswers_IsNull() =>
        Assert.Null(await Finder.FindImapAsync("mail.example.com", CancellationToken.None));

    [Fact]
    public async Task Smtp_Prefers465Ssl_ThenUses587_WithTheSenderDomain()
    {
        _probe.Reachable.Add(new("mail.example.com", 587, MailSecurity.Tls));

        var found = await Finder.FindSmtpAsync("mail.example.com", "example.com", CancellationToken.None);

        Assert.Equal(new MailServerEndpoint("mail.example.com", 587, MailSecurity.Tls), found);
        Assert.All(_probe.LocalDomains, d => Assert.Equal("example.com", d));

        _probe.Reachable.Add(new("mail.example.com", 465, MailSecurity.Ssl));
        Assert.Equal(new MailServerEndpoint("mail.example.com", 465, MailSecurity.Ssl),
            await Finder.FindSmtpAsync("mail.example.com", "example.com", CancellationToken.None));
    }

    [Fact]
    public async Task ProbeErrors_CountAsNotReachable()
    {
        var finder = new MailServerFinder(new ThrowingProbe());

        Assert.Null(await finder.FindImapAsync("mail.example.com", CancellationToken.None));
        Assert.Null(await finder.FindSmtpAsync("mail.example.com", "example.com", CancellationToken.None));
    }
}
