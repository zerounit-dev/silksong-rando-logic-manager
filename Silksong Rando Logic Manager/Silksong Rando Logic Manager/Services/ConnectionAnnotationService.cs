using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class ConnectionAnnotationService
{
    private readonly LogicValidationService validation = new();

    public ConnectionAnnotationGroup? FindEligibleGroup(SubroomConnection connection, IEnumerable<SubroomConnection> connections, IEnumerable<Subroom> subrooms)
    {
        if (connection.IsArchived || string.IsNullOrWhiteSpace(connection.Alias)) return null;
        var group = connections.Where(item => !item.IsArchived && item.RoomId == connection.RoomId && SameAlias(item.Alias, connection.Alias)).ToArray();
        return group.Length > 0 && group.All(item =>
            validation.ConnectionAlias(item) == ValidationSeverity.Neutral &&
            validation.ConnectionFriendlyName(item) == ValidationSeverity.Neutral &&
            validation.ConnectionPathway(item, connections) == ValidationSeverity.Neutral &&
            validation.ConnectionWithoutSubrooms(item, subrooms) == ValidationSeverity.Neutral)
            ? new ConnectionAnnotationGroup(connection.RoomId, connection.Alias, group)
            : null;
    }

    public IReadOnlyList<ConnectionAnnotationGroup> GetEligibleGroups(IEnumerable<SubroomConnection> connections, IEnumerable<Subroom> subrooms) =>
        connections.Where(item => !item.IsArchived && !string.IsNullOrWhiteSpace(item.Alias))
            .GroupBy(item => item.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => FindEligibleGroup(group.First(), connections, subrooms))
            .Where(group => group is not null)
            .Cast<ConnectionAnnotationGroup>()
            .ToArray();

    private static bool SameAlias(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record ConnectionAnnotationGroup(Guid RoomId, string Alias, IReadOnlyList<SubroomConnection> Rows);
