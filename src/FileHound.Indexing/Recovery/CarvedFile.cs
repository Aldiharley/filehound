using FileHound.Core.Carving;

namespace FileHound.Indexing.Recovery;

/// <summary>A file found by signature in free space: where it starts and how long the validator says it is. The <c>Key</c> of Carving candidates.</summary>
public sealed record CarvedFile(CarveType Type, long StartLcn, long Size, string? Info)
{
    /// <summary>FR-24: <c>&lt;type&gt;_&lt;LCN&gt;.&lt;ext&gt;</c>, with the validator's subtype (docx, dll…) when it reported one.</summary>
    public string SuggestedName => $"{Type.Id}_{StartLcn}{Signatures.ExtensionFor(Type, Info)}";
}

public sealed record CarveProgress(long BytesScanned, long FreeBytes, int Found, TimeSpan? Eta);
