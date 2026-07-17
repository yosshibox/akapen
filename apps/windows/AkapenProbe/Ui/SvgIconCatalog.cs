using System.Reflection;
using System.Xml.Linq;

namespace AkapenProbe.Ui;

public sealed record SvgIcon(string Id, string PathData, IReadOnlyList<SvgSegment> Segments, bool Filled);

/// <summary>Loads the canonical embedded SVG sprite without platform graphics dependencies.</summary>
public static class SvgIconCatalog
{
    public const int ViewBoxSize = 24;
    public const float StrokeWidth = 1.75f;

    private static readonly Lazy<IReadOnlyDictionary<string, SvgIcon>> Icons = new(Load);

    public static SvgIcon For(UiCommandId command) => Icons.Value[UiCommand.Name(command)];
    public static int Count => Icons.Value.Count;

    private static IReadOnlyDictionary<string, SvgIcon> Load()
    {
        Assembly assembly = typeof(SvgIconCatalog).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("akapen-ui-icons.svg", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded Akapen SVG icon sprite is missing.");
        XDocument document = XDocument.Load(stream);
        XNamespace svg = "http://www.w3.org/2000/svg";
        return document.Descendants(svg + "symbol").ToDictionary(
            symbol => (string)symbol.Attribute("id")!,
            symbol =>
            {
                string id = (string)symbol.Attribute("id")!;
                string pathData = string.Join(" ", symbol.Elements(svg + "path").Select(path => (string)path.Attribute("d")!));
                // V1.2 (Fluent UI System Icons): sprite symbols declare
                // fill="currentColor" — these are monochrome FILL paths
                // (nonzero rule), not stroke outlines.
                bool filled = !string.Equals((string?)symbol.Attribute("fill"), "none", StringComparison.Ordinal);
                return new SvgIcon(id, pathData, SvgPathParser.Parse(pathData), filled);
            },
            StringComparer.Ordinal);
    }
}
