namespace LeanViz;

/// <summary>
/// The declaration graph once every module has been read: who references whom, the reverse of that, and which
/// axioms each declaration transitively rests on.
///
/// Sized for Mathlib, where this is 791,453 nodes and 20.9 million edges. Reverse edges are stored as CSR
/// (an offset array plus one flat array) rather than a list per node, because 791,453 small arrays cost more in
/// object headers than the edges do in content. The axiom closure is a bitset, two 64-bit words per declaration
/// for Mathlib's 84 axioms, so a union is two OR instructions rather than a set merge. Both together take under
/// a second.
/// </summary>
internal sealed class Graph
{
    public required int[][] Out;          // out-edges per id, sorted, no self edges
    public required int[] InOffset;       // CSR offsets into InFlat, length n+1
    public required int[] InFlat;         // in-edges, sorted per node
    public required ulong[] AxiomBits;    // n * Words bitset, one bit per axiom in AxiomIds order
    public required int[] AxiomIds;       // ids of the axioms, in id order
    public int Words => (AxiomIds.Length + 63) / 64;
    public long EdgeCount => InFlat.LongLength;

    public static Graph Build(int[][] outEdges, bool[] isAxiom)
    {
        int n = outEdges.Length;
        var inCount = new int[n + 1];
        long edges = 0;
        foreach (int[] o in outEdges)
        {
            edges += o.Length;
            foreach (int t in o)
            {
                inCount[t]++;
            }
        }
        var inOffset = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            inOffset[i + 1] = inOffset[i] + inCount[i];
        }
        var fill = (int[])inOffset.Clone();
        var inFlat = new int[edges];
        for (int v = 0; v < n; v++)
        {
            foreach (int t in outEdges[v])
            {
                inFlat[fill[t]++] = v; // v ascending, so each node's in-list comes out sorted
            }
        }

        var axiomIds = new List<int>();
        var axiomIndex = new int[n];
        Array.Fill(axiomIndex, -1);
        for (int i = 0; i < n; i++)
        {
            if (isAxiom[i])
            {
                axiomIndex[i] = axiomIds.Count;
                axiomIds.Add(i);
            }
        }
        int words = (axiomIds.Count + 63) / 64;
        var bits = new ulong[(long)n * words];

        // Depth-first, so that a node is finished after everything it references, except across a cycle, which a
        // mutual block can make; a fixpoint pass afterwards settles those, and reports how many passes it took.
        // The walk is an explicit stack: Mathlib's reference graph is thousands of levels deep and recursion
        // overflows. The fixpoint is not an optimization to skip, since a single topological pass would silently
        // give a mutual block only the axioms of whichever member was finished first.
        var state = new byte[n];
        var stack = new Stack<(int Node, int Pos)>();
        for (int root = 0; root < n; root++)
        {
            if (state[root] != 0)
            {
                continue;
            }
            state[root] = 1;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                (int v, int pos) = stack.Pop();
                if (pos < outEdges[v].Length)
                {
                    stack.Push((v, pos + 1));
                    int t = outEdges[v][pos];
                    if (state[t] == 0)
                    {
                        state[t] = 1;
                        stack.Push((t, 0));
                    }
                    continue;
                }
                Combine(v);
                state[v] = 2;
            }
        }
        for (int pass = 1; ; pass++)
        {
            bool changed = false;
            for (int v = 0; v < n; v++)
            {
                changed |= Combine(v);
            }
            if (!changed)
            {
                Console.WriteLine($"  axiom closure settled after {pass} fixpoint pass{(pass == 1 ? "" : "es")}");
                break;
            }
        }
        return new Graph { Out = outEdges, InOffset = inOffset, InFlat = inFlat, AxiomBits = bits, AxiomIds = axiomIds.ToArray() };

        bool Combine(int v)
        {
            bool changed = false;
            long b = (long)v * words;
            if (axiomIndex[v] >= 0)
            {
                ulong mask = 1UL << (axiomIndex[v] % 64);
                long at = b + axiomIndex[v] / 64;
                if ((bits[at] & mask) == 0)
                {
                    bits[at] |= mask;
                    changed = true;
                }
            }
            foreach (int t in outEdges[v])
            {
                long tb = (long)t * words;
                for (int w = 0; w < words; w++)
                {
                    ulong nv = bits[b + w] | bits[tb + w];
                    if (nv != bits[b + w])
                    {
                        bits[b + w] = nv;
                        changed = true;
                    }
                }
            }
            return changed;
        }
    }

    public IEnumerable<int> AxiomsOf(int id)
    {
        int words = Words;
        for (int w = 0; w < words; w++)
        {
            ulong x = AxiomBits[(long)id * words + w];
            while (x != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(x);
                yield return w * 64 + bit;
                x &= x - 1;
            }
        }
    }

    public ReadOnlySpan<int> In(int id) => new(InFlat, InOffset[id], InOffset[id + 1] - InOffset[id]);

    /// <summary>Up to <paramref name="cap"/> dependents of <paramref name="id"/>, the most depended-upon first, ties by id.</summary>
    public int[] MostUsed(int id, int cap)
    {
        ReadOnlySpan<int> ins = In(id);
        if (ins.Length <= cap)
        {
            var all = ins.ToArray();
            Array.Sort(all, Compare);
            return all;
        }
        // a bounded selection: keep the best cap seen so far in a sorted buffer
        var best = new List<int>(cap + 1);
        foreach (int t in ins)
        {
            if (best.Count == cap && Compare(t, best[^1]) >= 0)
            {
                continue;
            }
            int at = best.BinarySearch(t, Comparer<int>.Create(Compare));
            best.Insert(at < 0 ? ~at : at, t);
            if (best.Count > cap)
            {
                best.RemoveAt(best.Count - 1);
            }
        }
        return best.ToArray();

        int Compare(int a, int b)
        {
            int ua = InOffset[a + 1] - InOffset[a], ub = InOffset[b + 1] - InOffset[b];
            return ua != ub ? ub.CompareTo(ua) : a.CompareTo(b);
        }
    }
}
