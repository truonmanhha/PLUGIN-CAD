using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CuocSongQuanhTa.CadTools.Core;

internal sealed record SegmentGeometry(Point2d Start, Point2d End, double Bulge, bool IsBridge)
{
    public SegmentGeometry Reversed() => new(End, Start, -Bulge, IsBridge);

    public IReadOnlyList<Point2d> SamplePoints(bool forward, int minimumArcSegments = 8)
    {
        var segment = forward ? this : Reversed();
        if (Math.Abs(segment.Bulge) < 1e-12)
        {
            return [segment.Start, segment.End];
        }

        var chordX = segment.End.X - segment.Start.X;
        var chordY = segment.End.Y - segment.Start.Y;
        var chord = Math.Sqrt(chordX * chordX + chordY * chordY);
        if (chord < 1e-12)
        {
            return [segment.Start, segment.End];
        }

        var includedAngle = 4.0 * Math.Atan(segment.Bulge);
        var radius = chord * (1.0 + segment.Bulge * segment.Bulge) / (4.0 * Math.Abs(segment.Bulge));
        var midpoint = new Point2d((segment.Start.X + segment.End.X) * 0.5, (segment.Start.Y + segment.End.Y) * 0.5);
        var leftX = -chordY / chord;
        var leftY = chordX / chord;
        var centerOffset = chord * (1.0 - segment.Bulge * segment.Bulge) / (4.0 * segment.Bulge);
        var center = new Point2d(midpoint.X + leftX * centerOffset, midpoint.Y + leftY * centerOffset);
        var startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var count = Math.Max(minimumArcSegments, (int)Math.Ceiling(Math.Abs(includedAngle) / (Math.PI / 18.0)));
        var points = new List<Point2d>(count + 1);

        for (var index = 0; index <= count; index++)
        {
            var angle = startAngle + includedAngle * index / count;
            points.Add(new Point2d(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }

        return points;
    }
}

internal sealed class BoundaryLoop
{
    public required IReadOnlyList<DirectedGraphEdge> Edges { get; init; }

    public required IReadOnlyList<Point2d> Polygon { get; init; }

    public required double SignedArea { get; init; }

    public double Area => Math.Abs(SignedArea);

    public double BridgeLength => Edges
        .Where(edge => edge.Edge.Geometry.IsBridge)
        .Sum(edge => edge.Edge.Geometry.Start.GetDistanceTo(edge.Edge.Geometry.End));

    public Polyline CreatePolyline(Matrix3d ucsToWcs)
    {
        var polyline = new Polyline(Edges.Count);
        for (var index = 0; index < Edges.Count; index++)
        {
            var segment = Edges[index].Forward
                ? Edges[index].Edge.Geometry
                : Edges[index].Edge.Geometry.Reversed();
            polyline.AddVertexAt(index, segment.Start, segment.Bulge, 0.0, 0.0);
        }

        polyline.Closed = true;
        polyline.TransformBy(ucsToWcs);
        return polyline;
    }

    public Spline CreateSpline(Matrix3d ucsToWcs, double fitTolerance)
    {
        var fitPoints = new Point3dCollection();
        foreach (var directedEdge in Edges)
        {
            var segment = directedEdge.Forward
                ? directedEdge.Edge.Geometry
                : directedEdge.Edge.Geometry.Reversed();

            if (Math.Abs(segment.Bulge) >= 1e-12)
            {
                var arcPoints = segment.SamplePoints(true, 12);
                for (var index = 0; index < arcPoints.Count - 1; index++)
                {
                    fitPoints.Add(new Point3d(arcPoints[index].X, arcPoints[index].Y, 0.0));
                }

                continue;
            }

            var length = segment.Start.GetDistanceTo(segment.End);
            var step = Math.Max(fitTolerance * 10.0, length / 12.0);
            var sectionCount = Math.Clamp((int)Math.Ceiling(length / Math.Max(step, 1e-6)), 1, 32);
            for (var section = 0; section < sectionCount; section++)
            {
                var ratio = (double)section / sectionCount;
                fitPoints.Add(new Point3d(
                    segment.Start.X + (segment.End.X - segment.Start.X) * ratio,
                    segment.Start.Y + (segment.End.Y - segment.Start.Y) * ratio,
                    0.0));
            }
        }

        if (fitPoints.Count < 4)
        {
            throw new InvalidOperationException("Can it nhat 4 diem de tao SPLINE kin.");
        }

        fitPoints.Add(fitPoints[0]);
        var spline = new Spline(fitPoints, 3, Math.Max(fitTolerance, 1e-6));
        spline.TransformBy(ucsToWcs);
        return spline;
    }
}

internal sealed class BoundaryBuildResult
{
    public required BoundaryLoop Loop { get; init; }

    public required int SourceCurveCount { get; init; }

    public required int BridgeCount { get; init; }
}

internal sealed record DetectedGap(Point2d Start, Point2d End)
{
    public double Length => Start.GetDistanceTo(End);
}

internal sealed class GapDetectionResult
{
    public required IReadOnlyList<DetectedGap> Gaps { get; init; }

    public required int DanglingEndpointCount { get; init; }

    public required int UnmatchedEndpointCount { get; init; }
}

internal sealed class GraphNode(int id, Point2d position)
{
    public int Id { get; } = id;

    public Point2d Position { get; } = position;

    public List<GraphEdge> Edges { get; } = [];
}

internal sealed class GraphEdge(int id, GraphNode start, GraphNode end, SegmentGeometry geometry)
{
    public int Id { get; } = id;

    public GraphNode Start { get; } = start;

    public GraphNode End { get; } = end;

    public SegmentGeometry Geometry { get; } = geometry;
}

internal sealed class DirectedGraphEdge(GraphEdge edge, bool forward)
{
    public GraphEdge Edge { get; } = edge;

    public bool Forward { get; } = forward;

    public GraphNode From => Forward ? Edge.Start : Edge.End;

    public GraphNode To => Forward ? Edge.End : Edge.Start;

    public DirectedGraphEdge? Twin { get; set; }

    public DirectedGraphEdge? Next { get; set; }

    public bool Visited { get; set; }

    public Vector2d DirectionAtStart()
    {
        var points = Edge.Geometry.SamplePoints(Forward);
        for (var index = 1; index < points.Count; index++)
        {
            var vector = points[index] - points[0];
            if (vector.Length > 1e-12)
            {
                return vector.GetNormal();
            }
        }

        return Vector2d.XAxis;
    }
}
