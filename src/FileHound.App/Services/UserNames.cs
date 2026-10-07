namespace FileHound.App.Services;

/// <summary>Works out what to call the user in the greeting.</summary>
public static class UserNames
{
    public const string Fallback = "there";

    /// <summary>
    /// The name to greet with: the name the user typed in Settings, else the first name from their Windows display name
    /// (as shown on the sign-in screen), else their account name, else a friendly "there".
    /// </summary>
    public static string Resolve(string? preferred) => Pick(preferred, WindowsDisplayName(), Environment.UserName);

    /// <summary>What the greeting shows when no name has been set (for the Settings hint).</summary>
    public static string Automatic() => Pick(null, WindowsDisplayName(), Environment.UserName);

    internal static string Pick(string? preferred, string? displayName, string? accountName)
    {
        var chosen = preferred?.Trim();
        if (!string.IsNullOrEmpty(chosen)) return chosen;
        var first = FirstWord(displayName);
        if (first is null && FirstWord(accountName) is { } account)
            first = char.ToUpperInvariant(account[0]) + account[1..]; // account names are usually lower-case
        return first ?? Fallback;
    }

    /// <summary>"Dennis Davison", "Davison, Dennis", "dennis.davison" and @"CORP\dennis" all give the given name.</summary>
    private static string? FirstWord(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        int slash = t.LastIndexOf('\\');
        if (slash >= 0) t = t[(slash + 1)..];
        int comma = t.LastIndexOf(',');
        if (comma >= 0) t = t[(comma + 1)..];
        var word = t.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(word) ? null : word;
    }

    private static unsafe string? WindowsDisplayName()
    {
        const int NameDisplay = 3; // EXTENDED_NAME_FORMAT.NameDisplay
        Span<char> buffer = stackalloc char[256];
        uint size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            // Fails (ERROR_NONE_MAPPED) for local accounts with no full name set; the caller then falls back.
            if (!NativeMethods.GetUserNameExW(NameDisplay, p, ref size) || size == 0 || size > buffer.Length) return null;
        }
        return new string(buffer[..(int)size]);
    }
}
