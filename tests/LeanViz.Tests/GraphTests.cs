using LeanViz;
using Xunit;

namespace LeanViz.Tests;

public class GraphTests
{
    // 0 -> 1 -> 2 (axiom); 3 -> 1; 4 <-> 5 (a mutual block), 5 -> 2; 6 is alone and an axiom itself
    private static readonly int[][] Out =
    [
        [1], [2], [], [1], [5], [2, 4], [],
    ];
    private static readonly bool[] Axiom = [false, false, true, false, false, false, true];

    [Fact]
    public void ReverseEdgesAreSortedAndComplete()
    {
        Graph g = Graph.Build(Out, Axiom);
        Assert.Equal(6, g.EdgeCount);
        Assert.Equal([0, 3], g.In(1).ToArray());
        Assert.Equal([1, 5], g.In(2).ToArray());
        Assert.Empty(g.In(0).ToArray());
        Assert.Equal([5], g.In(4).ToArray());
    }

    [Fact]
    public void AxiomClosureFollowsReferencesAndSurvivesACycle()
    {
        Graph g = Graph.Build(Out, Axiom);
        Assert.Equal([2, 6], g.AxiomIds);
        Assert.Equal([0], g.AxiomsOf(0).ToArray()); // axiom index 0 is declaration 2
        Assert.Equal([0], g.AxiomsOf(3).ToArray());
        Assert.Equal([0], g.AxiomsOf(4).ToArray()); // through the cycle 4 -> 5 -> 2
        Assert.Equal([0], g.AxiomsOf(5).ToArray());
        Assert.Equal([0], g.AxiomsOf(2).ToArray()); // an axiom rests on itself
        Assert.Equal([1], g.AxiomsOf(6).ToArray());
    }

    [Fact]
    public void MostUsedKeepsTheMostDependedUponDependents()
    {
        // node 1 is used by 0 and 3; node 0 is used by nobody, node 3 by nobody either: ties break by id
        Graph g = Graph.Build(Out, Axiom);
        Assert.Equal([0, 3], g.MostUsed(1, 10));
        Assert.Equal([0], g.MostUsed(1, 1));
        // node 2 is used by 1 (itself used twice) and 5 (used once): 1 first
        Assert.Equal([1, 5], g.MostUsed(2, 10));
        Assert.Equal([1], g.MostUsed(2, 1));
    }

    [Fact]
    public void MoreThanSixtyFourAxiomsFitTheBitset()
    {
        int n = 70;
        var outs = new int[n + 1][];
        var ax = new bool[n + 1];
        for (int i = 0; i < n; i++) { outs[i] = []; ax[i] = true; }
        outs[n] = Enumerable.Range(0, n).ToArray();
        Graph g = Graph.Build(outs, ax);
        Assert.Equal(2, g.Words);
        Assert.Equal(Enumerable.Range(0, n), g.AxiomsOf(n));
        Assert.Equal([69], g.AxiomsOf(69).ToArray());
    }
}
