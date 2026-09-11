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
}
