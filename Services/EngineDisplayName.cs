using System.Linq;

namespace MovieHunter.Services;

/// <summary>
/// Maps SearXNG engine identifiers (lowercase, no spaces) to user-friendly
/// display names shown in the UI. Fall back to a generic title-case helper
/// for engines not explicitly listed here.
/// </summary>
internal static class EngineDisplayName
{
    public static string For(string engineName)
    {
        return engineName.ToLowerInvariant() switch
        {
            "hdfilme" => "HDfilme (hdfilme.win)",
            _ => GenericPretty(engineName),
        };
    }

    private static string GenericPretty(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i == 0 ? char.ToUpperInvariant(c).ToString() :
            (name[i - 1] == ' ' || name[i - 1] == '.') ? char.ToUpperInvariant(c).ToString() :
            c.ToString()));
    }
}
