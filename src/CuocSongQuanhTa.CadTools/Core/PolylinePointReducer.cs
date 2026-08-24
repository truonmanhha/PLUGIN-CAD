using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CuocSongQuanhTa.CadTools.Core;

internal static class PolylinePointReducer
{
    private const double RelativeTolerance = 1e-11;
    private const double AngularTolerance = 1e-11;

    public static int ReduceExact(Polyline polyline)
    {
        var removedCount = 0;
        var safety = Math.Max(polyline.NumberOfVertices * 4, 16);

        while (polyline.NumberOfVertices >= (polyline.Closed ? 4 : 3) && safety-- > 0)
        {
            var removedInPass = false;
            var firstCandidate = polyline.Closed ? 0 : 1;
            var lastCandidate = polyline.Closed
                ? polyline.NumberOfVertices - 1
                : polyline.NumberOfVertices - 2;

            for (var index = firstCandidate; index <= lastCandidate; index++)
            {
                if (!TryGetMergedBulge(polyline, index, out var mergedBulge))
                {
                    continue;
                }

                var previous = (index - 1 + polyline.NumberOfVertices) % polyline.NumberOfVertices;
                polyline.SetBulgeAt(previous, mergedBulge);
                polyline.RemoveVertexAt(index);
                removedCount++;
                removedInPass = true;
                break;
            }

            if (!removedInPass)
            {
                break;
            }
        }

        return removedCount;
    }

    public static bool GeometryMeasuresMatch(Polyline original, Polyline reduced)
    {
        if (!NearlyEqual(original.Length, reduced.Length))
        {
            return false;
        }

        if (!original.Closed || !reduced.Closed)
        {
            return true;
        }

        try
        {
            return NearlyEqual(original.Area, reduced.Area);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    public static int ReduceWithinTolerance(Polyline polyline, double tolerance)
    {
        if (tolerance <= 0.0 || polyline.NumberOfVertices < (polyline.Closed ? 4 : 3))
        {
            return 0;
        }

        if (HasArcSegments(polyline))
        {
            return ReduceFlattenedCurve(polyline, tolerance);
        }

        var count = polyline.NumberOfVertices;
        var protectedIndices = FindProtectedIndices(polyline);
        var retained = new HashSet<int>(protectedIndices);
        var orderedProtected = protectedIndices.OrderBy(index => index).ToArray();

        if (polyline.Closed)
        {
            for (var anchor = 0; anchor < orderedProtected.Length; anchor++)
            {
                var start = orderedProtected[anchor];
                var end = orderedProtected[(anchor + 1) % orderedProtected.Length];
                RetainRunByDeviation(polyline, BuildCyclicRun(start, end, count), tolerance, retained);
            }
        }
        else
        {
            for (var anchor = 0; anchor < orderedProtected.Length - 1; anchor++)
            {
                var start = orderedProtected[anchor];
                var end = orderedProtected[anchor + 1];
                RetainRunByDeviation(polyline, Enumerable.Range(start, end - start + 1).ToArray(),
                    tolerance, retained);
            }
        }

        var minimumCount = polyline.Closed ? 3 : 2;
        if (retained.Count < minimumCount)
        {
            return 0;
        }

        var removed = 0;
        for (var index = count - 1; index >= 0; index--)
        {
            if (retained.Contains(index))
            {
                continue;
            }

            polyline.RemoveVertexAt(index);
            removed++;
        }

        return removed;
    }

    public static bool GeometryWithinTolerance(Polyline original, Polyline reduced, double tolerance)
    {
        if (original.Closed != reduced.Closed || tolerance <= 0.0)
        {
            return false;
        }

        var allowed = tolerance * 1.000001 + 1e-12;
        return MaximumSampledDeviation(original, reduced) <= allowed &&
               MaximumSampledDeviation(reduced, original) <= allowed;
    }

    private static IReadOnlyCollection<int> FindProtectedIndices(Polyline polyline)
    {
        var count = polyline.NumberOfVertices;
        var protectedIndices = new HashSet<int>();
        if (!polyline.Closed)
        {
            protectedIndices.Add(0);
            protectedIndices.Add(count - 1);
        }

        var segmentCount = polyline.Closed ? count : count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            if (polyline.GetBulgeAt(index) == 0.0)
            {
                continue;
            }

            protectedIndices.Add(index);
            protectedIndices.Add((index + 1) % count);
        }

        if (polyline.Closed && protectedIndices.Count < 2)
        {
            var first = 0;
            var second = FarthestVertexFrom(polyline, first);
            var third = FarthestVertexFrom(polyline, second);
            protectedIndices.Add(second);
            protectedIndices.Add(third == second ? first : third);
        }

        return protectedIndices;
    }

    private static bool WidthsAreUniform(Polyline polyline)
    {
        if (Math.Abs(polyline.ConstantWidth) > RelativeTolerance)
        {
            return true;
        }

        var segmentCount = polyline.Closed
            ? polyline.NumberOfVertices
            : polyline.NumberOfVertices - 1;
        if (segmentCount <= 0)
        {
            return true;
        }

        var expectedWidth = polyline.GetStartWidthAt(0);
        for (var segment = 0; segment < segmentCount; segment++)
        {
            if (!NearlyEqual(polyline.GetStartWidthAt(segment), expectedWidth) ||
                !NearlyEqual(polyline.GetEndWidthAt(segment), expectedWidth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasArcSegments(Polyline polyline)
    {
        var segmentCount = polyline.Closed
            ? polyline.NumberOfVertices
            : polyline.NumberOfVertices - 1;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            if (polyline.GetBulgeAt(segment) != 0.0)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReduceFlattenedCurve(Polyline polyline, double tolerance)
    {
        var before = polyline.NumberOfVertices;
        var samplingTolerance = tolerance * 0.2;
        var simplificationTolerance = tolerance * 0.75;
        var samples = SamplePolyline(polyline, samplingTolerance);
        if (samples.Count < (polyline.Closed ? 4 : 3))
        {
            return 0;
        }

        using var candidate = (Polyline)polyline.Clone();
        var uniformWidth = Math.Abs(candidate.ConstantWidth) > RelativeTolerance
            ? candidate.ConstantWidth
            : candidate.GetStartWidthAt(0);
        for (var index = candidate.NumberOfVertices - 1; index >= 0; index--)
        {
            candidate.RemoveVertexAt(index);
        }

        for (var index = 0; index < samples.Count; index++)
        {
            candidate.AddVertexAt(index, samples[index], 0.0, uniformWidth, uniformWidth);
        }

        candidate.Closed = polyline.Closed;
        candidate.ConstantWidth = uniformWidth;
        ReduceWithinTolerance(candidate, simplificationTolerance);
        if (candidate.NumberOfVertices >= before)
        {
            return 0;
        }

        polyline.CopyFrom(candidate);
        return before - polyline.NumberOfVertices;
    }

    private static List<Point2d> SamplePolyline(Polyline polyline, double tolerance)
    {
        var samples = new List<Point2d>(polyline.NumberOfVertices);
        var count = polyline.NumberOfVertices;
        var segmentCount = polyline.Closed ? count : count - 1;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            if (samples.Count == 0)
            {
                samples.Add(polyline.GetPoint2dAt(segment));
            }

            var next = (segment + 1) % count;
            var bulge = polyline.GetBulgeAt(segment);
            if (bulge != 0.0)
            {
                var start = polyline.GetPoint2dAt(segment);
                var end = polyline.GetPoint2dAt(next);
                var chord = start.GetDistanceTo(end);
                var radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
                var sweep = Math.Abs(4.0 * Math.Atan(bulge));
                var safeTolerance = Math.Max(tolerance, radius * 1e-12);
                var cosine = Math.Clamp(1.0 - safeTolerance / Math.Max(radius, 1e-12), -1.0, 1.0);
                var maximumStep = Math.Max(2.0 * Math.Acos(cosine), 1e-6);
                var subdivisions = Math.Clamp((int)Math.Ceiling(sweep / maximumStep), 1, 8192);
                for (var division = 1; division < subdivisions; division++)
                {
                    var parameter = segment + division / (double)subdivisions;
                    var worldPoint = polyline.GetPointAtParameter(parameter);
                    var planePoint = worldPoint.TransformBy(Matrix3d.WorldToPlane(polyline.Normal));
                    samples.Add(new Point2d(planePoint.X, planePoint.Y));
                }
            }

            if (!polyline.Closed || next != 0)
            {
                samples.Add(polyline.GetPoint2dAt(next));
            }
        }

        return samples;
    }

    private static int FarthestVertexFrom(Polyline polyline, int sourceIndex)
    {
        var source = polyline.GetPoint2dAt(sourceIndex);
        var farthestIndex = sourceIndex;
        var farthestDistance = -1.0;
        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            var distance = source.GetDistanceTo(polyline.GetPoint2dAt(index));
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthestIndex = index;
            }
        }

        return farthestIndex;
    }

    private static int[] BuildCyclicRun(int start, int end, int count)
    {
        var indices = new List<int> { start };
        var current = start;
        while (current != end && indices.Count <= count)
        {
            current = (current + 1) % count;
            indices.Add(current);
        }

        return indices.ToArray();
    }

    private static void RetainRunByDeviation(
        Polyline polyline,
        IReadOnlyList<int> run,
        double tolerance,
        ISet<int> retained)
    {
        if (run.Count <= 2)
        {
            foreach (var index in run)
            {
                retained.Add(index);
            }

            return;
        }

        retained.Add(run[0]);
        retained.Add(run[^1]);
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, run.Count - 1));
        while (ranges.Count > 0)
        {
            var range = ranges.Pop();
            if (range.End <= range.Start + 1)
            {
                continue;
            }

            var start = polyline.GetPoint2dAt(run[range.Start]);
            var end = polyline.GetPoint2dAt(run[range.End]);
            var maximumDistance = -1.0;
            var maximumIndex = -1;
            for (var index = range.Start + 1; index < range.End; index++)
            {
                var distance = DistanceToSegment(polyline.GetPoint2dAt(run[index]), start, end);
                if (distance > maximumDistance)
                {
                    maximumDistance = distance;
                    maximumIndex = index;
                }
            }

            if (maximumDistance <= tolerance || maximumIndex < 0)
            {
                continue;
            }

            retained.Add(run[maximumIndex]);
            ranges.Push((range.Start, maximumIndex));
            ranges.Push((maximumIndex, range.End));
        }
    }

    private static double DistanceToSegment(Point2d point, Point2d start, Point2d end)
    {
        var segment = end - start;
        var lengthSquared = segment.DotProduct(segment);
        if (lengthSquared <= 1e-24)
        {
            return point.GetDistanceTo(start);
        }

        var ratio = Math.Clamp((point - start).DotProduct(segment) / lengthSquared, 0.0, 1.0);
        return point.GetDistanceTo(start + segment * ratio);
    }

    private static double MaximumSampledDeviation(Curve source, Curve target)
    {
        var length = source.GetDistanceAtParameter(source.EndParam);
        if (length <= 1e-14)
        {
            return source.StartPoint.DistanceTo(target.GetClosestPointTo(source.StartPoint, false));
        }

        var sampleCount = Math.Clamp(
            source is Polyline polyline ? polyline.NumberOfVertices * 2 : 1024,
            512,
            16384);
        var maximum = 0.0;
        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var point = source.GetPointAtDist(length * sample / sampleCount);
            maximum = Math.Max(maximum, point.DistanceTo(target.GetClosestPointTo(point, false)));
        }

        return maximum;
    }

    private static bool TryGetMergedBulge(Polyline polyline, int index, out double mergedBulge)
    {
        mergedBulge = 0.0;
        var count = polyline.NumberOfVertices;
        var previous = (index - 1 + count) % count;
        var next = (index + 1) % count;

        if (HasVariableWidth(polyline, previous) || HasVariableWidth(polyline, index))
        {
            return false;
        }

        var start = polyline.GetPoint2dAt(previous);
        var middle = polyline.GetPoint2dAt(index);
        var end = polyline.GetPoint2dAt(next);
        var firstBulge = polyline.GetBulgeAt(previous);
        var secondBulge = polyline.GetBulgeAt(index);
        // A non-zero bulge is a real arc, even when it is extremely small. Treating it
        // as a line would technically alter the stored geometry.
        var firstIsLine = firstBulge == 0.0;
        var secondIsLine = secondBulge == 0.0;

        if (firstIsLine && secondIsLine)
        {
            return PointsAreExactlyCollinear(start, middle, end);
        }

        if (firstIsLine || secondIsLine || Math.Sign(firstBulge) != Math.Sign(secondBulge))
        {
            return false;
        }

        if (!TryGetArcDefinition(start, middle, firstBulge, out var firstCenter, out var firstRadius) ||
            !TryGetArcDefinition(middle, end, secondBulge, out var secondCenter, out var secondRadius))
        {
            return false;
        }

        var arcScale = Math.Max(1.0, Math.Max(firstRadius, secondRadius));
        if (!PointsNearlyEqual(firstCenter, secondCenter, arcScale) ||
            !NearlyEqual(firstRadius, secondRadius))
        {
            return false;
        }

        var combinedAngle = 4.0 * Math.Atan(firstBulge) + 4.0 * Math.Atan(secondBulge);
        if (Math.Abs(combinedAngle) >= Math.PI * 2.0 - AngularTolerance)
        {
            return false;
        }

        mergedBulge = Math.Tan(combinedAngle * 0.25);
        return double.IsFinite(mergedBulge);
    }

    private static bool HasVariableWidth(Polyline polyline, int segmentIndex)
    {
        if (Math.Abs(polyline.ConstantWidth) > RelativeTolerance)
        {
            return false;
        }

        return Math.Abs(polyline.GetStartWidthAt(segmentIndex)) > RelativeTolerance ||
               Math.Abs(polyline.GetEndWidthAt(segmentIndex)) > RelativeTolerance;
    }

    private static bool PointsAreExactlyCollinear(Point2d start, Point2d middle, Point2d end)
    {
        var first = middle - start;
        var second = end - middle;
        if (PointsExactlyEqual(start, middle) || PointsExactlyEqual(middle, end))
        {
            return true;
        }

        if (first.Length <= RelativeTolerance || second.Length <= RelativeTolerance)
        {
            return false;
        }

        var cross = first.X * second.Y - first.Y * second.X;
        var scale = Math.Max(1.0, first.Length * second.Length);
        var dot = first.DotProduct(second);
        return Math.Abs(cross) <= RelativeTolerance * scale && dot > 0.0;
    }

    private static bool TryGetArcDefinition(
        Point2d start,
        Point2d end,
        double bulge,
        out Point2d center,
        out double radius)
    {
        center = Point2d.Origin;
        radius = 0.0;
        var chordVector = end - start;
        var chord = chordVector.Length;
        if (chord <= RelativeTolerance || bulge == 0.0)
        {
            return false;
        }

        var midpoint = new Point2d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5);
        var left = new Vector2d(-chordVector.Y / chord, chordVector.X / chord);
        var centerOffset = chord * (1.0 - bulge * bulge) / (4.0 * bulge);
        center = midpoint + left * centerOffset;
        radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
        return double.IsFinite(radius);
    }

    private static bool PointsNearlyEqual(Point2d first, Point2d second, double geometryScale)
    {
        return first.GetDistanceTo(second) <= RelativeTolerance * Math.Max(1.0, geometryScale);
    }

    private static bool PointsExactlyEqual(Point2d first, Point2d second) =>
        first.X == second.X && first.Y == second.Y;

    private static bool NearlyEqual(double first, double second)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(second)));
        return Math.Abs(first - second) <= RelativeTolerance * scale;
    }
}
