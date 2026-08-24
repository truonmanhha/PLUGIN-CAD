using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(PointReducerHarness.Commands))]

namespace PointReducerHarness;

public sealed class Commands
{
    private const string ModulePath =
        @"D:\Du An\LISP CAD\LOAD TOOL\Modules\CuocSongQuanhTa.CadTools.Module.dll";

    [CommandMethod("TMHREDUCETEST", CommandFlags.Session)]
    public void Run()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            var module = Assembly.Load(File.ReadAllBytes(ModulePath));
            var reducer = module.GetType(
                "CuocSongQuanhTa.CadTools.Core.PolylinePointReducer",
                throwOnError: true)!;
            var reduce = reducer.GetMethod(
                "ReduceExact",
                BindingFlags.Public | BindingFlags.Static)!;
            var measuresMatch = reducer.GetMethod(
                "GeometryMeasuresMatch",
                BindingFlags.Public | BindingFlags.Static)!;
            var optimizePolyline = reducer.GetMethod(
                "ReduceWithinTolerance",
                BindingFlags.Public | BindingFlags.Static)!;
            var polylineWithinTolerance = reducer.GetMethod(
                "GeometryWithinTolerance",
                BindingFlags.Public | BindingFlags.Static)!;
            var splineReducer = module.GetType(
                "CuocSongQuanhTa.CadTools.Core.SplinePointReducer",
                throwOnError: true)!;
            var reduceSpline = splineReducer.GetMethod(
                "ReduceExact",
                BindingFlags.Public | BindingFlags.Static)!;
            var splineMatches = splineReducer.GetMethod(
                "GeometryMatches",
                BindingFlags.Public | BindingFlags.Static)!;
            var optimizeSpline = splineReducer.GetMethod(
                "ReduceWithinTolerance",
                BindingFlags.Public | BindingFlags.Static)!;
            var splineWithinTolerance = splineReducer.GetMethod(
                "GeometryWithinTolerance",
                BindingFlags.Public | BindingFlags.Static)!;

            TestStraightSegments(reduce, measuresMatch);
            TestRealCorner(reduce, measuresMatch);
            TestCircularArcs(reduce, measuresMatch);
            TestCollinearSpline(reduceSpline, splineMatches);
            TestCurvedSplineIsProtected(reduceSpline, splineMatches);
            TestDenseClosedPolylineOptimization(optimizePolyline, polylineWithinTolerance);
            TestDenseArcPolylineOptimization(optimizePolyline, polylineWithinTolerance);
            TestDenseSplineOptimization(optimizeSpline, splineWithinTolerance);
            editor.WriteMessage("\nTMH_POINT_REDUCER_TEST: PASS");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nTMH_POINT_REDUCER_TEST: FAIL - {exception}");
        }
    }

    [CommandMethod("TMHSPLINEAPI", CommandFlags.Session)]
    public void PrintSplineApi()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        foreach (var constructor in typeof(Spline).GetConstructors())
        {
            var constructorParameters = string.Join(", ", constructor.GetParameters()
                .Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
            editor.WriteMessage($"\nCTOR({constructorParameters})");
        }

        foreach (var method in typeof(Spline).GetMethods()
                     .Where(method => method.Name.Contains("Control", StringComparison.OrdinalIgnoreCase) ||
                                      method.Name.Contains("Fit", StringComparison.OrdinalIgnoreCase) ||
                                      method.Name.Contains("Knot", StringComparison.OrdinalIgnoreCase) ||
                                      method.Name.Contains("Rebuild", StringComparison.OrdinalIgnoreCase) ||
                                      method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(method => method.Name))
        {
            var parameters = string.Join(", ", method.GetParameters()
                .Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
            editor.WriteMessage($"\n{method.ReturnType.Name} {method.Name}({parameters})");
        }
    }

    [CommandMethod("TMHINSPECTDWG", CommandFlags.Session)]
    public void InspectDrawing()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;
        var database = document.Database;
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForRead);
        editor.WriteMessage("\nTMH_DWG_INSPECT_BEGIN");
        foreach (ObjectId id in modelSpace)
        {
            PrintEntity(transaction, editor, id, 0, new HashSet<ObjectId>());
        }

        editor.WriteMessage("\nTMH_DWG_INSPECT_END");
    }

    [CommandMethod("TMHAUTODIMTEST", CommandFlags.Session)]
    public void TestAutoDimension()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var database = document.Database;
        var editor = document.Editor;
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite);
            var polyline = new Polyline(6) { Closed = true };
            polyline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(100, 0), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(125, 25), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(100, 50), 0, 0, 0);
            polyline.AddVertexAt(4, new Point2d(35, 50), 0, 0, 0);
            polyline.AddVertexAt(5, new Point2d(0, 30), 0, 0, 0);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            transaction.Commit();
        }

        var module = Assembly.Load(File.ReadAllBytes(ModulePath));
        var settingsType = module.GetType(
            "CuocSongQuanhTa.CadTools.Core.BoundarySettings",
            throwOnError: true)!;
        var settings = settingsType.GetProperty("Current")!.GetValue(null)!;
        settingsType.GetMethod("RequestAutoDimension")!.Invoke(settings, null);
        var commandsType = module.GetType(
            "CuocSongQuanhTa.CadTools.Commands.BoundaryCommands",
            throwOnError: true)!;
        var commands = Activator.CreateInstance(commandsType)!;
        commandsType.GetMethod("CreateBoundaryPolyline")!.Invoke(commands, null);

        using var check = database.TransactionManager.StartOpenCloseTransaction();
        var currentSpace = (BlockTableRecord)check.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        var dimensionCount = currentSpace.Cast<ObjectId>()
            .Count(id => check.GetObject(id, OpenMode.ForRead) is Dimension);
        editor.WriteMessage(
            dimensionCount >= 4
                ? $"\nTMH_AUTO_DIM_TEST: PASS ({dimensionCount} dimensions)"
                : $"\nTMH_AUTO_DIM_TEST: FAIL ({dimensionCount} dimensions)");
    }

    private static void PrintEntity(
        Transaction transaction,
        Autodesk.AutoCAD.EditorInput.Editor editor,
        ObjectId id,
        int depth,
        ISet<ObjectId> activeDefinitions)
    {
        if (depth > 8 || transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        var extentsText = "no-extents";
        try
        {
            var extents = entity.GeometricExtents;
            extentsText = $"min=({extents.MinPoint.X:0.###},{extents.MinPoint.Y:0.###},{extents.MinPoint.Z:0.###}) " +
                          $"max=({extents.MaxPoint.X:0.###},{extents.MaxPoint.Y:0.###},{extents.MaxPoint.Z:0.###})";
        }
        catch
        {
            // Diagnostic only.
        }

        editor.WriteMessage($"\n{indent}{entity.GetType().Name} dxf={entity.GetRXClass().DxfName} {extentsText}");
        if (entity is not BlockReference blockReference ||
            activeDefinitions.Contains(blockReference.BlockTableRecord))
        {
            return;
        }

        var definition = (BlockTableRecord)transaction.GetObject(
            blockReference.BlockTableRecord,
            OpenMode.ForRead);
        editor.WriteMessage($" block={definition.Name}");
        activeDefinitions.Add(blockReference.BlockTableRecord);
        foreach (ObjectId childId in definition)
        {
            PrintEntity(transaction, editor, childId, depth + 1, activeDefinitions);
        }

        activeDefinitions.Remove(blockReference.BlockTableRecord);
    }

    private static void TestStraightSegments(MethodInfo reduce, MethodInfo measuresMatch)
    {
        using var polyline = NewPolyline(
            (0, 0, 0),
            (25, 0, 0),
            (50, 0, 0),
            (50, 25, 0),
            (50, 50, 0));
        using var original = (Polyline)polyline.Clone();
        var removed = InvokeReduce(reduce, polyline);
        Assert(removed == 2, "straight removed count");
        Assert(polyline.NumberOfVertices == 3, "straight vertex count");
        Assert(InvokeMatch(measuresMatch, original, polyline), "straight geometry");
    }

    private static void TestRealCorner(MethodInfo reduce, MethodInfo measuresMatch)
    {
        using var polyline = NewPolyline(
            (0, 0, 0),
            (25, 0, 0),
            (50, 1, 0));
        using var original = (Polyline)polyline.Clone();
        var removed = InvokeReduce(reduce, polyline);
        Assert(removed == 0, "real corner preserved");
        Assert(polyline.NumberOfVertices == 3, "real corner vertex count");
        Assert(InvokeMatch(measuresMatch, original, polyline), "real corner geometry");
    }

    private static void TestCircularArcs(MethodInfo reduce, MethodInfo measuresMatch)
    {
        var segmentBulge = Math.Tan(Math.PI / 16.0);
        using var polyline = NewPolyline(
            (50, 0, segmentBulge),
            (50 / Math.Sqrt(2), 50 / Math.Sqrt(2), segmentBulge),
            (0, 50, 0));
        using var original = (Polyline)polyline.Clone();
        var removed = InvokeReduce(reduce, polyline);
        Assert(removed == 1, "arc removed count");
        Assert(polyline.NumberOfVertices == 2, "arc vertex count");
        Assert(Math.Abs(polyline.GetBulgeAt(0) - Math.Tan(Math.PI / 8.0)) < 1e-12,
            "arc merged bulge");
        Assert(InvokeMatch(measuresMatch, original, polyline), "arc geometry");
    }

    private static void TestCollinearSpline(MethodInfo reduce, MethodInfo measuresMatch)
    {
        var points = new Point3dCollection
        {
            new(0, 0, 0),
            new(25, 0, 0),
            new(50, 0, 0),
            new(75, 0, 0),
            new(100, 0, 0)
        };
        using var spline = new Spline(
            points,
            KnotParameterizationEnum.Chord,
            3,
            1e-12);
        using var original = (Spline)spline.Clone();
        var removed = InvokeReduce(reduce, spline);
        Assert(removed > 0, "spline removed count");
        Assert(InvokeSplineMatch(measuresMatch, original, spline), "spline geometry");
    }

    private static void TestCurvedSplineIsProtected(MethodInfo reduce, MethodInfo measuresMatch)
    {
        var points = new Point3dCollection
        {
            new(0, 0, 0),
            new(20, 35, 0),
            new(45, -10, 0),
            new(70, 40, 0),
            new(100, 0, 0)
        };
        using var spline = new Spline(
            points,
            KnotParameterizationEnum.Chord,
            3,
            1e-12);
        using var original = (Spline)spline.Clone();
        var removed = InvokeReduce(reduce, spline);
        Assert(removed == 0, "curved spline point preserved");
        Assert(InvokeSplineMatch(measuresMatch, original, spline), "curved spline geometry");
    }

    private static void TestDenseClosedPolylineOptimization(MethodInfo reduce, MethodInfo withinTolerance)
    {
        using var polyline = new Polyline(3600) { Closed = true };
        for (var index = 0; index < 3600; index++)
        {
            var angle = Math.PI * 2.0 * index / 3600.0;
            polyline.AddVertexAt(
                index,
                new Point2d(Math.Cos(angle) * 100.0, Math.Sin(angle) * 100.0),
                0,
                0,
                0);
        }

        using var original = (Polyline)polyline.Clone();
        var removed = InvokeReduceWithTolerance(reduce, polyline, 0.01);
        Assert(removed > 3000, "dense polyline reduced strongly");
        Assert(InvokeWithinTolerance(withinTolerance, original, polyline, 0.01),
            "dense polyline tolerance");
    }

    private static void TestDenseArcPolylineOptimization(MethodInfo reduce, MethodInfo withinTolerance)
    {
        const int vertexCount = 720;
        var segmentBulge = Math.Tan((Math.PI * 2.0 / vertexCount) / 4.0);
        using var polyline = new Polyline(vertexCount) { Closed = true };
        for (var index = 0; index < vertexCount; index++)
        {
            var angle = Math.PI * 2.0 * index / vertexCount;
            polyline.AddVertexAt(
                index,
                new Point2d(Math.Cos(angle) * 100.0, Math.Sin(angle) * 100.0),
                segmentBulge,
                0,
                0);
        }

        using var original = (Polyline)polyline.Clone();
        var removed = InvokeReduceWithTolerance(reduce, polyline, 0.01);
        Assert(removed > 500, "dense arc polyline reduced strongly");
        Assert(InvokeWithinTolerance(withinTolerance, original, polyline, 0.01),
            "dense arc polyline tolerance");
    }

    private static void TestDenseSplineOptimization(MethodInfo reduce, MethodInfo withinTolerance)
    {
        var points = new Point3dCollection();
        for (var index = 0; index <= 200; index++)
        {
            var x = index * 0.5;
            points.Add(new Point3d(x, Math.Sin(x * 0.06) * 15.0, 0));
        }

        using var spline = new Spline(points, KnotParameterizationEnum.Chord, 3, 1e-10);
        using var original = (Spline)spline.Clone();
        var removed = InvokeReduceWithTolerance(reduce, spline, 0.01);
        Assert(removed > 0, "dense spline reduced");
        Assert(InvokeWithinTolerance(withinTolerance, original, spline, 0.01),
            "dense spline tolerance");
    }

    private static Polyline NewPolyline(params (double X, double Y, double Bulge)[] vertices)
    {
        var polyline = new Polyline(vertices.Length);
        for (var index = 0; index < vertices.Length; index++)
        {
            var vertex = vertices[index];
            polyline.AddVertexAt(index, new Point2d(vertex.X, vertex.Y), vertex.Bulge, 0, 0);
        }

        return polyline;
    }

    private static int InvokeReduce(MethodInfo method, Entity entity) =>
        (int)method.Invoke(null, [entity])!;

    private static int InvokeReduceWithTolerance(MethodInfo method, Entity entity, double tolerance) =>
        (int)method.Invoke(null, [entity, tolerance])!;

    private static bool InvokeMatch(MethodInfo method, Polyline original, Polyline reduced) =>
        (bool)method.Invoke(null, [original, reduced])!;

    private static bool InvokeSplineMatch(MethodInfo method, Spline original, Spline reduced) =>
        (bool)method.Invoke(null, [original, reduced])!;

    private static bool InvokeWithinTolerance(
        MethodInfo method,
        Entity original,
        Entity reduced,
        double tolerance) =>
        (bool)method.Invoke(null, [original, reduced, tolerance])!;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
