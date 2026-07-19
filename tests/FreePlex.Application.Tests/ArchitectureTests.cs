using FluentAssertions;
using FreePlex.Domain.Users;
using NetArchTest.Rules;

namespace FreePlex.Application.Tests;

public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly DomainAssembly = typeof(User).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(FreePlex.Application.DependencyInjection).Assembly;

    [Fact]
    public void Domain_has_no_dependency_on_other_layers_or_infrastructure_frameworks()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "FreePlex.Application",
                "FreePlex.Infrastructure",
                "FreePlex.Api",
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
            .HaveDependencyOnAny("FreePlex.Infrastructure", "FreePlex.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application depends only on Domain + abstractions. Offending types: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
