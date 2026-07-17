namespace EveDeck.Models;

// One EVE solar system node in the stargate graph. Wormhole systems have no stargates, so
// NeighborIds can be empty -- it is never null.
public sealed class SystemNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<int> NeighborIds { get; set; } = new();
}

// Pure in-memory stargate graph: no network/file I/O, trivially unit-testable. Built once (see
// SystemJumpGraphService) and queried repeatedly for "how many jumps between these two named
// systems" lookups.
public sealed class SystemJumpGraph
{
    private readonly Dictionary<int, SystemNode> _byId;
    private readonly Dictionary<string, SystemNode> _byName;

    public SystemJumpGraph(IEnumerable<SystemNode> nodes)
    {
        _byId = new Dictionary<int, SystemNode>();
        _byName = new Dictionary<string, SystemNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            _byId[node.Id] = node;
            _byName[node.Name] = node;
        }
    }

    public int Count => _byId.Count;

    public SystemNode? GetById(int id) => _byId.TryGetValue(id, out var node) ? node : null;
    public SystemNode? GetByName(string name) => _byName.TryGetValue(name, out var node) ? node : null;

    // Case-insensitive name match on both ends; null if either name is unknown. 0 if the same system.
    // Otherwise a plain BFS over NeighborIds that gives up as soon as the next hop would exceed
    // maxDistance, so a "within N jumps" query never has to walk the whole ~8000-system graph.
    public int? DistanceBetween(string fromSystemName, string toSystemName, int maxDistance)
    {
        if (!_byName.TryGetValue(fromSystemName, out var from)) return null;
        if (!_byName.TryGetValue(toSystemName, out var to)) return null;
        if (from.Id == to.Id) return 0;

        var visited = new HashSet<int> { from.Id };
        var queue = new Queue<(SystemNode Node, int Dist)>();
        queue.Enqueue((from, 0));

        while (queue.Count > 0)
        {
            var (node, dist) = queue.Dequeue();
            var nextDist = dist + 1;
            if (nextDist > maxDistance) continue;

            foreach (var neighborId in node.NeighborIds)
            {
                if (!visited.Add(neighborId)) continue;
                if (neighborId == to.Id) return nextDist;
                if (_byId.TryGetValue(neighborId, out var neighborNode)) queue.Enqueue((neighborNode, nextDist));
            }
        }
        return null;
    }
}
