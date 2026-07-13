using System.Globalization;
using System.Text.RegularExpressions;

namespace AkapenProbe.Ui;

public enum SvgSegmentKind { Move, Line, Cubic, Arc, Close }

public readonly record struct SvgSegment(SvgSegmentKind Kind, IReadOnlyList<float> Values);

/// <summary>Strict parser for the absolute SVG path subset used by Akapen icons.</summary>
public static partial class SvgPathParser
{
    [GeneratedRegex("[AaCcHhLlMmVvZz]|[-+]?(?:\\d*\\.\\d+|\\d+\\.?)(?:[eE][-+]?\\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static IReadOnlyList<SvgSegment> Parse(string pathData)
    {
        string[] tokens = TokenPattern().Matches(pathData).Select(match => match.Value).ToArray();
        var result = new List<SvgSegment>();
        int index = 0;
        char command = '\0';
        while (index < tokens.Length)
        {
            if (char.IsLetter(tokens[index][0])) command = tokens[index++][0];
            if (command == '\0' || char.IsLower(command))
                throw new FormatException("Akapen SVG icons must use absolute path commands.");
            switch (command)
            {
                case 'M':
                    result.Add(new SvgSegment(SvgSegmentKind.Move, Read(tokens, ref index, 2)));
                    command = 'L';
                    break;
                case 'L': result.Add(new SvgSegment(SvgSegmentKind.Line, Read(tokens, ref index, 2))); break;
                case 'H': result.Add(new SvgSegment(SvgSegmentKind.Line, new[] { Number(tokens[index++]), float.NaN })); break;
                case 'V': result.Add(new SvgSegment(SvgSegmentKind.Line, new[] { float.NaN, Number(tokens[index++]) })); break;
                case 'C': result.Add(new SvgSegment(SvgSegmentKind.Cubic, Read(tokens, ref index, 6))); break;
                case 'A': result.Add(new SvgSegment(SvgSegmentKind.Arc, Read(tokens, ref index, 7))); break;
                case 'Z': result.Add(new SvgSegment(SvgSegmentKind.Close, Array.Empty<float>())); command = '\0'; break;
                default: throw new FormatException($"Unsupported Akapen SVG path command '{command}'.");
            }
        }
        return result;
    }

    private static float[] Read(string[] tokens, ref int index, int count)
    {
        if (index + count > tokens.Length || tokens.Skip(index).Take(count).Any(token => char.IsLetter(token[0])))
            throw new FormatException("Incomplete Akapen SVG path command.");
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = Number(tokens[index++]);
        return values;
    }

    private static float Number(string token) => float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
}
