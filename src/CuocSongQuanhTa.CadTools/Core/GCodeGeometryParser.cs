using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Geometry;

namespace CuocSongQuanhTa.CadTools.Core;

public sealed record GCodeSegment(Point2d Start, Point2d End, double Bulge, bool IsArc);

public sealed class GCodePath
{
    public List<GCodeSegment> Segments { get; } = [];

    public bool IsClosed => Segments.Count > 1 &&
                            Segments[^1].End.GetDistanceTo(Segments[0].Start) <= 1e-6;
}

public sealed record GCodeParseResult(
    IReadOnlyList<GCodePath> Paths,
    int LineSegmentCount,
    int ArcSegmentCount,
    int IgnoredArcCount);

public static partial class GCodeGeometryParser
{
    private enum MotionMode
    {
        Rapid,
        Linear,
        ClockwiseArc,
        CounterClockwiseArc
    }

    [GeneratedRegex(@"[A-Za-z][-+]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.Compiled)]
    private static partial Regex ParenthesisCommentRegex();

    public static GCodeParseResult ParseFile(string filePath) => ParseLines(File.ReadLines(filePath));

    public static GCodeParseResult ParseLines(IEnumerable<string> lines)
    {
        var paths = new List<GCodePath>();
        GCodePath? activePath = null;
        var motion = MotionMode.Rapid;
        var absolute = true;
        var unitScale = 1.0;
        var currentX = 0.0;
        var currentY = 0.0;
        var currentZ = 0.0;
        var lineCount = 0;
        var arcCount = 0;
        var ignoredArcs = 0;

        void FlushPath()
        {
            if (activePath is { Segments.Count: > 0 })
            {
                paths.Add(activePath);
            }

            activePath = null;
        }

        foreach (var sourceLine in lines)
        {
            var line = ParenthesisCommentRegex().Replace(sourceLine, string.Empty);
            var semicolon = line.IndexOf(';');
            if (semicolon >= 0)
            {
                line = line[..semicolon];
            }

            var words = new Dictionary<char, double>();
            var gCodes = new List<double>();
            foreach (Match match in WordRegex().Matches(line))
            {
                var letter = char.ToUpperInvariant(match.Value[0]);
                if (!double.TryParse(
                        match.Value.AsSpan(1),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    continue;
                }

                if (letter == 'G')
                {
                    gCodes.Add(value);
                }
                else
                {
                    words[letter] = value;
                }
            }

            foreach (var gCode in gCodes)
            {
                if (NearlyEqual(gCode, 0.0)) motion = MotionMode.Rapid;
                else if (NearlyEqual(gCode, 1.0)) motion = MotionMode.Linear;
                else if (NearlyEqual(gCode, 2.0)) motion = MotionMode.ClockwiseArc;
                else if (NearlyEqual(gCode, 3.0)) motion = MotionMode.CounterClockwiseArc;
                else if (NearlyEqual(gCode, 20.0)) unitScale = 25.4;
                else if (NearlyEqual(gCode, 21.0)) unitScale = 1.0;
                else if (NearlyEqual(gCode, 90.0)) absolute = true;
                else if (NearlyEqual(gCode, 91.0)) absolute = false;
            }

            var hasX = words.TryGetValue('X', out var rawX);
            var hasY = words.TryGetValue('Y', out var rawY);
            var hasZ = words.TryGetValue('Z', out var rawZ);
            if (!hasX && !hasY && !hasZ)
            {
                continue;
            }

            var targetX = hasX
                ? absolute ? rawX * unitScale : currentX + rawX * unitScale
                : currentX;
            var targetY = hasY
                ? absolute ? rawY * unitScale : currentY + rawY * unitScale
                : currentY;
            var targetZ = hasZ
                ? absolute ? rawZ * unitScale : currentZ + rawZ * unitScale
                : currentZ;
            var hasPlanarMove = hasX || hasY;

            if (motion == MotionMode.Rapid)
            {
                FlushPath();
            }
            else if (hasPlanarMove)
            {
                var start = new Point2d(currentX, currentY);
                var end = new Point2d(targetX, targetY);
                if (start.GetDistanceTo(end) > 1e-9)
                {
                    activePath ??= new GCodePath();
                    if (activePath.Segments.Count > 0 &&
                        activePath.Segments[^1].End.GetDistanceTo(start) > 1e-6)
                    {
                        FlushPath();
                        activePath = new GCodePath();
                    }

                    if (motion == MotionMode.Linear)
                    {
                        activePath.Segments.Add(new GCodeSegment(start, end, 0.0, false));
                        lineCount++;
                    }
                    else
                    {
                        var clockwise = motion == MotionMode.ClockwiseArc;
                        if (TryCalculateArcBulge(start, end, words, unitScale, clockwise, out var bulge))
                        {
                            activePath.Segments.Add(new GCodeSegment(start, end, bulge, true));
                            arcCount++;
                        }
                        else
                        {
                            activePath.Segments.Add(new GCodeSegment(start, end, 0.0, false));
                            lineCount++;
                            ignoredArcs++;
                        }
                    }
                }
            }
            else if (hasZ && targetZ > currentZ + 1e-9)
            {
                FlushPath();
            }

            currentX = targetX;
            currentY = targetY;
            currentZ = targetZ;
        }

        FlushPath();
        return new GCodeParseResult(paths, lineCount, arcCount, ignoredArcs);
    }

    private static bool TryCalculateArcBulge(
        Point2d start,
        Point2d end,
        IReadOnlyDictionary<char, double> words,
        double unitScale,
        bool clockwise,
        out double bulge)
    {
        bulge = 0.0;
        Point2d center;
        var preferMajor = false;

        var hasI = words.TryGetValue('I', out var rawI);
        var hasJ = words.TryGetValue('J', out var rawJ);
        if (hasI || hasJ)
        {
            center = new Point2d(
                start.X + rawI * unitScale,
                start.Y + rawJ * unitScale);
        }
        else if (words.TryGetValue('R', out var rawRadius))
        {
            var radius = Math.Abs(rawRadius * unitScale);
            preferMajor = rawRadius < 0.0;
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord <= 1e-12 || radius < chord * 0.5 - 1e-8)
            {
                return false;
            }

            var midX = (start.X + end.X) * 0.5;
            var midY = (start.Y + end.Y) * 0.5;
            var height = Math.Sqrt(Math.Max(0.0, radius * radius - chord * chord * 0.25));
            var leftX = -dy / chord;
            var leftY = dx / chord;
            var candidateA = new Point2d(midX + leftX * height, midY + leftY * height);
            var candidateB = new Point2d(midX - leftX * height, midY - leftY * height);
            var sweepA = CalculateSweep(start, end, candidateA, clockwise);
            var aIsMajor = Math.Abs(sweepA) > Math.PI + 1e-9;
            center = aIsMajor == preferMajor ? candidateA : candidateB;
        }
        else
        {
            return false;
        }

        var sweep = CalculateSweep(start, end, center, clockwise);
        if (preferMajor && Math.Abs(sweep) < Math.PI - 1e-9)
        {
            sweep += clockwise ? -Math.PI * 2.0 : Math.PI * 2.0;
        }

        bulge = Math.Tan(sweep * 0.25);
        return double.IsFinite(bulge);
    }

    private static double CalculateSweep(Point2d start, Point2d end, Point2d center, bool clockwise)
    {
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var sweep = endAngle - startAngle;
        if (clockwise)
        {
            while (sweep >= 0.0) sweep -= Math.PI * 2.0;
        }
        else
        {
            while (sweep <= 0.0) sweep += Math.PI * 2.0;
        }

        return sweep;
    }

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1e-8;
}
