using System.Reflection;

namespace WitcherTrack.App;

/// <summary>
/// The files carried inside the executable: the web interface, the catalogue, the map
/// data and the licence.
/// </summary>
/// <remarks>
/// <para>
/// A release is one file. Anything the tracker needs to be useful on its own therefore
/// travels inside the binary, so that a bare <c>WitcherTrack.exe</c> downloaded by itself
/// is a whole tracker rather than one that starts at 0/0 and draws a map with no map on
/// it.
/// </para>
/// <para>
/// This does not make the data unchangeable, which would be worse than not embedding it:
/// every caller looks on disk first and comes here only when it finds nothing. Rebuilding
/// the catalogue after a game update stays a matter of dropping a new
/// <c>catalog.json</c> beside the executable.
/// </para>
/// </remarks>
public static class EmbeddedAssets
{
    /// <summary>Opens an embedded asset by file name, or null when it is not there.</summary>
    /// <remarks>
    /// Resource names are derived from the root namespace and folder path, which is not the
    /// same as the assembly name here, and the data files set theirs explicitly because they
    /// live outside the project folder. Matching on the suffix covers both and keeps working
    /// if either name changes.
    /// </remarks>
    public static Stream? Open(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Assembly assembly = typeof(EmbeddedAssets).Assembly;

        string? resource = Array.Find(
            assembly.GetManifestResourceNames(),
            candidate => candidate.EndsWith("." + name, StringComparison.Ordinal));

        return resource is null ? null : assembly.GetManifestResourceStream(resource);
    }

    /// <summary>Reads an embedded text asset, or null when it is not there.</summary>
    public static string? ReadText(string name)
    {
        using Stream? stream = Open(name);

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
