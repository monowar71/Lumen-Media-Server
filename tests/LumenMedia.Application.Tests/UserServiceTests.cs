using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Users;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Users;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public class UserServiceTests
{
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();

    public UserServiceTests()
    {
        _uow.Users.Returns(_users);
        _hasher.Hash(Arg.Any<string>()).Returns("hashed");
    }

    private UserService CreateSut() => new(_uow, _hasher, TimeProvider.System);

    private User SetupExistingUser(UserRole role = UserRole.User)
    {
        var user = new User("kate", "oldhash", role, DateTimeOffset.UtcNow);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    [Fact]
    public async Task Password_change_revokes_active_refresh_tokens()
    {
        var user = SetupExistingUser();
        var now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(user.Id, "hash", now.AddDays(30), now);
        _users.GetActiveRefreshTokensAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns([token]);

        await CreateSut().UpdateAsync(user.Id, new UpdateUserRequest { Password = "newpassword1" }, default);

        token.IsActive(now).Should().BeFalse("a stolen refresh token must not survive a password change");
    }

    [Fact]
    public async Task Role_change_revokes_active_refresh_tokens()
    {
        var user = SetupExistingUser(UserRole.Admin);
        var now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(user.Id, "hash", now.AddDays(30), now);
        _users.GetActiveRefreshTokensAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns([token]);

        await CreateSut().UpdateAsync(user.Id, new UpdateUserRequest { Role = UserRole.User }, default);

        token.IsActive(now).Should().BeFalse("old refresh tokens must not keep minting the old role");
    }

    [Fact]
    public async Task Update_without_credential_changes_does_not_touch_refresh_tokens()
    {
        var user = SetupExistingUser();

        await CreateSut().UpdateAsync(user.Id, new UpdateUserRequest { AllowTranscoding = false }, default);

        await _users.DidNotReceive().GetActiveRefreshTokensAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_rejects_password_shorter_than_eight_characters()
    {
        var act = () => CreateSut().CreateAsync(
            new CreateUserRequest { Username = "kate", Password = "short67" },
            default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Update_rejects_password_shorter_than_eight_characters()
    {
        var user = SetupExistingUser();

        var act = () => CreateSut().UpdateAsync(user.Id, new UpdateUserRequest { Password = "short67" }, default);

        await act.Should().ThrowAsync<ValidationException>();
    }
}
