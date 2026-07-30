using FSH.Framework.Quota;
using FSH.Framework.Shared.Quota;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.Quota;

/// <summary>
/// Guards the "quota enabled" DI wiring. <see cref="Extensions.AddHeroQuotas"/> registers the quota
/// service by type and the enforcement middleware resolves it per request; the host disables
/// ValidateOnBuild, so a service the container cannot construct only fails at resolution time — which
/// is why a non-public <see cref="InMemoryQuotaService"/> constructor turned every authenticated
/// request (login included) into a 500. These tests therefore RESOLVE the service inside a scope
/// (mirroring the middleware), not merely register it.
/// </summary>
public sealed class QuotaExtensionsTests
{
    private static ServiceProvider BuildProvider(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QuotaOptions:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHeroQuotas(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddHeroQuotas_Should_ResolveInMemoryService_When_EnabledWithoutRedis()
    {
        // Arrange
        using var provider = BuildProvider(enabled: true);
        using var scope = provider.CreateScope();

        // Act — this is the exact resolution the enforcement middleware performs per request.
        var quotaService = scope.ServiceProvider.GetRequiredService<IQuotaService>();

        // Assert
        quotaService.ShouldBeOfType<InMemoryQuotaService>();
    }

    [Fact]
    public void AddHeroQuotas_Should_ResolveNoopService_When_Disabled()
    {
        // Arrange
        using var provider = BuildProvider(enabled: false);
        using var scope = provider.CreateScope();

        // Act
        var quotaService = scope.ServiceProvider.GetRequiredService<IQuotaService>();

        // Assert
        quotaService.ShouldBeOfType<NoopQuotaService>();
    }
}
