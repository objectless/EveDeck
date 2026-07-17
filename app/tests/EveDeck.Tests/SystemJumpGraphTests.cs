using Xunit;
using EveDeck.Models;

namespace EveDeck.Tests;

public class SystemJumpGraphTests
{
    // Hand-built topology, not real ESI data: Jita -- Perimeter -- Urlen -- Sobaseki -- Malkalen
    // chain, with Perimeter also branching to Ashab, and Colelie left with no edges at all (never
    // reachable from anything).
    //
    //   Jita -- Perimeter -- Urlen -- Sobaseki -- Malkalen
    //              |
    //            Ashab
    //
    //   Colelie (isolated)
    private static SystemJumpGraph BuildTestGraph()
    {
        var nodes = new[]
        {
            Node(30000142, "Jita", 30000144),
            Node(30000144, "Perimeter", 30000142, 30002766, 30003504),
            Node(30002766, "Urlen", 30000144, 30002770),
            Node(30002770, "Sobaseki", 30002766, 30002718),
            Node(30002718, "Malkalen", 30002770),
            Node(30003504, "Ashab", 30000144),
            Node(30002697, "Colelie"),
        };
        return new SystemJumpGraph(nodes);
    }

    private static SystemNode Node(int id, string name, params int[] neighborIds)
        => new() { Id = id, Name = name, NeighborIds = neighborIds.ToList() };

    [Fact]
    public void Graph_Count_ReflectsNodeCount()
    {
        var graph = BuildTestGraph();
        Assert.Equal(7, graph.Count);
    }

    [Theory]
    [InlineData("Jita", "Jita")]
    [InlineData("Jita", "JITA")]
    [InlineData("Colelie", "colelie")]
    public void DistanceBetween_SameSystem_ReturnsZero(string from, string to)
    {
        var graph = BuildTestGraph();
        Assert.Equal(0, graph.DistanceBetween(from, to, maxDistance: 10));
    }

    [Fact]
    public void DistanceBetween_DirectNeighbor_ReturnsOne()
    {
        var graph = BuildTestGraph();
        Assert.Equal(1, graph.DistanceBetween("Jita", "Perimeter", maxDistance: 10));
    }

    [Theory]
    [InlineData("Jita", "Urlen", 2)]
    [InlineData("Jita", "Ashab", 2)]
    [InlineData("Jita", "Sobaseki", 3)]
    [InlineData("Jita", "Malkalen", 4)]
    public void DistanceBetween_MultiHopPath_ReturnsCorrectDistance(string from, string to, int expected)
    {
        var graph = BuildTestGraph();
        Assert.Equal(expected, graph.DistanceBetween(from, to, maxDistance: 10));
    }

    [Fact]
    public void DistanceBetween_BeyondMaxDistance_ReturnsNull()
    {
        var graph = BuildTestGraph();
        // Jita -> Malkalen is 4 hops; capping the search at 3 must not find it.
        Assert.Null(graph.DistanceBetween("Jita", "Malkalen", maxDistance: 3));
    }

    [Fact]
    public void DistanceBetween_ExactlyAtMaxDistance_ReturnsDistance()
    {
        var graph = BuildTestGraph();
        // maxDistance is inclusive of the true distance -- boundary must still resolve, not just "under".
        Assert.Equal(4, graph.DistanceBetween("Jita", "Malkalen", maxDistance: 4));
        Assert.Equal(3, graph.DistanceBetween("Jita", "Sobaseki", maxDistance: 3));
    }

    [Fact]
    public void DistanceBetween_IsolatedSystem_ReturnsNull()
    {
        var graph = BuildTestGraph();
        Assert.Null(graph.DistanceBetween("Jita", "Colelie", maxDistance: 100));
        Assert.Null(graph.DistanceBetween("Colelie", "Jita", maxDistance: 100));
    }

    [Theory]
    [InlineData("Nonexistent", "Jita")]
    [InlineData("Jita", "Nonexistent")]
    [InlineData("Nonexistent", "AlsoNonexistent")]
    public void DistanceBetween_UnknownSystemName_ReturnsNullWithoutThrowing(string from, string to)
    {
        var graph = BuildTestGraph();
        Assert.Null(graph.DistanceBetween(from, to, maxDistance: 10));
    }

    [Fact]
    public void DistanceBetween_NameLookupIsCaseInsensitive()
    {
        var graph = BuildTestGraph();
        Assert.Equal(1, graph.DistanceBetween("jita", "PERIMETER", maxDistance: 10));
        Assert.Equal(3, graph.DistanceBetween("JITA", "sobaseki", maxDistance: 10));
    }
}
