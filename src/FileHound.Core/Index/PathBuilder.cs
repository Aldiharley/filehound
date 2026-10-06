namespace FileHound.Core.Index;

/// <summary>Builds full paths on demand by walking parent links (paths are never stored).</summary>
public static class PathBuilder
{
    private const int MaxChain = 4096;

    public static string GetFullPath(VolumeIndex v, int e)
    {
        if (e == VolumeIndex.RootEntry) return v.Root;
        Span<int> chainBuf = stackalloc int[128];
        int[]? rented = null;
        int depth = 0;
        int length = v.RootName.Length;
        for (int x = e; x > 0 && depth < MaxChain; x = v.Parent(x))
        {
            if (depth == chainBuf.Length)
            {
                var bigger = new int[chainBuf.Length * 2];
                chainBuf.CopyTo(bigger);
                rented = bigger;
                chainBuf = bigger;
            }
            chainBuf[depth++] = x;
            length += 1 + v.Name(x).Length;
        }
        var chain = rented ?? chainBuf[..depth].ToArray();
        return string.Create(length, (v, chain, depth), static (dst, s) =>
        {
            s.v.RootName.AsSpan().CopyTo(dst);
            int pos = s.v.RootName.Length;
            for (int i = s.depth - 1; i >= 0; i--)
            {
                dst[pos++] = '\\';
                var name = s.v.Name(s.chain[i]);
                name.CopyTo(dst[pos..]);
                pos += name.Length;
            }
        });
    }

    /// <summary>Full path of the folder containing <paramref name="e"/>.</summary>
    public static string GetParentPath(VolumeIndex v, int e)
    {
        int p = v.Parent(e);
        if (p < 0) return string.Empty;
        return p == VolumeIndex.RootEntry ? v.Root : GetFullPath(v, p);
    }

    /// <summary>True when any ancestor's lowercase name (including the root name) contains <paramref name="fold"/>.</summary>
    public static bool AncestorFoldContains(VolumeIndex v, int e, ReadOnlySpan<char> fold)
    {
        int guard = 0;
        for (int p = v.Parent(e); p >= 0 && guard++ < MaxChain; p = v.Parent(p))
            if (v.FoldName(p).Contains(fold, StringComparison.Ordinal)) return true;
        return false;
    }
}
