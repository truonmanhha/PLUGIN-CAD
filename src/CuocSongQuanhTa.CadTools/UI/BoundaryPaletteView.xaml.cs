using CuocSongQuanhTa.CadTools.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CuocSongQuanhTa.CadTools.UI;

public partial class BoundaryPaletteView : UserControl
{
    private bool _gapDetectionSubscribed;
    private bool _pointReductionSubscribed;
    private bool _dimensionSearchSubscribed;

    public BoundaryPaletteView()
    {
        InitializeComponent();
        LoadSettings();
        Loaded += BoundaryPaletteView_OnLoaded;
        Unloaded += BoundaryPaletteView_OnUnloaded;
        UpdateGapDetectionDisplay();
        UpdatePointReductionDisplay();
        UpdateDimensionSearchDisplay();
    }

    private void BoundaryPaletteView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_gapDetectionSubscribed)
        {
            BoundarySettings.Current.GapDetectionChanged += Settings_OnGapDetectionChanged;
            _gapDetectionSubscribed = true;
        }

        if (!_pointReductionSubscribed)
        {
            BoundarySettings.Current.PointReductionChanged += Settings_OnPointReductionChanged;
            _pointReductionSubscribed = true;
        }

        if (!_dimensionSearchSubscribed)
        {
            BoundarySettings.Current.DimensionSearchChanged += Settings_OnDimensionSearchChanged;
            _dimensionSearchSubscribed = true;
        }

        UpdateGapDetectionDisplay();
        UpdatePointReductionDisplay();
        UpdateDimensionSearchDisplay();
    }

    private void BoundaryPaletteView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_gapDetectionSubscribed)
        {
            BoundarySettings.Current.GapDetectionChanged -= Settings_OnGapDetectionChanged;
            _gapDetectionSubscribed = false;
        }

        if (_pointReductionSubscribed)
        {
            BoundarySettings.Current.PointReductionChanged -= Settings_OnPointReductionChanged;
            _pointReductionSubscribed = false;
        }

        if (_dimensionSearchSubscribed)
        {
            BoundarySettings.Current.DimensionSearchChanged -= Settings_OnDimensionSearchChanged;
            _dimensionSearchSubscribed = false;
        }
    }

    private void Settings_OnGapDetectionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateGapDetectionDisplay);
            return;
        }

        UpdateGapDetectionDisplay();
    }

    private void Settings_OnPointReductionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdatePointReductionDisplay);
            return;
        }

        UpdatePointReductionDisplay();
    }

    private void Settings_OnDimensionSearchChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateDimensionSearchDisplay);
            return;
        }

        UpdateDimensionSearchDisplay();
    }

    private void UpdateDimensionSearchDisplay()
    {
        var results = BoundarySettings.Current.LastDimensionSearchResults;
        DimensionSearchResultsPanel.Children.Clear();
        DimensionSearchCountText.Text = results.Count.ToString(CultureInfo.CurrentCulture);
        DimensionSearchStatusText.Text = results.Count == 0
            ? "CHƯA CÓ KẾT QUẢ"
            : $"TÌM THẤY {results.Count} ĐỐI TƯỢNG";

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var button = new Button
            {
                Content = $"⌕  {index + 1:00}  ·  {result.Width:0.##} × {result.Height:0.##}",
                Tag = result,
                ToolTip = $"Zoom và AUTO DIM đối tượng {index + 1}"
            };
            button.Style = (Style)FindResource("GapZoomButtonStyle");
            button.Click += DimensionSearchResult_OnClick;
            DimensionSearchResultsPanel.Children.Add(button);
        }
    }

    private void DimensionSearchResult_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DimensionSearchSnapshot result })
        {
            return;
        }

        var settings = BoundarySettings.Current;
        settings.CancelGapScanRequest();
        settings.CancelPointReductionRequest();
        settings.CancelFlatten3dRequest();
        settings.CancelAutoDimensionRequest();
        settings.CancelAutoDeleteDimensionRequest();
        settings.CancelDimensionSearchRequest();
        settings.RequestDimensionSearchFocus(result.HandleValue);
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void UpdatePointReductionDisplay()
    {
        var settings = BoundarySettings.Current;
        if (!settings.HasPointReductionResult)
        {
            PointCountBeforeText.Text = "--";
            PointCountAfterText.Text = "--";
            PointRemovedText.Text = "--";
            PointReductionStatusText.Text = "CHƯA CHẠY";
            return;
        }

        PointCountBeforeText.Text = settings.LastPointCountBefore.ToString(CultureInfo.CurrentCulture);
        PointCountAfterText.Text = settings.LastPointCountAfter.ToString(CultureInfo.CurrentCulture);
        PointRemovedText.Text = (settings.LastPointCountBefore - settings.LastPointCountAfter)
            .ToString(CultureInfo.CurrentCulture);
        PointReductionStatusText.Text = settings.LastPointCountBefore == settings.LastPointCountAfter
            ? "KHÔNG CÓ POINT DƯ"
            : $"ĐÃ GIẢM {settings.LastReducedObjectCount} ĐỐI TƯỢNG";
    }

    private void UpdateGapDetectionDisplay()
    {
        var settings = BoundarySettings.Current;
        GapButtonsPanel.Children.Clear();
        GapButtonsPanel.Visibility = Visibility.Collapsed;
        if (!settings.HasGapDetectionResult)
        {
            GapScanStatusText.Text = "CHƯA QUÉT";
            GapScanStatusText.Foreground = new SolidColorBrush(Color.FromRgb(145, 164, 183));
            GapScanStatusBorder.Background = new SolidColorBrush(Color.FromRgb(53, 68, 86));
            GapCountValueText.Text = "--";
            GapMaximumValueText.Text = "--";
            GapDetailsText.Text = "Bấm nút rồi chọn các nét. Không cần pick vùng; không tạo đường.";
            return;
        }

        var gapCount = settings.LastDetectedGapCount;
        GapCountValueText.Text = gapCount.ToString(CultureInfo.CurrentCulture);
        if (gapCount == 0)
        {
            GapScanStatusText.Text = "ĐÃ KÍN";
            GapScanStatusText.Foreground = new SolidColorBrush(Color.FromRgb(85, 197, 255));
            GapScanStatusBorder.Background = new SolidColorBrush(Color.FromRgb(35, 65, 83));
            GapMaximumValueText.Text = "0";
            GapDetailsText.Text = "Không phát hiện khe hở trên vòng biên đã chọn.";
            return;
        }

        GapScanStatusText.Text = $"{gapCount} KHE";
        GapScanStatusText.Foreground = new SolidColorBrush(Color.FromRgb(85, 197, 255));
        GapScanStatusBorder.Background = new SolidColorBrush(Color.FromRgb(35, 65, 83));
        GapMaximumValueText.Text = settings.LastDetectedMaximumGap.ToString("0.###", CultureInfo.CurrentCulture);
        GapDetailsText.Text = "Bấm từng khe để zoom. Chọn CÓ ở dòng lệnh nếu muốn nối tất cả.";
        GapButtonsPanel.Visibility = Visibility.Visible;
        for (var index = 0; index < settings.LastDetectedGaps.Count; index++)
        {
            var gap = settings.LastDetectedGaps[index];
            var button = new Button
            {
                Content = $"⌕  Khe {index + 1}: {gap.Length.ToString("0.###", CultureInfo.CurrentCulture)}",
                Tag = gap,
                ToolTip = $"Zoom tới khe {index + 1}"
            };
            button.Style = (Style)FindResource("GapZoomButtonStyle");
            button.Click += GapZoomButton_OnClick;
            GapButtonsPanel.Children.Add(button);
        }
    }

    private void GapZoomButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GapDetectionSnapshot gap })
        {
            return;
        }

        ZoomToGap(gap);
    }

    private static void ZoomToGap(GapDetectionSnapshot gap)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        var editor = document.Editor;
        try
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

            var start = gap.Start.TransformBy(worldToDisplay);
            var end = gap.End.TransformBy(worldToDisplay);
            var centerX = (start.X + end.X) * 0.5;
            var centerY = (start.Y + end.Y) * 0.5;
            var deltaX = Math.Abs(end.X - start.X);
            var deltaY = Math.Abs(end.Y - start.Y);
            var aspect = view.Height > 1e-9 ? view.Width / view.Height : 1.0;
            var requiredHeight = Math.Max(deltaY, deltaX / Math.Max(aspect, 1e-9));
            var targetHeight = Math.Max(requiredHeight * 8.0, gap.Length * 2.0);

            view.CenterPoint = new Autodesk.AutoCAD.Geometry.Point2d(centerX, centerY);
            view.Height = Math.Max(targetHeight, 1e-6);
            view.Width = view.Height * Math.Max(aspect, 1e-6);
            editor.SetCurrentView(view);
            editor.Regen();
        }
        catch (System.Exception exception)
        {
            editor.WriteMessage($"\nKhong zoom duoc toi khe ho: {exception.Message}");
        }
    }

    private void LoadSettings()
    {
        var settings = BoundarySettings.Current;
        GapToleranceBox.Text = settings.GapTolerance.ToString("0.###", CultureInfo.CurrentCulture);
        GapAngleBox.Text = settings.MaximumGapAngleDegrees.ToString("0.###", CultureInfo.CurrentCulture);
        SplineToleranceBox.Text = settings.SplineTolerance.ToString("0.###", CultureInfo.CurrentCulture);
        SelectItemByTag(OutputTypeBox, settings.OutputCurveType.ToString());
        SelectItemByTag(OutputColorBox, settings.OutputColorIndex.ToString(CultureInfo.InvariantCulture));
        SelectItemByTag(PointReductionModeBox, settings.PointReductionMode.ToString());
        DimensionSearchWidthBox.Text = settings.DimensionSearchWidth > 0.0
            ? settings.DimensionSearchWidth.ToString("0.###", CultureInfo.CurrentCulture)
            : string.Empty;
        DimensionSearchHeightBox.Text = settings.DimensionSearchHeight > 0.0
            ? settings.DimensionSearchHeight.ToString("0.###", CultureInfo.CurrentCulture)
            : string.Empty;
        AlignmentSpacingBox.Text = settings.AlignmentSpacing.ToString("0.###", CultureInfo.CurrentCulture);
        if (!string.IsNullOrWhiteSpace(settings.GCodeFilePath))
        {
            GCodeFileNameText.Text = Path.GetFileName(settings.GCodeFilePath);
            GCodeFileNameText.ToolTip = settings.GCodeFilePath;
        }
    }

    private void SettingsBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings(showMessage: false);
    }

    private void BoundaryTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(0);
    }

    private void PointReductionTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(1);
    }

    private void Flatten3dTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(2);
    }

    private void AutoDimensionTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(3);
    }

    private void QuickSearchTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(4);
    }

    private void AlignmentTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(5);
    }

    private void GCodeTab_OnClick(object sender, RoutedEventArgs e)
    {
        ShowTool(6);
    }

    private void ShowTool(int toolIndex)
    {
        BoundaryContentPanel.Visibility = toolIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PointReductionContentPanel.Visibility = toolIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Flatten3dContentPanel.Visibility = toolIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AutoDimensionContentPanel.Visibility = toolIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        QuickSearchContentPanel.Visibility = toolIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        AlignmentContentPanel.Visibility = toolIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        GCodeContentPanel.Visibility = toolIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        ToolSubtitleText.Text = toolIndex switch
        {
            0 => "Tạo đường bao theo biên dạng rời",
            1 => "Giảm point nhưng giữ nguyên hình học",
            2 => "Xoay mặt 3D nằm bệt trên mặt phẳng WCS XY",
            3 => "Tự động ghi và xóa kích thước nhiều chi tiết",
            4 => "Tìm nhanh chi tiết theo kích thước",
            5 => "Căn thẳng hàng và căn đều khoảng cách",
            _ => "Chuyển đường chạy dao GCODE thành hình thật"
        };
        CurrentToolBadgeText.Text = toolIndex switch
        {
            0 => "TẠO BIÊN",
            1 => "GIẢM POINT",
            2 => "3D → 2D",
            3 => "AUTO DIM",
            4 => "TÌM NHANH",
            5 => "CĂN CHỈNH",
            _ => "GCODE → HÌNH"
        };
        CurrentToolBadgeBorder.Background = BrushFromRgb(18, 111, 174);

        SetTabAppearance(BoundaryTabButton, active: toolIndex == 0, boundaryTab: true);
        SetTabAppearance(PointReductionTabButton, active: toolIndex == 1, boundaryTab: false);
        SetTabAppearance(Flatten3dTabButton, active: toolIndex == 2, boundaryTab: false);
        SetTabAppearance(AutoDimensionTabButton, active: toolIndex == 3, boundaryTab: false);
        SetTabAppearance(QuickSearchTabButton, active: toolIndex == 4, boundaryTab: false);
        SetTabAppearance(AlignmentTabButton, active: toolIndex == 5, boundaryTab: false);
        SetTabAppearance(GCodeTabButton, active: toolIndex == 6, boundaryTab: false);
        var activeTab = toolIndex switch
        {
            0 => BoundaryTabButton,
            1 => PointReductionTabButton,
            2 => Flatten3dTabButton,
            3 => AutoDimensionTabButton,
            4 => QuickSearchTabButton,
            5 => AlignmentTabButton,
            _ => GCodeTabButton
        };
        activeTab.BringIntoView();
        var activePanel = toolIndex switch
        {
            0 => (FrameworkElement)BoundaryContentPanel,
            1 => PointReductionContentPanel,
            2 => Flatten3dContentPanel,
            3 => AutoDimensionContentPanel,
            4 => QuickSearchContentPanel,
            5 => AlignmentContentPanel,
            _ => GCodeContentPanel
        };
        AnimateToolPanel(activePanel);
        AnimateToolHeader();
        ToolScrollViewer.ScrollToTop();
    }

    private static void AnimateToolPanel(FrameworkElement panel)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 0.0;
        var translate = new TranslateTransform(0.0, 8.0);
        panel.RenderTransform = translate;
        panel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easing });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = easing });
    }

    private void AnimateToolHeader()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        CurrentToolBadgeBorder.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1.0, duration) { EasingFunction = easing });
        ToolSubtitleText.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.25, 1.0, duration) { EasingFunction = easing });
    }

    private static void SetTabAppearance(Button button, bool active, bool boundaryTab)
    {
        var activeColor = boundaryTab
            ? Color.FromRgb(18, 111, 174)
            : Color.FromRgb(154, 53, 181);
        button.Background = new SolidColorBrush(
            active ? activeColor : Color.FromRgb(38, 50, 64));
        button.BorderBrush = new SolidColorBrush(
            active
                ? Color.FromRgb(
                    (byte)Math.Max(activeColor.R - 32, 0),
                    (byte)Math.Max(activeColor.G - 32, 0),
                    (byte)Math.Max(activeColor.B - 32, 0))
                : Color.FromRgb(16, 24, 32));
        if (button.Content is TextBlock label)
        {
            label.Foreground = new SolidColorBrush(
                active ? Colors.White : Color.FromRgb(145, 164, 183));
            label.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
        }
    }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private void ImportGCode_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = BoundarySettings.Current;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn file GCODE cần chuyển thành hình CAD",
            Filter = "GCODE (*.nc;*.tap;*.gcode;*.cnc)|*.nc;*.tap;*.gcode;*.cnc|Tất cả file (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(settings.GCodeFilePath) && File.Exists(settings.GCodeFilePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(settings.GCodeFilePath);
            dialog.FileName = Path.GetFileName(settings.GCodeFilePath);
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        GCodeFileNameText.Text = Path.GetFileName(dialog.FileName);
        GCodeFileNameText.ToolTip = dialog.FileName;
        settings.CancelGapScanRequest();
        settings.CancelPointReductionRequest();
        settings.CancelFlatten3dRequest();
        settings.CancelAutoDimensionRequest();
        settings.CancelAutoDeleteDimensionRequest();
        settings.CancelDimensionSearchRequest();
        settings.CancelAlignmentRequest();
        settings.RequestGCodeImport(dialog.FileName);
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void RunBoundary_OnClick(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showMessage: true))
        {
            return;
        }

        BoundarySettings.Current.CancelGapScanRequest();
        BoundarySettings.Current.CancelPointReductionRequest();
        BoundarySettings.Current.CancelFlatten3dRequest();
        BoundarySettings.Current.CancelAutoDimensionRequest();
        BoundarySettings.Current.CancelAutoDeleteDimensionRequest();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void ScanGaps_OnClick(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showMessage: true))
        {
            return;
        }

        BoundarySettings.Current.RequestGapScan();
        BoundarySettings.Current.CancelPointReductionRequest();
        BoundarySettings.Current.CancelFlatten3dRequest();
        BoundarySettings.Current.CancelAutoDimensionRequest();
        BoundarySettings.Current.CancelAutoDeleteDimensionRequest();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void ReducePoints_OnClick(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showMessage: true))
        {
            return;
        }

        BoundarySettings.Current.CancelGapScanRequest();
        BoundarySettings.Current.CancelFlatten3dRequest();
        BoundarySettings.Current.CancelAutoDimensionRequest();
        BoundarySettings.Current.CancelAutoDeleteDimensionRequest();
        BoundarySettings.Current.RequestPointReduction();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void Flatten3d_OnClick(object sender, RoutedEventArgs e)
    {
        BoundarySettings.Current.CancelGapScanRequest();
        BoundarySettings.Current.CancelPointReductionRequest();
        BoundarySettings.Current.CancelAutoDimensionRequest();
        BoundarySettings.Current.CancelAutoDeleteDimensionRequest();
        BoundarySettings.Current.RequestFlatten3d();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void AutoDimension_OnClick(object sender, RoutedEventArgs e)
    {
        BoundarySettings.Current.CancelGapScanRequest();
        BoundarySettings.Current.CancelPointReductionRequest();
        BoundarySettings.Current.CancelFlatten3dRequest();
        BoundarySettings.Current.CancelAutoDeleteDimensionRequest();
        BoundarySettings.Current.RequestAutoDimension();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void AutoDeleteDimension_OnClick(object sender, RoutedEventArgs e)
    {
        BoundarySettings.Current.CancelGapScanRequest();
        BoundarySettings.Current.CancelPointReductionRequest();
        BoundarySettings.Current.CancelFlatten3dRequest();
        BoundarySettings.Current.CancelAutoDimensionRequest();
        BoundarySettings.Current.RequestAutoDeleteDimension();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void SearchDimensions_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPositive(DimensionSearchWidthBox.Text, out var width) ||
            !TryReadPositive(DimensionSearchHeightBox.Text, out var height))
        {
            MessageBox.Show(
                "Hãy nhập chiều rộng và chiều cao lớn hơn 0.",
                "TMH CAD Tools",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var settings = BoundarySettings.Current;
        settings.DimensionSearchWidth = width;
        settings.DimensionSearchHeight = height;
        settings.CancelGapScanRequest();
        settings.CancelPointReductionRequest();
        settings.CancelFlatten3dRequest();
        settings.CancelAutoDimensionRequest();
        settings.CancelAutoDeleteDimensionRequest();
        settings.RequestDimensionSearch();
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private void AlignHorizontal_OnClick(object sender, RoutedEventArgs e) =>
        RunAlignment(AlignmentOperation.AlignHorizontal, 0.0);

    private void AlignVertical_OnClick(object sender, RoutedEventArgs e) =>
        RunAlignment(AlignmentOperation.AlignVertical, 0.0);

    private void DistributeHorizontal_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadNonNegative(AlignmentSpacingBox.Text, out var spacing))
        {
            ShowAlignmentSpacingWarning();
            return;
        }

        RunAlignment(AlignmentOperation.DistributeHorizontal, spacing);
    }

    private void DistributeVertical_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadNonNegative(AlignmentSpacingBox.Text, out var spacing))
        {
            ShowAlignmentSpacingWarning();
            return;
        }

        RunAlignment(AlignmentOperation.DistributeVertical, spacing);
    }

    private static void ShowAlignmentSpacingWarning()
    {
        MessageBox.Show(
            "Khoảng cách phải là số lớn hơn hoặc bằng 0.",
            "TMH CAD Tools",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void RunAlignment(AlignmentOperation operation, double spacing)
    {
        var settings = BoundarySettings.Current;
        settings.CancelGapScanRequest();
        settings.CancelPointReductionRequest();
        settings.CancelFlatten3dRequest();
        settings.CancelAutoDimensionRequest();
        settings.CancelAutoDeleteDimensionRequest();
        settings.CancelDimensionSearchRequest();
        settings.RequestAlignment(operation, spacing);
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        document?.SendStringToExecute("CQBD ", true, false, false);
    }

    private bool SaveSettings(bool showMessage)
    {
        if (!TryReadPositive(GapToleranceBox.Text, out var gapTolerance) ||
            !TryReadPositive(SplineToleranceBox.Text, out var splineTolerance) ||
            !TryReadPositive(GapAngleBox.Text, out var gapAngle) || gapAngle > 180.0)
        {
            if (showMessage)
            {
                MessageBox.Show(
                    "Hãy nhập số dương. Góc nối phải từ 0 đến 180 độ.",
                    "CQ CAD Tools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        var settings = BoundarySettings.Current;
        settings.GapTolerance = gapTolerance;
        settings.SplineTolerance = splineTolerance;
        settings.MaximumGapAngleDegrees = gapAngle;
        settings.PointReductionMode = ReadTag(PointReductionModeBox) == nameof(PointReductionMode.Exact)
            ? PointReductionMode.Exact
            : PointReductionMode.Optimize;
        settings.OutputCurveType = ReadTag(OutputTypeBox) == nameof(OutputCurveType.Spline)
            ? OutputCurveType.Spline
            : OutputCurveType.Polyline;
        settings.OutputColorIndex = short.TryParse(
            ReadTag(OutputColorBox),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var colorIndex)
            ? colorIndex
            : (short)256;
        LoadSettings();
        return true;
    }

    private static bool TryReadPositive(string text, out double value)
    {
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
               value > 0.0;
    }

    private static bool TryReadNonNegative(string text, out double value)
    {
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
               value >= 0.0;
    }

    private static string? ReadTag(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    }

    private static void SelectItemByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }
}
