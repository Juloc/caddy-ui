using System.Reflection;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class RoutePersistenceOwnershipTests
{
    private static readonly string[] ApplyPersistenceMethods =
    [
        "CreateRevisionAsync",
        "GetRevisionAsync",
        "GetRevisionByDigestAsync",
        "ListRevisionsAsync",
        "CreateSnapshotAsync",
        "GetSnapshotAsync",
        "StartOperationAsync",
        "RecordOperationStepAsync",
        "CompleteOperationAsync",
        "ListOperationsAsync",
        "GetLatestAppliedOperationAsync",
    ];

    [Fact]
    public void RouteManagementStore_DoesNotOwnApplyPersistence()
    {
        var routeMethods = typeof(RouteManagementStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var applyMethods = typeof(RouteApplyStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in ApplyPersistenceMethods)
        {
            Assert.DoesNotContain(method, routeMethods);
            Assert.Contains(method, applyMethods);
        }
    }

    [Fact]
    public void RouteStores_UseSharedRelationalPlumbing()
    {
        var privateFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var routePrivateMethods = typeof(RouteManagementStore)
            .GetMethods(privateFlags)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var applyPrivateMethods = typeof(RouteApplyStore)
            .GetMethods(privateFlags)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("OpenConnectionAsync", routePrivateMethods);
        Assert.DoesNotContain("AddParameter", routePrivateMethods);
        Assert.DoesNotContain("OpenConnectionAsync", applyPrivateMethods);
        Assert.DoesNotContain("AddParameter", applyPrivateMethods);
        Assert.DoesNotContain("ExecuteAsync", applyPrivateMethods);

        var supportType = typeof(RouteManagementStore).Assembly.GetType(
            "CaddyUi.Infrastructure.Persistence.RelationalStoreSupport",
            throwOnError: true)!;
        var supportMethods = supportType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OpenConnectionAsync", supportMethods);
        Assert.Contains("AddParameter", supportMethods);
        Assert.Contains("ExecuteNonQueryAsync", supportMethods);
    }
}
