using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CuocSongQuanhTa.CadTools.Core;

internal static class SplinePointReducer
{
    private const double RelativeTolerance = 1e-10;

    public static int CountEditablePoints(Spline spline) =>
        spline.HasFitData ? spline.NumFitPoints : spline.NumControlPoints;

    public static int ReduceExact(Spline spline)
    {
        var removedCount = 0;
        var safety = Math.Max(CountEditablePoints(spline) * 3, 12);

        while (safety-- > 0)
        {
            var count = CountEditablePoints(spline);
            if (count <= 2)
            {
                break;
            }

            var removedInPass = false;
            var firstIndex = spline.Closed ? 0 : 1;
            var lastIndex = spline.Closed ? count - 1 : count - 2;
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                using var candidate = (Spline)spline.Clone();
                if (!TryRemovePoint(candidate, index) ||
                    CountEditablePoints(candidate) >= count ||
                    !GeometryMatches(spline, candidate))
                {
                    continue;
                }

                spline.CopyFrom(candidate);
                removedCount += count - CountEditablePoints(spline);
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

    public static bool GeometryMatches(Spline original, Spline reduced)
    {
        var originalLength = original.GetDistanceAtParameter(original.EndParam);
        var reducedLength = reduced.GetDistanceAtParameter(reduced.EndParam);
        if (original.Closed != reduced.Closed || !NearlyEqual(originalLength, reducedLength))
        {
            return false;
        }

        var geometryScale = Math.Max(1.0, Math.Max(originalLength, reducedLength));
        var positionTolerance = RelativeTolerance * geometryScale;
        var sampleCount = Math.Clamp(
            Math.Max(CountEditablePoints(original), CountEditablePoints(reduced)) * 24,
            192,
            2048);

        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var ratio = sample / (double)sampleCount;
            var originalParameter = original.StartParam +
                                    (original.EndParam - original.StartParam) * ratio;
            var reducedParameter = reduced.StartParam +
                                   (reduced.EndParam - reduced.StartParam) * ratio;
            var originalPoint = original.GetPointAtParameter(originalParameter);
            var reducedPoint = reduced.GetPointAtParameter(reducedParameter);
            if (originalPoint.DistanceTo(reducedPoint) > positionTolerance ||
                !DirectionsMatch(
                    original.GetFirstDerivative(originalParameter),
                    reduced.GetFirstDerivative(reducedParameter)))
            {
                return false;
            }
        }

        return true;
    }

    public static int ReduceWithinTolerance(Spline spline, double tolerance)
    {
        var before = CountEditablePoints(spline);
        var minimumControlPoints = Math.Max(spline.Degree + 1, 2);
        if (tolerance <= 0.0 || before <= minimumControlPoints)
        {
            return 0;
        }

        Spline? best = null;
        var low = minimumControlPoints;
        var high = before - 1;
        try
        {
            while (low <= high)
            {
                var targetCount = low + (high - low) / 2;
                using var candidate = (Spline)spline.Clone();
                ErrorStatus status;
                try
                {
                    status = candidate.Rebuild(spline.Degree, targetCount);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    status = ErrorStatus.InvalidInput;
                }

                if (status == ErrorStatus.OK && GeometryWithinTolerance(spline, candidate, tolerance))
                {
                    best?.Dispose();
                    best = (Spline)candidate.Clone();
                    high = targetCount - 1;
                }
                else
                {
                    low = targetCount + 1;
                }
            }

            using var fitted = FindFittedCandidate(spline, tolerance, minimumControlPoints, before - 1);
            if (fitted is not null &&
                (best is null || CountEditablePoints(fitted) < CountEditablePoints(best)))
            {
                best?.Dispose();
                best = (Spline)fitted.Clone();
            }

            if (best is null)
            {
                return 0;
            }

            spline.CopyFrom(best);
            return before - CountEditablePoints(spline);
        }
        finally
        {
            best?.Dispose();
        }
    }

    private static Spline? FindFittedCandidate(
        Spline original,
        double tolerance,
        int minimumPointCount,
        int maximumPointCount)
    {
        Spline? best = null;
        var low = minimumPointCount;
        var high = maximumPointCount;
        while (low <= high)
        {
            var targetCount = low + (high - low) / 2;
            using var candidate = CreateFittedCandidate(original, targetCount, tolerance);
            if (candidate is not null && GeometryWithinTolerance(original, candidate, tolerance))
            {
                best?.Dispose();
                best = (Spline)candidate.Clone();
                high = targetCount - 1;
            }
            else
            {
                low = targetCount + 1;
            }
        }

        return best;
    }

    private static Spline? CreateFittedCandidate(Spline original, int pointCount, double tolerance)
    {
        try
        {
            var length = original.GetDistanceAtParameter(original.EndParam);
            if (length <= 1e-14 || pointCount < 2)
            {
                return null;
            }

            var points = new Point3dCollection();
            if (original.Closed)
            {
                for (var index = 0; index < pointCount; index++)
                {
                    points.Add(original.GetPointAtDist(length * index / pointCount));
                }
            }
            else
            {
                for (var index = 0; index < pointCount; index++)
                {
                    points.Add(original.GetPointAtDist(length * index / (pointCount - 1.0)));
                }
            }

            var fitTolerance = Math.Max(tolerance * 0.02, 1e-10);
            return original.Closed
                ? new Spline(
                    points,
                    true,
                    KnotParameterizationEnum.Chord,
                    original.Degree,
                    fitTolerance)
                : new Spline(
                    points,
                    KnotParameterizationEnum.Chord,
                    original.Degree,
                    fitTolerance);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    public static bool GeometryWithinTolerance(Spline original, Spline reduced, double tolerance)
    {
        if (original.Closed != reduced.Closed || tolerance <= 0.0)
        {
            return false;
        }

        var allowed = tolerance * 1.000001 + 1e-12;
        if (!original.Closed &&
            (original.StartPoint.DistanceTo(reduced.StartPoint) > allowed ||
             original.EndPoint.DistanceTo(reduced.EndPoint) > allowed))
        {
            return false;
        }

        return MaximumSampledDeviation(original, reduced) <= allowed &&
               MaximumSampledDeviation(reduced, original) <= allowed;
    }

    private static bool TryRemovePoint(Spline spline, int index)
    {
        try
        {
            if (spline.HasFitData)
            {
                spline.RemoveFitPointAt(index);
                return true;
            }

            return spline.RemoveControlPointAt(index) == ErrorStatus.OK;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    private static double MaximumSampledDeviation(Spline source, Spline target)
    {
        var length = source.GetDistanceAtParameter(source.EndParam);
        if (length <= 1e-14)
        {
            return source.StartPoint.DistanceTo(target.GetClosestPointTo(source.StartPoint, false));
        }

        var sampleCount = Math.Clamp(CountEditablePoints(source) * 4, 1024, 16384);
        var maximum = 0.0;
        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var point = source.GetPointAtDist(length * sample / sampleCount);
            maximum = Math.Max(maximum, point.DistanceTo(target.GetClosestPointTo(point, false)));
        }

        return maximum;
    }

    private static bool DirectionsMatch(Vector3d first, Vector3d second)
    {
        if (first.Length <= 1e-14 || second.Length <= 1e-14)
        {
            return first.Length <= 1e-14 && second.Length <= 1e-14;
        }

        var normalizedDot = first.DotProduct(second) / (first.Length * second.Length);
        return normalizedDot >= 1.0 - RelativeTolerance;
    }

    private static bool NearlyEqual(double first, double second)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(second)));
        return Math.Abs(first - second) <= RelativeTolerance * scale;
    }
}
