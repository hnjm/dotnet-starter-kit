using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Auditing.Contracts.Dtos;
using FSH.Modules.Auditing.Persistence;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Auditing;

/// <summary>
/// Runtime repro for audit finding DATA-01 (unbounded audit list). The security-audit list handler
/// does ToListAsync with no Skip/Take/cap and the validator does not require a time window, so an
/// unpaged call materializes the whole matching set. A bounded API would cap the page size.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class SecurityAuditListBoundsTests
{
    private const int SeededRows = 500;

    // Analogous to the 200-row server cap that SessionService.GetTenantSessionsAsync already applies
    // "so an over-eager client can't pull a tenant's full session table in one round-trip".
    private const int ExpectedServerCap = 200;

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public SecurityAuditListBoundsTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task GetSecurityAudits_Should_CapResults_When_NoPagingOrWindowProvided()
    {
        await SeedSecurityAuditsAsync(SeededRows);

        using var client = await _auth.CreateRootAdminClientAsync();
        using var response = await client.GetAsync($"{TestConstants.AuditsBasePath}/security");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.DeserializeAsync<IReadOnlyList<AuditSummaryDto>>();

        list.Count.ShouldBeLessThanOrEqualTo(
            ExpectedServerCap,
            $"an unpaged security-audit list must cap results; seeded {SeededRows} rows and the endpoint " +
            $"returned {list.Count} with no Skip/Take/default page size.");
    }

    private async Task SeedSecurityAuditsAsync(int count)
    {
        using var scope = _factory.Services.CreateScope();

        // Tenant context is AsyncLocal — set inline in the same method as the DbContext call.
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await tenantStore.GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var now = DateTime.UtcNow;
        var rows = new List<AuditRecord>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new AuditRecord
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = now.AddSeconds(-i),
                ReceivedAtUtc = now,
                EventType = (int)AuditEventType.Security,
                Severity = (byte)AuditSeverity.Information,
                TenantId = TestConstants.RootTenantId,
                UserId = "data01-seed-user",
                UserName = "data01-seed",
                Source = "DATA-01-test",
                Tags = 0,
                PayloadJson = "{}",
            });
        }

        db.AuditRecords.AddRange(rows);
        await db.SaveChangesAsync();
    }
}
