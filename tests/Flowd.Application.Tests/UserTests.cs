using Flowd.Application.Security;
using Microsoft.Extensions.Time.Testing;

namespace Flowd.Application.Tests;

/// <summary>FR-SEC-011: password hashing and the login lockout.</summary>
public sealed class UserTests
{
    private sealed class MemoryUsers : IUserStore
    {
        public UserCredential? Credential { get; set; }

        public Task<UserCredential?> FindAsync(string username, CancellationToken cancellationToken) =>
            Task.FromResult(Credential is { } c && c.User.Username == username ? Credential : null);

        public Task RecordLoginAsync(string username, bool success, DateTimeOffset? lockUntil, CancellationToken cancellationToken)
        {
            var failures = Credential!.FailedAttempts + 1;
            if (success || lockUntil is not null)
            {
                failures = 0;
            }

            Credential = Credential with { FailedAttempts = failures, LockedUntil = success ? null : lockUntil ?? Credential.LockedUntil };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UserInfo>> ListAsync(string? tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CreateAsync(string username, string passwordHash, UserInfo info, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(string username, string? tenantId, IReadOnlyList<string> roles, IReadOnlyList<string> groups, bool disabled, string? passwordHash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string username, string? tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static MemoryUsers Store(bool disabled = false) => new()
    {
        Credential = new UserCredential(new UserInfo("amy", ["reader"], string.Empty, [], DateTimeOffset.UnixEpoch, disabled), PasswordHasher.Hash("a long enough password"), 0, null),
    };

    [Fact]
    public void FR_SEC_011_a_hash_verifies_only_the_right_password_and_is_salted()
    {
        var first = PasswordHasher.Hash("secret secret secret");
        var second = PasswordHasher.Hash("secret secret secret");

        PasswordHasher.Verify("secret secret secret", first).ShouldBeTrue();
        PasswordHasher.Verify("secret secret secreT", first).ShouldBeFalse();
        first.ShouldNotBe(second);
        first.ShouldStartWith("pbkdf2-sha256$600000$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("pbkdf2-sha256$abc$AAAA$BBBB")]
    [InlineData("pbkdf2-sha256$600000$not base64!$BBBB")]
    [InlineData("pbkdf2-sha256$99999999999$AAAA$BBBB")]
    public void FR_SEC_011_malformed_stored_hashes_never_verify_and_never_throw(string stored) =>
        PasswordHasher.Verify("anything", stored).ShouldBeFalse();

    [Fact]
    public async Task FR_SEC_011_five_failures_lock_the_user_for_fifteen_minutes()
    {
        var clock = new FakeTimeProvider();
        var store = Store();
        var login = new LoginService(store, clock);
        for (var i = 0; i < LoginService.MaxFailures; i++)
        {
            (await login.LoginAsync("amy", "wrong", CancellationToken.None)).ShouldBeNull();
        }

        (await login.LoginAsync("amy", "a long enough password", CancellationToken.None)).ShouldBeNull(); // locked
        clock.Advance(LoginService.LockDuration + TimeSpan.FromSeconds(1));

        (await login.LoginAsync("amy", "a long enough password", CancellationToken.None))!.Username.ShouldBe("amy");
    }

    [Fact]
    public async Task FR_SEC_011_a_success_resets_the_failure_count_and_disabled_users_cannot_log_in()
    {
        var store = Store();
        var login = new LoginService(store, new FakeTimeProvider());
        await login.LoginAsync("amy", "wrong", CancellationToken.None);
        await login.LoginAsync("amy", "wrong", CancellationToken.None);

        (await login.LoginAsync("amy", "a long enough password", CancellationToken.None)).ShouldNotBeNull();

        store.Credential!.FailedAttempts.ShouldBe(0);
        (await new LoginService(Store(disabled: true), new FakeTimeProvider()).LoginAsync("amy", "a long enough password", CancellationToken.None)).ShouldBeNull();
        (await login.LoginAsync("  ", "x", CancellationToken.None)).ShouldBeNull();
    }
}
