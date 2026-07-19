using FluentAssertions;
using LumenMedia.Domain.Users;
using NetArchTest.Rules;

namespace LumenMedia.Application.Tests;

public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly DomainAssembly = typeof(User).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(LumenMedia.Application.DependencyInjection).Assembly;

    [Fact]
    public void Domain_has_no_dependency_on_other_layers_or_infrastructure_frameworks()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "LumenMedia.Application",
                "LumenMedia.Infrastructure",
                "LumenMedia.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain must stay pure. Offending types: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("LumenMedia.Infrastructure", "LumenMedia.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application depends only on Domain + abstractions. Offending types: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
