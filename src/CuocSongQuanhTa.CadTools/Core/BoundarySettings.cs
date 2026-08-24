using Autodesk.AutoCAD.Geometry;

namespace CuocSongQuanhTa.CadTools.Core;

public enum OutputCurveType
{
    Polyline,
    Spline
}

public enum PointReductionMode
{
    Optimize,
    Exact
}

public enum AlignmentOperation
{
    None,
    AlignHorizontal,
    AlignVertical,
    DistributeHorizontal,
    DistributeVertical
}

public sealed record GapDetectionSnapshot(Point3d Start, Point3d End)
{
    public double Length => Start.DistanceTo(End);
}

public sealed record DimensionSearchSnapshot(
    long HandleValue,
    double Width,
    double Height,
    Point3d Minimum,
    Point3d Maximum);

public sealed class BoundarySettings
{
    private bool _gapScanRequested;
    private bool _pointReductionRequested;
    private bool _flatten3dRequested;
    private bool _autoDimensionRequested;
    private bool _autoDeleteDimensionRequested;
    private bool _dimensionSearchRequested;
    private long? _dimensionSearchFocusHandle;
    private AlignmentOperation _alignmentOperation;
    private bool _gCodeImportRequested;

    private static readonly Lazy<BoundarySettings> LazyCurrent = new(() => new BoundarySettings());

    public static BoundarySettings Current => LazyCurrent.Value;

    public double GapTolerance { get; set; } = 10.0;

    public double SplineTolerance { get; set; } = 1.0;

    public double MaximumGapAngleDegrees { get; set; } = 120.0;

    public bool KeepSourceObjects { get; set; } = true;

    public OutputCurveType OutputCurveType { get; set; } = OutputCurveType.Polyline;

    public short OutputColorIndex { get; set; } = 256;

    public PointReductionMode PointReductionMode { get; set; } = PointReductionMode.Optimize;

    public double DimensionSearchWidth { get; set; }

    public double DimensionSearchHeight { get; set; }

    public double AlignmentSpacing { get; set; }

    public string GCodeFilePath { get; set; } = string.Empty;

    public IReadOnlyList<DimensionSearchSnapshot> LastDimensionSearchResults { get; private set; } =
        Array.Empty<DimensionSearchSnapshot>();

    public event EventHandler? DimensionSearchChanged;

    public bool HasGapDetectionResult { get; private set; }

    public int LastDetectedGapCount => LastDetectedGaps.Count;

    public double LastDetectedMaximumGap => LastDetectedGaps.Count == 0
        ? 0.0
        : LastDetectedGaps.Max(gap => gap.Length);

    public double LastDetectedTotalGap => LastDetectedGaps.Sum(gap => gap.Length);

    public IReadOnlyList<GapDetectionSnapshot> LastDetectedGaps { get; private set; } =
        Array.Empty<GapDetectionSnapshot>();

    public event EventHandler? GapDetectionChanged;

    public bool HasPointReductionResult { get; private set; }

    public int LastPointCountBefore { get; private set; }

    public int LastPointCountAfter { get; private set; }

    public int LastReducedObjectCount { get; private set; }

    public event EventHandler? PointReductionChanged;

    public void RequestGapScan()
    {
        _gapScanRequested = true;
    }

    public void CancelGapScanRequest()
    {
        _gapScanRequested = false;
    }

    public bool ConsumeGapScanRequest()
    {
        var requested = _gapScanRequested;
        _gapScanRequested = false;
        return requested;
    }

    public void RequestPointReduction()
    {
        _pointReductionRequested = true;
    }

    public void CancelPointReductionRequest()
    {
        _pointReductionRequested = false;
    }

    public bool ConsumePointReductionRequest()
    {
        var requested = _pointReductionRequested;
        _pointReductionRequested = false;
        return requested;
    }

    public void RequestFlatten3d()
    {
        _flatten3dRequested = true;
    }

    public void CancelFlatten3dRequest()
    {
        _flatten3dRequested = false;
    }

    public bool ConsumeFlatten3dRequest()
    {
        var requested = _flatten3dRequested;
        _flatten3dRequested = false;
        return requested;
    }

    public void RequestAutoDimension()
    {
        _autoDimensionRequested = true;
    }

    public void CancelAutoDimensionRequest()
    {
        _autoDimensionRequested = false;
    }

    public bool ConsumeAutoDimensionRequest()
    {
        var requested = _autoDimensionRequested;
        _autoDimensionRequested = false;
        return requested;
    }

    public void RequestAutoDeleteDimension()
    {
        _autoDeleteDimensionRequested = true;
    }

    public void CancelAutoDeleteDimensionRequest()
    {
        _autoDeleteDimensionRequested = false;
    }

    public bool ConsumeAutoDeleteDimensionRequest()
    {
        var requested = _autoDeleteDimensionRequested;
        _autoDeleteDimensionRequested = false;
        return requested;
    }

    public void RequestDimensionSearch()
    {
        _dimensionSearchRequested = true;
    }

    public void CancelDimensionSearchRequest()
    {
        _dimensionSearchRequested = false;
    }

    public bool ConsumeDimensionSearchRequest()
    {
        var requested = _dimensionSearchRequested;
        _dimensionSearchRequested = false;
        return requested;
    }

    public void RequestDimensionSearchFocus(long handleValue)
    {
        _dimensionSearchFocusHandle = handleValue;
    }

    public long? ConsumeDimensionSearchFocusRequest()
    {
        var handleValue = _dimensionSearchFocusHandle;
        _dimensionSearchFocusHandle = null;
        return handleValue;
    }

    public void UpdateDimensionSearch(IEnumerable<DimensionSearchSnapshot> results)
    {
        LastDimensionSearchResults = results.ToArray();
        DimensionSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestAlignment(AlignmentOperation operation, double spacing = 0.0)
    {
        _alignmentOperation = operation;
        AlignmentSpacing = spacing;
    }

    public void CancelAlignmentRequest()
    {
        _alignmentOperation = AlignmentOperation.None;
    }

    public AlignmentOperation ConsumeAlignmentRequest()
    {
        var operation = _alignmentOperation;
        _alignmentOperation = AlignmentOperation.None;
        return operation;
    }

    public void RequestGCodeImport(string filePath)
    {
        GCodeFilePath = filePath;
        _gCodeImportRequested = true;
    }

    public void CancelGCodeImportRequest()
    {
        _gCodeImportRequested = false;
    }

    public bool ConsumeGCodeImportRequest()
    {
        var requested = _gCodeImportRequested;
        _gCodeImportRequested = false;
        return requested;
    }

    public void UpdateGapDetection(IEnumerable<GapDetectionSnapshot> gaps)
    {
        LastDetectedGaps = gaps
            .Where(gap => gap.Length > 0.0)
            .OrderByDescending(gap => gap.Length)
            .ToArray();
        HasGapDetectionResult = true;
        GapDetectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdatePointReduction(int pointCountBefore, int pointCountAfter, int objectCount)
    {
        LastPointCountBefore = pointCountBefore;
        LastPointCountAfter = pointCountAfter;
        LastReducedObjectCount = objectCount;
        HasPointReductionResult = true;
        PointReductionChanged?.Invoke(this, EventArgs.Empty);
    }
}
