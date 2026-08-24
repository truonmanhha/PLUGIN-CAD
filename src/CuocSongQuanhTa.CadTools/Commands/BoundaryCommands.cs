using Autodesk.AutoCAD.ApplicationServices;
using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CuocSongQuanhTa.CadTools.Core;
using CuocSongQuanhTa.CadTools.UI;

namespace CuocSongQuanhTa.CadTools.Commands;

public sealed class BoundaryCommands
{
    public void ShowTools()
    {
        PaletteHost.Show();
    }

    public void CreateBoundaryPolyline()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        var editor = document.Editor;
        var database = document.Database;
        var settings = BoundarySettings.Current;
        if (settings.ConsumeGCodeImportRequest())
        {
            ImportGCodeGeometry(database, editor, settings.GCodeFilePath);
            return;
        }

        var alignmentOperation = settings.ConsumeAlignmentRequest();
        if (alignmentOperation != AlignmentOperation.None)
        {
            AlignOrDistributeSelectedObjects(
                database,
                editor,
                alignmentOperation,
                settings.AlignmentSpacing);
            return;
        }

        var dimensionSearchFocusHandle = settings.ConsumeDimensionSearchFocusRequest();
        if (dimensionSearchFocusHandle.HasValue)
        {
            FocusDimensionSearchResult(database, editor, dimensionSearchFocusHandle.Value);
            return;
        }

        var dimensionSearchOnly = settings.ConsumeDimensionSearchRequest();
        if (dimensionSearchOnly)
        {
            SearchObjectsByDimensions(database, editor, settings);
            return;
        }

        var autoDeleteDimensionOnly = settings.ConsumeAutoDeleteDimensionRequest();
        if (autoDeleteDimensionOnly)
        {
            DeleteSelectedDimensions(database, editor);
            return;
        }

        var autoDimensionOnly = settings.ConsumeAutoDimensionRequest();
        if (autoDimensionOnly)
        {
            AutoDimensionSelectedDetails(database, editor);
            return;
        }

        var flatten3dOnly = settings.ConsumeFlatten3dRequest();
        if (flatten3dOnly)
        {
            LaySelectedObjectsFlat(database, editor);
            return;
        }

        var reducePointsOnly = settings.ConsumePointReductionRequest();
        if (reducePointsOnly)
        {
            ReduceSelectedPolylinePoints(database, editor, settings);
            return;
        }

        var scanGapsOnly = settings.ConsumeGapScanRequest();

        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = scanGapsOnly
                ? "\nChon LINE, ARC, SPLINE va PLINE can quet khe ho: "
                : "\nChon LINE, ARC, SPLINE va PLINE tao thanh bien: "
        };
        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "LINE,ARC,SPLINE,LWPOLYLINE")
        ]);
        var selection = editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK)
        {
            return;
        }

        if (scanGapsOnly)
        {
            ScanSelectedGaps(
                database,
                editor,
                selection.Value.GetObjectIds(),
                settings);
            return;
        }

        var seedPrompt = new PromptPointOptions("\nChon mot diem nam ben trong vung can tao duong bao: ");
        var seed = editor.GetPoint(seedPrompt);
        if (seed.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var engine = new BoundaryGeometryEngine();
            var result = engine.Build(
                transaction,
                selection.Value.GetObjectIds(),
                seed.Value,
                editor.CurrentUserCoordinateSystem,
                settings);

            var detectedGaps = result.Loop.Edges
                .Where(edge => edge.Edge.Geometry.IsBridge)
                .Select(edge =>
                {
                    var geometry = edge.Edge.Geometry;
                    var start = new Autodesk.AutoCAD.Geometry.Point3d(
                            geometry.Start.X,
                            geometry.Start.Y,
                            0.0)
                        .TransformBy(editor.CurrentUserCoordinateSystem);
                    var end = new Autodesk.AutoCAD.Geometry.Point3d(
                            geometry.End.X,
                            geometry.End.Y,
                            0.0)
                        .TransformBy(editor.CurrentUserCoordinateSystem);
                    return new GapDetectionSnapshot(start, end);
                })
                .ToArray();
            settings.UpdateGapDetection(detectedGaps);
            var detectedGapLengths = detectedGaps.Select(gap => gap.Length).ToArray();

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace],
                OpenMode.ForWrite);

            var previewEntity = CreateOutputEntity(result, settings, editor.CurrentUserCoordinateSystem);
            previewEntity.Color = Color.FromColorIndex(ColorMethod.ByAci, 4);
            modelSpace.AppendEntity(previewEntity);
            transaction.AddNewlyCreatedDBObject(previewEntity, true);

            var bridgeMarkers = AddBridgeMarkers(result, modelSpace, transaction, editor.CurrentUserCoordinateSystem);
            database.TransactionManager.QueueForGraphicsFlush();
            editor.Regen();

            var gapMessage = detectedGapLengths.Length == 0
                ? "bien dang kin, khong co khe ho."
                : $"tu dong phat hien {detectedGapLengths.Length} khe; " +
                  $"khe lon nhat {detectedGapLengths.Max():0.###}.";
            editor.WriteMessage(
                $"\nDa tim thay bien gom {result.Loop.Edges.Count} doan; {gapMessage}");

            var previewOutputName = settings.OutputCurveType == OutputCurveType.Polyline ? "PLINE" : "SPLINE";
            var confirmOptions = new PromptKeywordOptions($"\nTao {previewOutputName} theo preview?")
            {
                AllowNone = true
            };
            confirmOptions.Keywords.Add("Tao");
            confirmOptions.Keywords.Add("Huy");
            confirmOptions.Keywords.Default = "Tao";
            var confirm = editor.GetKeywords(confirmOptions);

            if (confirm.Status == PromptStatus.OK && confirm.StringResult == "Huy" ||
                confirm.Status == PromptStatus.Cancel)
            {
                transaction.Abort();
                editor.WriteMessage("\nDa huy preview.");
                return;
            }

            foreach (var marker in bridgeMarkers)
            {
                marker.Erase();
            }

            previewEntity.Color = Color.FromColorIndex(ColorMethod.ByAci, settings.OutputColorIndex);
            transaction.Commit();
            var outputName = settings.OutputCurveType == OutputCurveType.Polyline ? "LWPOLYLINE kin" : "SPLINE";
            editor.WriteMessage($"\nDa tao {outputName}. Cac doi tuong goc duoc giu nguyen.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nCQBD: {exception.Message}");
        }
    }

    private static void ImportGCodeGeometry(Database database, Editor editor, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            editor.WriteMessage("\nGCODE: Khong tim thay file NC da chon.");
            return;
        }

        try
        {
            var result = GCodeGeometryParser.ParseFile(filePath);
            if (result.Paths.Count == 0)
            {
                editor.WriteMessage("\nGCODE: File khong co duong cat G01/G02/G03 hop le.");
                return;
            }

            Extents3d? sourceExtents = null;
            foreach (var path in result.Paths.Where(path => path.Segments.Count > 0))
            {
                using var previewPolyline = CreateGCodePolyline(path, database.Clayer);
                var extents = previewPolyline.GeometricExtents;
                if (sourceExtents is null)
                {
                    sourceExtents = extents;
                }
                else
                {
                    var union = sourceExtents.Value;
                    union.AddExtents(extents);
                    sourceExtents = union;
                }
            }

            if (!sourceExtents.HasValue)
            {
                editor.WriteMessage("\nGCODE: Khong tinh duoc kich thuoc hinh.");
                return;
            }

            var insertionPrompt = new PromptPointOptions(
                "\nChon vi tri dat GOC DUOI-TRAI cua hinh GCODE: ");
            var insertionResult = editor.GetPoint(insertionPrompt);
            if (insertionResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nGCODE -> HINH: Da huy chon vi tri dat.");
                return;
            }

            var displacement = Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                new Autodesk.AutoCAD.Geometry.Vector3d(
                    insertionResult.Value.X - sourceExtents.Value.MinPoint.X,
                    insertionResult.Value.Y - sourceExtents.Value.MinPoint.Y,
                    0.0));
            Extents3d? combinedExtents = null;
            var createdCount = 0;
            var closedCount = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite);

                foreach (var path in result.Paths.Where(path => path.Segments.Count > 0))
                {
                    var polyline = CreateGCodePolyline(path, database.Clayer);
                    polyline.TransformBy(displacement);
                    if (polyline.Closed) closedCount++;

                    currentSpace.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    createdCount++;

                    var extents = polyline.GeometricExtents;
                    if (combinedExtents is null)
                    {
                        combinedExtents = extents;
                    }
                    else
                    {
                        var union = combinedExtents.Value;
                        union.AddExtents(extents);
                        combinedExtents = union;
                    }
                }

                transaction.Commit();
            }

            if (combinedExtents.HasValue)
            {
                ZoomToTopExtents(editor, combinedExtents.Value);
            }

            var warning = result.IgnoredArcCount > 0
                ? $"; {result.IgnoredArcCount} cung loi da thay bang doan thang"
                : string.Empty;
            editor.WriteMessage(
                $"\nGCODE -> HINH: Da tao {createdCount} PLINE ({closedCount} kin), " +
                $"gom {result.LineSegmentCount} doan thang va {result.ArcSegmentCount} cung tron{warning}. " +
                $"Goc duoi-trai dat tai X={insertionResult.Value.X:0.###}, " +
                $"Y={insertionResult.Value.Y:0.###}, Z=0. Co the UNDO mot lan de hoan tac.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nGCODE -> HINH: Khong doc duoc file. {exception.Message}");
        }
    }

    private static Polyline CreateGCodePolyline(GCodePath path, ObjectId layerId)
    {
        var isClosed = path.IsClosed;
        var polyline = new Polyline(path.Segments.Count + (isClosed ? 0 : 1))
        {
            LayerId = layerId,
            Elevation = 0.0,
            Normal = Autodesk.AutoCAD.Geometry.Vector3d.ZAxis
        };

        for (var index = 0; index < path.Segments.Count; index++)
        {
            var segment = path.Segments[index];
            polyline.AddVertexAt(index, segment.Start, segment.Bulge, 0.0, 0.0);
        }

        if (isClosed)
        {
            polyline.Closed = true;
        }
        else
        {
            polyline.AddVertexAt(
                path.Segments.Count,
                path.Segments[^1].End,
                0.0,
                0.0,
                0.0);
        }

        return polyline;
    }

    private static void ZoomToTopExtents(Editor editor, Extents3d extents)
    {
        using var view = editor.GetCurrentView();
        var deltaX = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 1e-6);
        var deltaY = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 1e-6);
        var aspect = view.Height > 1e-9 ? view.Width / view.Height : 1.0;
        var requiredHeight = Math.Max(deltaY, deltaX / Math.Max(aspect, 1e-9));
        view.ViewDirection = Autodesk.AutoCAD.Geometry.Vector3d.ZAxis;
        view.ViewTwist = 0.0;
        view.Target = new Autodesk.AutoCAD.Geometry.Point3d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
            0.0);
        view.Height = requiredHeight * 1.25;
        view.Width = view.Height * Math.Max(aspect, 1e-9);
        editor.SetCurrentView(view);
        editor.Regen();
    }

    private static void LaySelectedObjectsFlat(Database database, Editor editor)
    {
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\nChon doi tuong 3D can dat nam bet tren mat phang XY: "
        };
        var selection = editor.GetSelection(selectionOptions);
        if (selection.Status != PromptStatus.OK)
        {
            return;
        }

        var firstResult = editor.GetPoint(
            "\nChon DIEM 1 tren mat can dat xuong (diem neo Z=0): ");
        if (firstResult.Status != PromptStatus.OK)
        {
            return;
        }

        var secondOptions = new PromptPointOptions(
            "\nChon DIEM 2 tren cung mat (huong nay se thanh truc ngang X): ")
        {
            BasePoint = firstResult.Value,
            UseBasePoint = true
        };
        var secondResult = editor.GetPoint(secondOptions);
        if (secondResult.Status != PromptStatus.OK)
        {
            return;
        }

        var thirdOptions = new PromptPointOptions(
            "\nChon DIEM 3 bat ky tren cung mat de xac dinh mat phang: ")
        {
            BasePoint = firstResult.Value,
            UseBasePoint = true
        };
        var thirdResult = editor.GetPoint(thirdOptions);
        if (thirdResult.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            var ucsToWcs = editor.CurrentUserCoordinateSystem;
            var first = firstResult.Value.TransformBy(ucsToWcs);
            var second = secondResult.Value.TransformBy(ucsToWcs);
            var third = thirdResult.Value.TransformBy(ucsToWcs);

            var sourceXVector = second - first;
            var planeVector = third - first;
            if (sourceXVector.Length <= 1e-9 || planeVector.Length <= 1e-9)
            {
                editor.WriteMessage("\n3D -> 2D: Ba diem phai khac nhau.");
                return;
            }

            var sourceX = sourceXVector.GetNormal();
            var sourceZVector = sourceX.CrossProduct(planeVector);
            if (sourceZVector.Length <= 1e-9)
            {
                editor.WriteMessage("\n3D -> 2D: Ba diem dang thang hang; hay chon lai diem 3.");
                return;
            }

            var sourceZ = sourceZVector.GetNormal();
            var sourceY = sourceZ.CrossProduct(sourceX).GetNormal();
            var destinationOrigin = new Autodesk.AutoCAD.Geometry.Point3d(first.X, first.Y, 0.0);
            var transform = Autodesk.AutoCAD.Geometry.Matrix3d.AlignCoordinateSystem(
                first,
                sourceX,
                sourceY,
                sourceZ,
                destinationOrigin,
                Autodesk.AutoCAD.Geometry.Vector3d.XAxis,
                Autodesk.AutoCAD.Geometry.Vector3d.YAxis,
                Autodesk.AutoCAD.Geometry.Vector3d.ZAxis);

            using var transaction = database.TransactionManager.StartTransaction();
            foreach (var objectId in selection.Value.GetObjectIds())
            {
                if (transaction.GetObject(objectId, OpenMode.ForWrite) is Entity entity)
                {
                    entity.TransformBy(transform);
                }
            }

            transaction.Commit();
            editor.Regen();
            editor.WriteMessage(
                "\nDa xoay doi tuong 3D nam bet tren WCS XY; mat da chon co Z=0. " +
                "Dung UNDO mot lan de hoan tac.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\n3D -> 2D: {exception.Message}");
        }
    }

    private static void AlignOrDistributeSelectedObjects(
        Database database,
        Editor editor,
        AlignmentOperation operation,
        double spacing)
    {
        var operationName = operation switch
        {
            AlignmentOperation.AlignHorizontal => "can hang ngang",
            AlignmentOperation.AlignVertical => "can hang doc",
            AlignmentOperation.DistributeHorizontal => "can deu ngang",
            _ => "can deu doc"
        };
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = $"\nChon cac doi tuong can {operationName}: "
        };
        var selection = editor.GetSelection(selectionOptions);
        if (selection.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var items = new List<(Entity Entity, Extents3d Extents)>();
            foreach (var objectId in selection.Value.GetObjectIds())
            {
                if (transaction.GetObject(objectId, OpenMode.ForWrite) is not Entity entity || entity.IsErased)
                {
                    continue;
                }

                try
                {
                    items.Add((entity, entity.GeometricExtents));
                }
                catch
                {
                    // Entity types without usable geometric extents are ignored.
                }
            }

            if (items.Count < 2)
            {
                transaction.Abort();
                editor.WriteMessage("\nCAN CHINH: Can chon it nhat 2 doi tuong co hinh hoc hop le.");
                return;
            }

            var movedCount = 0;
            var skippedCount = 0;
            if (operation == AlignmentOperation.AlignHorizontal ||
                operation == AlignmentOperation.AlignVertical)
            {
                var anchor = items[0].Extents;
                var anchorCenterX = (anchor.MinPoint.X + anchor.MaxPoint.X) * 0.5;
                var anchorCenterY = (anchor.MinPoint.Y + anchor.MaxPoint.Y) * 0.5;
                foreach (var item in items.Skip(1))
                {
                    var centerX = (item.Extents.MinPoint.X + item.Extents.MaxPoint.X) * 0.5;
                    var centerY = (item.Extents.MinPoint.Y + item.Extents.MaxPoint.Y) * 0.5;
                    var displacement = operation == AlignmentOperation.AlignHorizontal
                        ? new Autodesk.AutoCAD.Geometry.Vector3d(0.0, anchorCenterY - centerY, 0.0)
                        : new Autodesk.AutoCAD.Geometry.Vector3d(anchorCenterX - centerX, 0.0, 0.0);
                    try
                    {
                        item.Entity.TransformBy(
                            Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(displacement));
                        movedCount++;
                    }
                    catch
                    {
                        skippedCount++;
                    }
                }
            }
            else
            {
                var horizontal = operation == AlignmentOperation.DistributeHorizontal;
                var ordered = horizontal
                    ? items.OrderBy(item => item.Extents.MinPoint.X).ToArray()
                    : items.OrderBy(item => item.Extents.MinPoint.Y).ToArray();
                var cursor = horizontal
                    ? ordered[0].Extents.MaxPoint.X
                    : ordered[0].Extents.MaxPoint.Y;
                foreach (var item in ordered.Skip(1))
                {
                    var displacementAmount = horizontal
                        ? cursor + spacing - item.Extents.MinPoint.X
                        : cursor + spacing - item.Extents.MinPoint.Y;
                    var displacement = horizontal
                        ? new Autodesk.AutoCAD.Geometry.Vector3d(displacementAmount, 0.0, 0.0)
                        : new Autodesk.AutoCAD.Geometry.Vector3d(0.0, displacementAmount, 0.0);
                    try
                    {
                        item.Entity.TransformBy(
                            Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(displacement));
                        cursor = horizontal
                            ? item.Extents.MaxPoint.X + displacementAmount
                            : item.Extents.MaxPoint.Y + displacementAmount;
                        movedCount++;
                    }
                    catch
                    {
                        cursor = horizontal
                            ? Math.Max(cursor, item.Extents.MaxPoint.X)
                            : Math.Max(cursor, item.Extents.MaxPoint.Y);
                        skippedCount++;
                    }
                }
            }

            transaction.Commit();
            editor.WriteMessage(
                $"\nCAN CHINH: Da di chuyen {movedCount} doi tuong." +
                (skippedCount > 0 ? $" Bo qua {skippedCount} doi tuong khong the sua." : string.Empty) +
                " Dung UNDO mot lan de hoan tac.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nCAN CHINH: {exception.Message}");
        }
    }

    private static void SearchObjectsByDimensions(
        Database database,
        Editor editor,
        BoundarySettings settings)
    {
        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace],
                OpenMode.ForRead);
            var results = new List<DimensionSearchSnapshot>();
            foreach (ObjectId objectId in modelSpace)
            {
                if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline ||
                    !polyline.Closed ||
                    polyline.NumberOfVertices < 3)
                {
                    continue;
                }

                var (width, height) = GetMinimumOrientedDimensions(polyline);
                var directMatch =
                    DimensionValuesEqual(width, settings.DimensionSearchWidth) &&
                    DimensionValuesEqual(height, settings.DimensionSearchHeight);
                var swappedMatch =
                    DimensionValuesEqual(width, settings.DimensionSearchHeight) &&
                    DimensionValuesEqual(height, settings.DimensionSearchWidth);
                if (!directMatch && !swappedMatch)
                {
                    continue;
                }

                var extents = polyline.GeometricExtents;
                results.Add(new DimensionSearchSnapshot(
                    objectId.Handle.Value,
                    width,
                    height,
                    extents.MinPoint,
                    extents.MaxPoint));
            }

            transaction.Commit();
            settings.UpdateDimensionSearch(results);
            editor.WriteMessage(results.Count == 0
                ? "\nTIM NHANH KICH THUOC: Khong tim thay PLINE kin phu hop."
                : $"\nTIM NHANH KICH THUOC: Tim thay {results.Count} doi tuong. Bam ket qua tren bang TMH de zoom va AUTO DIM.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nTIM NHANH KICH THUOC: {exception.Message}");
        }
    }

    private static bool DimensionValuesEqual(double first, double second)
    {
        // Numerical guard only; this is not a user tolerance.  It removes floating
        // point noise while still requiring the same CAD dimension.
        var numericalEpsilon = Math.Max(Math.Max(Math.Abs(first), Math.Abs(second)), 1.0) * 1e-8;
        return Math.Abs(first - second) <= numericalEpsilon;
    }

    private static (double Width, double Height) GetMinimumOrientedDimensions(Polyline polyline)
    {
        var points = Enumerable.Range(0, polyline.NumberOfVertices)
            .Select(polyline.GetPoint2dAt)
            .ToArray();
        var bestArea = double.MaxValue;
        var bestWidth = 0.0;
        var bestHeight = 0.0;

        for (var index = 0; index < points.Length; index++)
        {
            var next = (index + 1) % points.Length;
            var edge = points[next] - points[index];
            var edgeLength = edge.Length;
            if (edgeLength <= 1e-9)
            {
                continue;
            }

            var cosine = edge.X / edgeLength;
            var sine = edge.Y / edgeLength;
            var minimumX = double.MaxValue;
            var maximumX = double.MinValue;
            var minimumY = double.MaxValue;
            var maximumY = double.MinValue;
            foreach (var point in points)
            {
                var x = point.X * cosine + point.Y * sine;
                var y = -point.X * sine + point.Y * cosine;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }

            var width = maximumX - minimumX;
            var height = maximumY - minimumY;
            var area = width * height;
            if (area < bestArea)
            {
                bestArea = area;
                bestWidth = width;
                bestHeight = height;
            }
        }

        return (bestWidth, bestHeight);
    }

    private static void FocusDimensionSearchResult(Database database, Editor editor, long handleValue)
    {
        try
        {
            var objectId = database.GetObjectId(false, new Handle(handleValue), 0);
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                if (transaction.GetObject(objectId, OpenMode.ForRead) is not Entity entity || entity.IsErased)
                {
                    editor.WriteMessage("\nTIM NHANH KICH THUOC: Doi tuong khong con ton tai.");
                    return;
                }

                ZoomToExtents(editor, entity.GeometricExtents);
            }

            AutoDimensionSelectedDetails(database, editor, [objectId]);
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nTIM NHANH KICH THUOC: Khong zoom/AUTO DIM duoc: {exception.Message}");
        }
    }

    private static void ZoomToExtents(Editor editor, Extents3d extents)
    {
        using var view = editor.GetCurrentView();
        var worldToDisplay = Autodesk.AutoCAD.Geometry.Matrix3d.PlaneToWorld(view.ViewDirection);
        worldToDisplay = Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                             view.Target - Autodesk.AutoCAD.Geometry.Point3d.Origin) *
                         worldToDisplay;
        worldToDisplay = Autodesk.AutoCAD.Geometry.Matrix3d.Rotation(
                             -view.ViewTwist,
                             view.ViewDirection,
                             view.Target) *
                         worldToDisplay;
        worldToDisplay = worldToDisplay.Inverse();

        var corners = new[]
        {
            new Autodesk.AutoCAD.Geometry.Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Autodesk.AutoCAD.Geometry.Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Autodesk.AutoCAD.Geometry.Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z),
            new Autodesk.AutoCAD.Geometry.Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z)
        }.Select(point => point.TransformBy(worldToDisplay)).ToArray();

        var minimumX = corners.Min(point => point.X);
        var maximumX = corners.Max(point => point.X);
        var minimumY = corners.Min(point => point.Y);
        var maximumY = corners.Max(point => point.Y);
        var aspect = view.Height > 1e-9 ? view.Width / view.Height : 1.0;
        var requiredHeight = Math.Max(
            maximumY - minimumY,
            (maximumX - minimumX) / Math.Max(aspect, 1e-9));
        view.CenterPoint = new Autodesk.AutoCAD.Geometry.Point2d(
            (minimumX + maximumX) * 0.5,
            (minimumY + maximumY) * 0.5);
        view.Height = Math.Max(requiredHeight * 1.7, 1e-6);
        view.Width = view.Height * Math.Max(aspect, 1e-6);
        editor.SetCurrentView(view);
        editor.Regen();
    }

    private static void DeleteSelectedDimensions(Database database, Editor editor)
    {
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\nQuet vung hoac chon doi tuong; tool chi tu dong xoa DIM: "
        };
        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "DIMENSION")
        ]);
        var selection = editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var deletedCount = 0;
            foreach (var objectId in selection.Value.GetObjectIds())
            {
                if (transaction.GetObject(objectId, OpenMode.ForWrite) is not Dimension dimension ||
                    dimension.IsErased)
                {
                    continue;
                }

                dimension.Erase();
                deletedCount++;
            }

            if (deletedCount == 0)
            {
                transaction.Abort();
                editor.WriteMessage("\nAUTO DELETE DIM: Khong tim thay DIM trong vung da chon.");
                return;
            }

            transaction.Commit();
            editor.WriteMessage(
                $"\nAUTO DELETE DIM: Da xoa {deletedCount} DIM. Dung UNDO mot lan de khoi phuc.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nAUTO DELETE DIM: {exception.Message}");
        }
    }

    private static void AutoDimensionSelectedDetails(
        Database database,
        Editor editor,
        ObjectId[]? suppliedObjectIds = null)
    {
        ObjectId[] objectIds;
        if (suppliedObjectIds is null)
        {
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nChon PLINE kin, ARC hoac CIRCLE can tu dong ghi kich thuoc: "
            };
            var filter = new SelectionFilter(
            [
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Start, "ARC"),
                new TypedValue((int)DxfCode.Start, "CIRCLE"),
                new TypedValue((int)DxfCode.Operator, "OR>")
            ]);
            var selection = editor.GetSelection(selectionOptions, filter);
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            objectIds = selection.Value.GetObjectIds();
        }
        else
        {
            objectIds = suppliedObjectIds;
        }

        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite);
            var dimStyle = (DimStyleTableRecord)transaction.GetObject(
                database.Dimstyle,
                OpenMode.ForRead);
            var baseTextHeight = dimStyle.Dimtxt > 1e-9 ? dimStyle.Dimtxt : 2.5;
            var baseArrowSize = dimStyle.Dimasz > 1e-9 ? dimStyle.Dimasz : baseTextHeight;
            var purple = Color.FromColorIndex(ColorMethod.ByAci, 6);
            var detailCount = 0;
            var dimensionCount = 0;
            var skippedOpenCount = 0;

            foreach (var objectId in objectIds)
            {
                var selectedEntity = transaction.GetObject(objectId, OpenMode.ForRead);
                var curveTextHeight = baseTextHeight * 0.55;
                var curveArrowSize = baseArrowSize * 0.60;

                if (selectedEntity is Circle circle)
                {
                    var chordPoint = circle.GetPointAtParameter(0.0);
                    var farChordPoint = circle.GetPointAtParameter(Math.PI);
                    var diameterDimension = new DiametricDimension(
                        chordPoint,
                        farChordPoint,
                        Math.Max(circle.Radius * 0.08, curveTextHeight * 1.5),
                        string.Empty,
                        database.Dimstyle);
                    ConfigureAutomaticDimension(
                        diameterDimension,
                        database,
                        purple,
                        curveTextHeight,
                        curveArrowSize);
                    currentSpace.AppendEntity(diameterDimension);
                    transaction.AddNewlyCreatedDBObject(diameterDimension, true);
                    dimensionCount++;
                    detailCount++;
                    continue;
                }

                if (selectedEntity is Arc arc)
                {
                    var arcPoint = arc.GetPointAtParameter((arc.StartParam + arc.EndParam) * 0.5);
                    var radialDimension = new RadialDimension(
                        arc.Center,
                        arcPoint,
                        Math.Max(arc.Radius * 0.08, curveTextHeight * 1.5),
                        string.Empty,
                        database.Dimstyle);
                    ConfigureAutomaticDimension(
                        radialDimension,
                        database,
                        purple,
                        curveTextHeight,
                        curveArrowSize);
                    currentSpace.AppendEntity(radialDimension);
                    transaction.AddNewlyCreatedDBObject(radialDimension, true);
                    dimensionCount++;
                    detailCount++;
                    continue;
                }

                if (selectedEntity is not Polyline polyline ||
                    polyline.NumberOfVertices < 3)
                {
                    continue;
                }

                if (!polyline.Closed)
                {
                    skippedOpenCount++;
                    continue;
                }

                var points = Enumerable.Range(0, polyline.NumberOfVertices)
                    .Select(polyline.GetPoint3dAt)
                    .ToArray();
                var minimumX = points.Min(point => point.X);
                var maximumX = points.Max(point => point.X);
                var minimumY = points.Min(point => point.Y);
                var maximumY = points.Max(point => point.Y);
                var width = maximumX - minimumX;
                var height = maximumY - minimumY;
                var diagonal = Math.Sqrt(width * width + height * height);
                if (diagonal <= 1e-9)
                {
                    continue;
                }

                var signedAreaTwice = 0.0;
                for (var areaIndex = 0; areaIndex < points.Length; areaIndex++)
                {
                    var areaNext = (areaIndex + 1) % points.Length;
                    signedAreaTwice +=
                        points[areaIndex].X * points[areaNext].Y -
                        points[areaNext].X * points[areaIndex].Y;
                }

                var counterClockwise = signedAreaTwice > 0.0;
                var childTextHeight = baseTextHeight * 0.55;
                var overallTextHeight = baseTextHeight * 1.25;
                // Child dimensions sit just inside the contour, with very short feet.
                var childOffset = Math.Max(diagonal * 0.008, childTextHeight * 1.4);
                var overallOffset = Math.Max(diagonal * 0.10, overallTextHeight * 5.0);
                var z = points.Average(point => point.Z);
                var occupiedChildLabels = new List<(
                    Autodesk.AutoCAD.Geometry.Point3d Center,
                    double Radius)>();

                if (width > 1e-9)
                {
                    var overallWidth = new RotatedDimension(
                        0.0,
                        new Autodesk.AutoCAD.Geometry.Point3d(minimumX, minimumY, z),
                        new Autodesk.AutoCAD.Geometry.Point3d(maximumX, minimumY, z),
                        new Autodesk.AutoCAD.Geometry.Point3d(
                            (minimumX + maximumX) * 0.5,
                            maximumY + overallOffset,
                            z),
                        string.Empty,
                        database.Dimstyle);
                    ConfigureAutomaticDimension(
                        overallWidth,
                        database,
                        purple,
                        overallTextHeight,
                        baseArrowSize * 1.25);
                    currentSpace.AppendEntity(overallWidth);
                    transaction.AddNewlyCreatedDBObject(overallWidth, true);
                    dimensionCount++;
                }

                if (height > 1e-9)
                {
                    var overallHeight = new RotatedDimension(
                        Math.PI * 0.5,
                        new Autodesk.AutoCAD.Geometry.Point3d(minimumX, minimumY, z),
                        new Autodesk.AutoCAD.Geometry.Point3d(minimumX, maximumY, z),
                        new Autodesk.AutoCAD.Geometry.Point3d(
                            minimumX - overallOffset,
                            (minimumY + maximumY) * 0.5,
                            z),
                        string.Empty,
                        database.Dimstyle);
                    ConfigureAutomaticDimension(
                        overallHeight,
                        database,
                        purple,
                        overallTextHeight,
                        baseArrowSize * 1.25);
                    currentSpace.AppendEntity(overallHeight);
                    transaction.AddNewlyCreatedDBObject(overallHeight, true);
                    dimensionCount++;
                }

                for (var index = 0; index < points.Length; index++)
                {
                    var next = (index + 1) % points.Length;
                    if (polyline.GetBulgeAt(index) != 0.0)
                    {
                        var arcGeometry = polyline.GetArcSegmentAt(index);
                        var arcPoint = polyline.GetPointAtParameter(index + 0.5);
                        var radialDimension = new RadialDimension(
                            arcGeometry.Center,
                            arcPoint,
                            Math.Max(arcGeometry.Radius * 0.08, childTextHeight * 1.5),
                            string.Empty,
                            database.Dimstyle);
                        ConfigureAutomaticDimension(
                            radialDimension,
                            database,
                            purple,
                            childTextHeight,
                            baseArrowSize * 0.60);
                        currentSpace.AppendEntity(radialDimension);
                        transaction.AddNewlyCreatedDBObject(radialDimension, true);
                        dimensionCount++;
                        continue;
                    }

                    var start = points[index];
                    var end = points[next];
                    var edge = end - start;
                    var length = edge.Length;
                    if (length <= 1e-9)
                    {
                        continue;
                    }

                    var horizontal = Math.Abs(edge.Y) <= length * 1e-8;
                    var vertical = Math.Abs(edge.X) <= length * 1e-8;
                    if (horizontal && Math.Abs(length - width) <= Math.Max(width, 1.0) * 1e-8 ||
                        vertical && Math.Abs(length - height) <= Math.Max(height, 1.0) * 1e-8)
                    {
                        continue;
                    }

                    var midpoint = new Autodesk.AutoCAD.Geometry.Point3d(
                        (start.X + end.X) * 0.5,
                        (start.Y + end.Y) * 0.5,
                        z);
                    var outward = counterClockwise
                        ? new Autodesk.AutoCAD.Geometry.Vector3d(edge.Y, -edge.X, 0.0).GetNormal()
                        : new Autodesk.AutoCAD.Geometry.Vector3d(-edge.Y, edge.X, 0.0).GetNormal();

                    var displayedLength = length.ToString("0.##");
                    var labelRadius = Math.Max(
                        diagonal * 0.015,
                        childTextHeight * (displayedLength.Length * 0.42 + 1.0));
                    var insideLinePoint = midpoint - outward * childOffset;
                    var outsideLinePoint = midpoint + outward * childOffset;

                    double CollisionScore(Autodesk.AutoCAD.Geometry.Point3d candidate)
                    {
                        var score = 0.0;
                        foreach (var occupied in occupiedChildLabels)
                        {
                            var requiredDistance = occupied.Radius + labelRadius;
                            var actualDistance = occupied.Center.DistanceTo(candidate);
                            if (actualDistance < requiredDistance)
                            {
                                score += 1.0 +
                                    (requiredDistance - actualDistance) / requiredDistance * 4.0;
                            }
                        }

                        return score;
                    }

                    var insideScore = CollisionScore(insideLinePoint);
                    // Slight preference for an inside dimension when both positions are
                    // equally clear.  Outside is selected automatically when it reduces
                    // crowding around a dense corner.
                    var outsideScore = CollisionScore(outsideLinePoint) + 0.15;
                    var dimensionLinePoint = outsideScore < insideScore
                        ? outsideLinePoint
                        : insideLinePoint;

                    var childDimension = new AlignedDimension(
                        start,
                        end,
                        dimensionLinePoint,
                        string.Empty,
                        database.Dimstyle);
                    ConfigureAutomaticDimension(
                        childDimension,
                        database,
                        purple,
                        childTextHeight,
                        baseArrowSize * 0.60);
                    childDimension.Dimexo = childTextHeight * 0.18;
                    childDimension.Dimexe = childTextHeight * 0.30;
                    currentSpace.AppendEntity(childDimension);
                    transaction.AddNewlyCreatedDBObject(childDimension, true);
                    occupiedChildLabels.Add((dimensionLinePoint, labelRadius));
                    dimensionCount++;
                }

                detailCount++;
            }

            if (dimensionCount == 0)
            {
                transaction.Abort();
                editor.WriteMessage("\nAUTO DIM: Khong co PLINE kin hop le de ghi kich thuoc.");
                return;
            }

            transaction.Commit();
            editor.Regen();
            editor.WriteMessage(
                $"\nAUTO DIM: Da tao {dimensionCount} kich thuoc cho {detailCount} chi tiet." +
                (skippedOpenCount > 0
                    ? $" Bo qua {skippedOpenCount} PLINE chua kin."
                    : string.Empty) +
                " Dung UNDO mot lan de hoan tac.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nAUTO DIM: {exception.Message}");
        }
    }

    private static void ConfigureAutomaticDimension(
        Dimension dimension,
        Database database,
        Color extensionLineColor,
        double textHeight,
        double arrowSize)
    {
        dimension.SetDatabaseDefaults(database);
        dimension.DimensionStyle = database.Dimstyle;
        dimension.Dimclre = extensionLineColor;
        dimension.Dimtxt = textHeight;
        dimension.Dimasz = arrowSize;
    }

    private static void ReduceSelectedPolylinePoints(
        Database database,
        Editor editor,
        BoundarySettings settings)
    {
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\nChon PLINE, SPLINE va LINE can giam point toi da: "
        };
        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "LWPOLYLINE,SPLINE,LINE")
        ]);
        var selection = editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var optimize = settings.PointReductionMode == PointReductionMode.Optimize;
            var candidates = new List<(ObjectId Id, int RemovedCount, bool IsSpline, double Tolerance)>();
            var pointCountBefore = 0;
            var pointCountAfter = 0;
            var selectedLineCount = 0;

            foreach (var objectId in selection.Value.GetObjectIds())
            {
                var entity = transaction.GetObject(objectId, OpenMode.ForRead);
                if (entity is Polyline polyline)
                {
                    var reductionTolerance = optimize
                        ? GetAutomaticReductionTolerance(polyline)
                        : 0.0;
                    pointCountBefore += polyline.NumberOfVertices;
                    using var reduced = (Polyline)polyline.Clone();
                    var removedCount = PolylinePointReducer.ReduceExact(reduced);
                    if (optimize)
                    {
                        removedCount += PolylinePointReducer.ReduceWithinTolerance(
                            reduced,
                            reductionTolerance);
                    }

                    // Tolerant PLINE reduction is bounded directly by the RDP/sagitta
                    // construction. A second closest-point gate can reject valid results
                    // on very dense closed polylines because of AutoCAD curve numerics.
                    var geometryAccepted = optimize ||
                                           PolylinePointReducer.GeometryMeasuresMatch(polyline, reduced);
                    if (removedCount > 0 && geometryAccepted)
                    {
                        candidates.Add((objectId, removedCount, false, reductionTolerance));
                        pointCountAfter += reduced.NumberOfVertices;
                    }
                    else
                    {
                        pointCountAfter += polyline.NumberOfVertices;
                    }

                    continue;
                }

                if (entity is Spline spline)
                {
                    var reductionTolerance = optimize
                        ? GetAutomaticReductionTolerance(spline)
                        : 0.0;
                    var before = SplinePointReducer.CountEditablePoints(spline);
                    pointCountBefore += before;
                    using var reduced = (Spline)spline.Clone();
                    var removedCount = SplinePointReducer.ReduceExact(reduced);
                    if (optimize)
                    {
                        removedCount += SplinePointReducer.ReduceWithinTolerance(
                            reduced,
                            reductionTolerance);
                    }

                    var geometryAccepted = optimize
                        ? SplinePointReducer.GeometryWithinTolerance(
                            spline,
                            reduced,
                            reductionTolerance)
                        : SplinePointReducer.GeometryMatches(spline, reduced);
                    if (removedCount > 0 && geometryAccepted)
                    {
                        candidates.Add((objectId, removedCount, true, reductionTolerance));
                        pointCountAfter += SplinePointReducer.CountEditablePoints(reduced);
                    }
                    else
                    {
                        pointCountAfter += before;
                    }

                    continue;
                }

                if (entity is Line)
                {
                    // A LINE already has the mathematical minimum of two endpoints.
                    pointCountBefore += 2;
                    pointCountAfter += 2;
                    selectedLineCount++;
                }
            }

            var totalRemoved = pointCountBefore - pointCountAfter;
            if (totalRemoved <= 0)
            {
                settings.UpdatePointReduction(pointCountBefore, pointCountBefore, 0);
                editor.WriteMessage(
                    (optimize
                        ? "\nKhong giam duoc point ma van giu an toan bien dang."
                        : "\nKhong co point du thua co the xoa ma van giu nguyen 100% hinh hoc.") +
                    (selectedLineCount > 0
                        ? $" {selectedLineCount} LINE da o muc toi thieu 2 point."
                        : string.Empty));
                transaction.Abort();
                return;
            }

            editor.WriteMessage(
                $"\nPhat hien {totalRemoved} point du tren {candidates.Count} doi tuong; " +
                $"{pointCountBefore} point -> {pointCountAfter} point." +
                (optimize ? " Sai so duoc tinh tu dong theo kich thuoc tung duong." : string.Empty));
            var confirmOptions = new PromptKeywordOptions(
                "\nAp dung ket qua giam point toi da? [Giam/Huy]")
            {
                AllowNone = true
            };
            confirmOptions.Keywords.Add("Giam");
            confirmOptions.Keywords.Add("Huy");
            confirmOptions.Keywords.Default = "Giam";
            var confirm = editor.GetKeywords(confirmOptions);
            if (confirm.Status == PromptStatus.Cancel ||
                confirm.Status == PromptStatus.OK && confirm.StringResult == "Huy")
            {
                transaction.Abort();
                editor.WriteMessage("\nDa huy giam point.");
                return;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.IsSpline)
                {
                    var spline = (Spline)transaction.GetObject(candidate.Id, OpenMode.ForWrite);
                    using var original = (Spline)spline.Clone();
                    var removedCount = SplinePointReducer.ReduceExact(spline);
                    if (optimize)
                    {
                        removedCount += SplinePointReducer.ReduceWithinTolerance(
                            spline,
                            candidate.Tolerance);
                    }

                    var geometryAccepted = optimize
                        ? SplinePointReducer.GeometryWithinTolerance(
                            original,
                            spline,
                            candidate.Tolerance)
                        : SplinePointReducer.GeometryMatches(original, spline);
                    if (removedCount != candidate.RemovedCount ||
                        !geometryAccepted)
                    {
                        throw new InvalidOperationException(
                            "Kiem tra SPLINE khong dat; da huy toan bo de bao ve duong goc.");
                    }

                    continue;
                }

                var polyline = (Polyline)transaction.GetObject(candidate.Id, OpenMode.ForWrite);
                using var originalPolyline = (Polyline)polyline.Clone();
                var polylineRemovedCount = PolylinePointReducer.ReduceExact(polyline);
                if (optimize)
                {
                    polylineRemovedCount += PolylinePointReducer.ReduceWithinTolerance(
                        polyline,
                        candidate.Tolerance);
                }

                var polylineGeometryAccepted = optimize ||
                                               PolylinePointReducer.GeometryMeasuresMatch(
                                                   originalPolyline,
                                                   polyline);
                if (polylineRemovedCount != candidate.RemovedCount ||
                    !polylineGeometryAccepted)
                {
                    throw new InvalidOperationException(
                        "Kiem tra PLINE khong dat; da huy toan bo de bao ve duong goc.");
                }
            }

            transaction.Commit();
            settings.UpdatePointReduction(pointCountBefore, pointCountAfter, candidates.Count);
            editor.Regen();
            editor.WriteMessage(
                $"\nDa giam {totalRemoved} point" +
                (optimize
                    ? ", da tu dong bao ve bien dang. "
                    : ", giu nguyen hinh hoc PLINE/SPLINE. ") +
                "Dung UNDO mot lan de hoan tac.");
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nGiam point: {exception.Message}");
        }
    }

    private static double GetAutomaticReductionTolerance(Entity entity)
    {
        // A scale-relative ceiling keeps the same visual fidelity in drawings that use
        // mm, m, inches, or unitless coordinates. 0.01% of the entity diagonal is small
        // enough to preserve its silhouette while still removing dense sampled points.
        const double relativeDeviation = 0.0001;
        try
        {
            var extents = entity.GeometricExtents;
            var diagonal = extents.MinPoint.DistanceTo(extents.MaxPoint);
            if (double.IsFinite(diagonal) && diagonal > 0.0)
            {
                return Math.Max(diagonal * relativeDeviation, 1e-9);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Fall through to a conservative scale when extents are unavailable.
        }

        return 1e-6;
    }

    private static void ScanSelectedGaps(
        Database database,
        Editor editor,
        IReadOnlyCollection<ObjectId> objectIds,
        BoundarySettings settings)
    {
        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var engine = new BoundaryGeometryEngine();
            var result = engine.DetectGaps(
                transaction,
                objectIds,
                editor.CurrentUserCoordinateSystem,
                settings);
            var detectedGaps = result.Gaps
                .Select(gap => new GapDetectionSnapshot(
                    new Autodesk.AutoCAD.Geometry.Point3d(gap.Start.X, gap.Start.Y, 0.0)
                        .TransformBy(editor.CurrentUserCoordinateSystem),
                    new Autodesk.AutoCAD.Geometry.Point3d(gap.End.X, gap.End.Y, 0.0)
                        .TransformBy(editor.CurrentUserCoordinateSystem)))
                .ToArray();
            settings.UpdateGapDetection(detectedGaps);

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace],
                OpenMode.ForWrite);
            var gapMarkers = AddGapDetectionMarkers(
                result,
                modelSpace,
                transaction,
                editor.CurrentUserCoordinateSystem);
            database.TransactionManager.QueueForGraphicsFlush();
            editor.Regen();

            if (result.Gaps.Count == 0)
            {
                editor.WriteMessage("\nKhong phat hien dau mut dang ho; bien dang kin.");
            }
            else
            {
                editor.WriteMessage(
                    $"\nDa phat hien {result.Gaps.Count} khe ho; " +
                    $"khe lon nhat {result.Gaps.Max(gap => gap.Length):0.###}. " +
                    "Cac khe dang duoc noi danh dau mau do.");
            }

            if (result.UnmatchedEndpointCount > 0)
            {
                editor.WriteMessage(
                    $"\nCon {result.UnmatchedEndpointCount} dau mut le chua the ghep cap.");
            }

            if (result.Gaps.Count == 0)
            {
                transaction.Abort();
                editor.Regen();
                return;
            }

            var connectOptions = new PromptKeywordOptions(
                $"\nNoi tat ca {result.Gaps.Count} khe ho? [Co/Khong]")
            {
                AllowNone = true
            };
            connectOptions.Keywords.Add("Co");
            connectOptions.Keywords.Add("Khong");
            connectOptions.Keywords.Default = "Khong";
            var connectResult = editor.GetKeywords(connectOptions);
            var shouldConnect = connectResult.Status == PromptStatus.OK &&
                                connectResult.StringResult == "Co";

            if (shouldConnect)
            {
                foreach (var marker in gapMarkers)
                {
                    marker.Color = Color.FromColorIndex(ColorMethod.ByAci, settings.OutputColorIndex);
                    marker.LineWeight = LineWeight.ByLayer;
                }

                transaction.Commit();
                editor.WriteMessage($"\nDa noi tat ca {result.Gaps.Count} khe ho bang LINE.");
            }
            else
            {
                transaction.Abort();
                editor.WriteMessage("\nKhong noi khe; ket qua do van duoc giu tren bang TMH.");
            }

            editor.Regen();
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nQuet khe ho: {exception.Message}");
        }
    }

    public void ConfigureOutput()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        var editor = document.Editor;
        var settings = BoundarySettings.Current;
        var typeOptions = new PromptKeywordOptions("\nChon loai duong ket qua")
        {
            AllowNone = true
        };
        typeOptions.Keywords.Add("Pline");
        typeOptions.Keywords.Add("Spline");
        typeOptions.Keywords.Default = settings.OutputCurveType == OutputCurveType.Spline ? "Spline" : "Pline";
        var typeResult = editor.GetKeywords(typeOptions);
        if (typeResult.Status == PromptStatus.Cancel)
        {
            return;
        }

        settings.OutputCurveType = typeResult.StringResult == "Spline"
            ? OutputCurveType.Spline
            : OutputCurveType.Polyline;

        var colorOptions = new PromptIntegerOptions("\nNhap mau ACI [1-255], hoac 256 de ByLayer")
        {
            AllowNone = true,
            DefaultValue = settings.OutputColorIndex,
            LowerLimit = 1,
            UpperLimit = 256
        };
        var colorResult = editor.GetInteger(colorOptions);
        if (colorResult.Status == PromptStatus.OK)
        {
            settings.OutputColorIndex = (short)colorResult.Value;
        }

        editor.WriteMessage(
            $"\nDau ra: {settings.OutputCurveType}; mau ACI: {settings.OutputColorIndex}.");
    }

    private static Entity CreateOutputEntity(
        BoundaryBuildResult result,
        BoundarySettings settings,
        Autodesk.AutoCAD.Geometry.Matrix3d ucsToWcs)
    {
        return settings.OutputCurveType switch
        {
            OutputCurveType.Spline => result.Loop.CreateSpline(ucsToWcs, settings.SplineTolerance),
            _ => result.Loop.CreatePolyline(ucsToWcs)
        };
    }

    private static List<Line> AddGapDetectionMarkers(
        GapDetectionResult result,
        BlockTableRecord modelSpace,
        Transaction transaction,
        Autodesk.AutoCAD.Geometry.Matrix3d ucsToWcs)
    {
        var markers = new List<Line>();
        foreach (var gap in result.Gaps)
        {
            var start = new Autodesk.AutoCAD.Geometry.Point3d(gap.Start.X, gap.Start.Y, 0.0)
                .TransformBy(ucsToWcs);
            var end = new Autodesk.AutoCAD.Geometry.Point3d(gap.End.X, gap.End.Y, 0.0)
                .TransformBy(ucsToWcs);
            var line = new Line(start, end)
            {
                Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
                LineWeight = LineWeight.LineWeight070
            };
            modelSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            markers.Add(line);
        }

        return markers;
    }

    private static List<Entity> AddBridgeMarkers(
        BoundaryBuildResult result,
        BlockTableRecord modelSpace,
        Transaction transaction,
        Autodesk.AutoCAD.Geometry.Matrix3d ucsToWcs)
    {
        var markers = new List<Entity>();
        foreach (var directedEdge in result.Loop.Edges.Where(edge => edge.Edge.Geometry.IsBridge))
        {
            var geometry = directedEdge.Edge.Geometry;
            var start = new Autodesk.AutoCAD.Geometry.Point3d(geometry.Start.X, geometry.Start.Y, 0.0)
                .TransformBy(ucsToWcs);
            var end = new Autodesk.AutoCAD.Geometry.Point3d(geometry.End.X, geometry.End.Y, 0.0)
                .TransformBy(ucsToWcs);
            var line = new Line(start, end)
            {
                Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
                LineWeight = LineWeight.LineWeight050
            };
            modelSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            markers.Add(line);
        }

        return markers;
    }
}
