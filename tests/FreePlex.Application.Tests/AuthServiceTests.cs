using FluentAssertions;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Users;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Users;
using NSubstitute;

namespace FreePlex.Application.Tests;

public class AuthServiceTests
{
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    public AuthServiceTests()
    {
        _uow.Users.Returns(_users);
        _hasher.Hash(Arg.Any<string>()).Returns("hashed");
    }

    private AuthService CreateSut() => new(_uow, _hasher, _tokens, TimeProvider.System);

    [Fact]
    public async Task Setup_creates_admin_when_no_users_exist()
    {
        _users.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        var result = await sut.SetupAsync(new SetupRequest { Username = "root", Password = "password123" }, default);

        result.Role.Should().Be(UserRole.Admin);
        await _users.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Setup_conflicts_when_admin_already_exists()
    {
        _users.CountAsync(Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        var act = () => sut.SetupAsync(new SetupRequest { Username = "root", Password = "password123" }, default);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Setup_validates_short_password()
    {
        _users.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        var act = () => sut.SetupAsync(new SetupRequest { Username = "root", Password = "short" }, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Login_rejects_unknown_user()
    {
        _users.GetByUsernameAsync("ghost", Arg.Any<CancellationToken>()).Returns((User?)null);
        var sut = CreateSut();

        var act = () => sut.LoginAsync(new LoginRequest { Username = "ghost", Password = "whatever1" }, default);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Login_rejects_wrong_password()
    {
        var user = new User("alex", "storedhash", UserRole.Admin, DateTimeOffset.UtcNow);
        _users.GetByUsernameAsync("alex", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("storedhash", "badpass12").Returns(false);
        var sut = CreateSut();

        var act = () => sut.LoginAsync(new LoginRequest { Username = "alex", Password = "badpass12" }, default);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
