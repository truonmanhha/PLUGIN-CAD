using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CuocSongQuanhTa.CadTools.Core;

internal sealed class BoundaryGeometryEngine
{
    private const double NumericalTolerance = 1e-6;

    public BoundaryBuildResult Build(
        Transaction transaction,
        IReadOnlyCollection<ObjectId> objectIds,
        Point3d seedPointWcs,
        Matrix3d ucsToWcs,
        BoundarySettings settings)
    {
        var wcsToUcs = ucsToWcs.Inverse();
        var curves = CloneSupportedCurves(transaction, objectIds, wcsToUcs);
        if (curves.Count == 0)
        {
            throw new InvalidOperationException("Khong tim thay LINE, ARC, SPLINE hoac LWPOLYLINE hop le.");
        }

        try
        {
            var pieces = SplitAndExtract(curves, settings.SplineTolerance);
            var graph = BuildGraph(pieces, settings);
            var seedUcs = seedPointWcs.TransformBy(wcsToUcs);
            var loop = FindBestLoop(graph, new Point2d(seedUcs.X, seedUcs.Y));
            if (loop is null)
            {
                throw new InvalidOperationException(
                    "Khong tim duoc vong bien bao quanh diem pick. Hay tang dung sai khe ho hoac chon gon cac duong bien can thiet.");
            }

            return new BoundaryBuildResult
            {
                Loop = loop,
                SourceCurveCount = curves.Count,
                BridgeCount = loop.Edges.Count(edge => edge.Edge.Geometry.IsBridge)
            };
        }
        finally
        {
            foreach (var curve in curves)
            {
                curve.Dispose();
            }
        }
    }

    public GapDetectionResult DetectGaps(
        Transaction transaction,
        IReadOnlyCollection<ObjectId> objectIds,
        Matrix3d ucsToWcs,
        BoundarySettings settings)
    {
        var wcsToUcs = ucsToWcs.Inverse();
        var curves = CloneSupportedCurves(transaction, objectIds, wcsToUcs);
        if (curves.Count == 0)
        {
            throw new InvalidOperationException("Khong tim thay LINE, ARC, SPLINE hoac LWPOLYLINE hop le.");
        }

        try
        {
            var pieces = SplitAndExtract(curves, settings.SplineTolerance);
            var mergeTolerance = Math.Max(
                NumericalTolerance,
                Math.Min(settings.GapTolerance * 1e-4, 1e-3));
            var graph = new GraphData(mergeTolerance);
            foreach (var piece in pieces)
            {
                graph.AddEdge(piece);
            }

            var dangling = graph.Nodes
                .Where(node => node.Edges.Count == 1)
                .ToList();
            var remaining = new List<GraphNode>(dangling);
            var gaps = new List<DetectedGap>();
            var maximumAngle = settings.MaximumGapAngleDegrees * Math.PI / 180.0;

            while (remaining.Count >= 2)
            {
                var pair = FindBestGapPair(remaining, maximumAngle, requireDirectionMatch: true)
                    ?? FindBestGapPair(remaining, maximumAngle, requireDirectionMatch: false);
                if (pair is null)
                {
                    break;
                }

                gaps.Add(new DetectedGap(pair.Value.Start.Position, pair.Value.End.Position));
                remaining.Remove(pair.Value.Start);
                remaining.Remove(pair.Value.End);
            }

            return new GapDetectionResult
            {
                Gaps = gaps.OrderByDescending(gap => gap.Length).ToArray(),
                DanglingEndpointCount = dangling.Count,
                UnmatchedEndpointCount = remaining.Count
            };
        }
        finally
        {
            foreach (var curve in curves)
            {
                curve.Dispose();
            }
        }
    }

    private static List<Curve> CloneSupportedCurves(
        Transaction transaction,
        IEnumerable<ObjectId> objectIds,
        Matrix3d wcsToUcs)
    {
        var curves = new List<Curve>();
        foreach (var objectId in objectIds)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Curve source ||
                source is not (Line or Arc or Spline or Polyline))
            {
                continue;
            }

            var clone = (Curve)source.Clone();
            clone.TransformBy(wcsToUcs);
            curves.Add(clone);
        }

        return curves;
    }

    private static List<SegmentGeometry> SplitAndExtract(IReadOnlyList<Curve> curves, double splineTolerance)
    {
        var splitPoints = Enumerable.Range(0, curves.Count)
            .Select(_ => new List<Point3d>())
            .ToArray();

        if (curves.Count <= 400)
        {
            for (var first = 0; first < curves.Count; first++)
            {
                for (var second = first + 1; second < curves.Count; second++)
                {
                    var intersections = new Point3dCollection();
                    try
                    {
                        curves[first].IntersectWith(
                            curves[second],
                            Intersect.OnBothOperands,
                            intersections,
                            IntPtr.Zero,
                            IntPtr.Zero);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    foreach (Point3d point in intersections)
                    {
                        AddSplitPointIfInternal(curves[first], point, splitPoints[first]);
                        AddSplitPointIfInternal(curves[second], point, splitPoints[second]);
                    }
                }
            }
        }

        var pieces = new List<SegmentGeometry>();
        for (var index = 0; index < curves.Count; index++)
        {
            var curve = curves[index];
            var points = Deduplicate(splitPoints[index]);
            if (points.Count == 0)
            {
                ExtractSegments(curve, splineTolerance, pieces);
                continue;
            }

            DBObjectCollection? fragments = null;
            try
            {
                fragments = curve.GetSplitCurves(new Point3dCollection(points.ToArray()));
                foreach (DBObject fragment in fragments)
                {
                    if (fragment is Curve fragmentCurve)
                    {
                        ExtractSegments(fragmentCurve, splineTolerance, pieces);
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                ExtractSegments(curve, splineTolerance, pieces);
            }
            finally
            {
                if (fragments is not null)
                {
                    foreach (DBObject fragment in fragments)
                    {
                        fragment.Dispose();
                    }
                }
            }
        }

        return pieces.Where(piece => piece.Start.GetDistanceTo(piece.End) > NumericalTolerance).ToList();
    }

    private static void AddSplitPointIfInternal(Curve curve, Point3d point, ICollection<Point3d> points)
    {
        try
        {
            if (point.DistanceTo(curve.StartPoint) > NumericalTolerance &&
                point.DistanceTo(curve.EndPoint) > NumericalTolerance)
            {
                points.Add(point);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Ignore invalid intersection points returned by degenerate curves.
        }
    }

    private static List<Point3d> Deduplicate(IEnumerable<Point3d> points)
    {
        var result = new List<Point3d>();
        foreach (var point in points)
        {
            if (result.All(existing => existing.DistanceTo(point) > NumericalTolerance))
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static void ExtractSegments(Curve curve, double splineTolerance, ICollection<SegmentGeometry> output)
    {
        switch (curve)
        {
            case Line line:
                output.Add(new SegmentGeometry(ToPoint2d(line.StartPoint), ToPoint2d(line.EndPoint), 0.0, false));
                break;

            case Arc arc:
                var sign = arc.Normal.Z < 0.0 ? -1.0 : 1.0;
                output.Add(new SegmentGeometry(
                    ToPoint2d(arc.StartPoint),
                    ToPoint2d(arc.EndPoint),
                    Math.Tan(arc.TotalAngle * 0.25) * sign,
                    false));
                break;

            case Polyline polyline:
                ExtractPolyline(polyline, output);
                break;

            default:
                ExtractSampledCurve(curve, Math.Max(splineTolerance, NumericalTolerance), output);
                break;
        }
    }

    private static void ExtractPolyline(Polyline polyline, ICollection<SegmentGeometry> output)
    {
        if (polyline.NumberOfVertices < 2)
        {
            return;
        }

        var segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var next = (index + 1) % polyline.NumberOfVertices;
            output.Add(new SegmentGeometry(
                polyline.GetPoint2dAt(index),
                polyline.GetPoint2dAt(next),
                polyline.GetBulgeAt(index),
                false));
        }
    }

    private static void ExtractSampledCurve(Curve curve, double tolerance, ICollection<SegmentGeometry> output)
    {
        const int initialSections = 8;
        var points = new List<Point3d>();
        var startParameter = curve.StartParam;
        var endParameter = curve.EndParam;

        for (var section = 0; section < initialSections; section++)
        {
            var firstParameter = startParameter + (endParameter - startParameter) * section / initialSections;
            var secondParameter = startParameter + (endParameter - startParameter) * (section + 1) / initialSections;
            var firstPoint = curve.GetPointAtParameter(firstParameter);
            var secondPoint = curve.GetPointAtParameter(secondParameter);
            if (points.Count == 0)
            {
                points.Add(firstPoint);
            }

            SubdivideCurve(curve, firstParameter, firstPoint, secondParameter, secondPoint, tolerance, 0, points);
        }

        for (var index = 0; index < points.Count - 1; index++)
        {
            output.Add(new SegmentGeometry(ToPoint2d(points[index]), ToPoint2d(points[index + 1]), 0.0, false));
        }
    }

    private static void SubdivideCurve(
        Curve curve,
        double firstParameter,
        Point3d firstPoint,
        double secondParameter,
        Point3d secondPoint,
        double tolerance,
        int depth,
        ICollection<Point3d> output)
    {
        var middleParameter = (firstParameter + secondParameter) * 0.5;
        var middlePoint = curve.GetPointAtParameter(middleParameter);
        var deviation = DistanceToChord(middlePoint, firstPoint, secondPoint);

        if (deviation > tolerance && depth < 12)
        {
            SubdivideCurve(curve, firstParameter, firstPoint, middleParameter, middlePoint, tolerance, depth + 1, output);
            SubdivideCurve(curve, middleParameter, middlePoint, secondParameter, secondPoint, tolerance, depth + 1, output);
            return;
        }

        output.Add(secondPoint);
    }

    private static double DistanceToChord(Point3d point, Point3d start, Point3d end)
    {
        var chord = end - start;
        if (chord.Length < NumericalTolerance)
        {
            return point.DistanceTo(start);
        }

        var projection = Math.Clamp((point - start).DotProduct(chord) / chord.DotProduct(chord), 0.0, 1.0);
        return point.DistanceTo(start + chord * projection);
    }

    private static Point2d ToPoint2d(Point3d point) => new(point.X, point.Y);

    private static GraphData BuildGraph(IReadOnlyCollection<SegmentGeometry> pieces, BoundarySettings settings)
    {
        var data = new GraphData(Math.Max(NumericalTolerance, Math.Min(settings.GapTolerance * 1e-4, 1e-3)));
        foreach (var piece in pieces)
        {
            data.AddEdge(piece);
        }

        var dangling = data.Nodes.Where(node => node.Edges.Count == 1).ToArray();
        var maximumAngle = settings.MaximumGapAngleDegrees * Math.PI / 180.0;
        for (var first = 0; first < dangling.Length; first++)
        {
            for (var second = first + 1; second < dangling.Length; second++)
            {
                var start = dangling[first];
                var end = dangling[second];
                var distance = start.Position.GetDistanceTo(end.Position);
                if (distance <= NumericalTolerance || distance > settings.GapTolerance)
                {
                    continue;
                }

                if (!GapDirectionIsAllowed(start, end, maximumAngle))
                {
                    continue;
                }

                data.AddEdge(new SegmentGeometry(start.Position, end.Position, 0.0, true), start, end);
            }
        }

        data.CreateDirectedEdges();
        return data;
    }

    private static bool GapDirectionIsAllowed(GraphNode start, GraphNode end, double maximumAngle)
    {
        var bridge = end.Position - start.Position;
        if (bridge.Length < NumericalTolerance)
        {
            return false;
        }

        var startOutward = DirectionAwayFromNode(start);
        var endOutward = DirectionAwayFromNode(end);
        var startDeviation = (-startOutward).GetAngleTo(bridge);
        var endDeviation = (-endOutward).GetAngleTo(-bridge);
        return startDeviation <= maximumAngle && endDeviation <= maximumAngle;
    }

    private static (GraphNode Start, GraphNode End)? FindBestGapPair(
        IReadOnlyList<GraphNode> nodes,
        double maximumAngle,
        bool requireDirectionMatch)
    {
        GraphNode? bestStart = null;
        GraphNode? bestEnd = null;
        var bestDistance = double.MaxValue;

        for (var first = 0; first < nodes.Count; first++)
        {
            for (var second = first + 1; second < nodes.Count; second++)
            {
                var start = nodes[first];
                var end = nodes[second];
                var distance = start.Position.GetDistanceTo(end.Position);
                if (distance <= NumericalTolerance || distance >= bestDistance)
                {
                    continue;
                }

                if (requireDirectionMatch && !GapDirectionIsAllowed(start, end, maximumAngle))
                {
                    continue;
                }

                bestStart = start;
                bestEnd = end;
                bestDistance = distance;
            }
        }

        return bestStart is null || bestEnd is null ? null : (bestStart, bestEnd);
    }

    private static Vector2d DirectionAwayFromNode(GraphNode node)
    {
        var edge = node.Edges[0];
        var forward = ReferenceEquals(edge.Start, node);
        var points = edge.Geometry.SamplePoints(forward);
        for (var index = 1; index < points.Count; index++)
        {
            var vector = points[index] - points[0];
            if (vector.Length > NumericalTolerance)
            {
                return vector.GetNormal();
            }
        }

        return Vector2d.XAxis;
    }

    private static BoundaryLoop? FindBestLoop(GraphData graph, Point2d seed)
    {
        var loops = new List<BoundaryLoop>();
        foreach (var start in graph.DirectedEdges)
        {
            if (start.Visited)
            {
                continue;
            }

            var loopEdges = new List<DirectedGraphEdge>();
            var current = start;
            var safety = 0;
            while (!current.Visited && safety++ <= graph.DirectedEdges.Count + 1)
            {
                current.Visited = true;
                loopEdges.Add(current);
                if (current.Next is null)
                {
                    break;
                }

                current = current.Next;
            }

            if (!ReferenceEquals(current, start) || loopEdges.Count < 2)
            {
                continue;
            }

            var polygon = BuildPolygon(loopEdges);
            var signedArea = SignedArea(polygon);
            if (signedArea <= NumericalTolerance || !ContainsPoint(polygon, seed))
            {
                continue;
            }

            loops.Add(new BoundaryLoop
            {
                Edges = loopEdges,
                Polygon = polygon,
                SignedArea = signedArea
            });
        }

        return loops
            .OrderBy(loop => loop.Area)
            .ThenBy(loop => loop.BridgeLength)
            .FirstOrDefault();
    }

    private static IReadOnlyList<Point2d> BuildPolygon(IEnumerable<DirectedGraphEdge> edges)
    {
        var polygon = new List<Point2d>();
        foreach (var edge in edges)
        {
            var samples = edge.Edge.Geometry.SamplePoints(edge.Forward);
            for (var index = 0; index < samples.Count - 1; index++)
            {
                if (polygon.Count == 0 || polygon[^1].GetDistanceTo(samples[index]) > NumericalTolerance)
                {
                    polygon.Add(samples[index]);
                }
            }
        }

        return polygon;
    }

    private static double SignedArea(IReadOnlyList<Point2d> polygon)
    {
        var twiceArea = 0.0;
        for (var index = 0; index < polygon.Count; index++)
        {
            var next = (index + 1) % polygon.Count;
            twiceArea += polygon[index].X * polygon[next].Y - polygon[next].X * polygon[index].Y;
        }

        return twiceArea * 0.5;
    }

    private static bool ContainsPoint(IReadOnlyList<Point2d> polygon, Point2d point)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            var intersects = (a.Y > point.Y) != (b.Y > point.Y) &&
                             point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private sealed class GraphData(double mergeTolerance)
    {
        private readonly double _mergeTolerance = mergeTolerance;

        public List<GraphNode> Nodes { get; } = [];

        public List<GraphEdge> Edges { get; } = [];

        public List<DirectedGraphEdge> DirectedEdges { get; } = [];

        public void AddEdge(SegmentGeometry geometry, GraphNode? fixedStart = null, GraphNode? fixedEnd = null)
        {
            var start = fixedStart ?? FindOrCreateNode(geometry.Start);
            var end = fixedEnd ?? FindOrCreateNode(geometry.End);
            if (ReferenceEquals(start, end))
            {
                return;
            }

            var edge = new GraphEdge(Edges.Count, start, end, geometry);
            Edges.Add(edge);
            start.Edges.Add(edge);
            end.Edges.Add(edge);
        }

        public void CreateDirectedEdges()
        {
            foreach (var edge in Edges)
            {
                var forward = new DirectedGraphEdge(edge, true);
                var reverse = new DirectedGraphEdge(edge, false);
                forward.Twin = reverse;
                reverse.Twin = forward;
                DirectedEdges.Add(forward);
                DirectedEdges.Add(reverse);
            }

            foreach (var node in Nodes)
            {
                var outgoing = DirectedEdges
                    .Where(edge => ReferenceEquals(edge.From, node))
                    .OrderBy(edge => Math.Atan2(edge.DirectionAtStart().Y, edge.DirectionAtStart().X))
                    .ToList();

                for (var index = 0; index < outgoing.Count; index++)
                {
                    var incomingTwin = outgoing[index];
                    if (incomingTwin.Twin is null)
                    {
                        continue;
                    }

                    var previous = (index - 1 + outgoing.Count) % outgoing.Count;
                    incomingTwin.Twin.Next = outgoing[previous];
                }
            }
        }

        private GraphNode FindOrCreateNode(Point2d point)
        {
            var existing = Nodes.FirstOrDefault(node => node.Position.GetDistanceTo(point) <= _mergeTolerance);
            if (existing is not null)
            {
                return existing;
            }

            var node = new GraphNode(Nodes.Count, point);
            Nodes.Add(node);
            return node;
        }
    }
}
