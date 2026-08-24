using Autodesk.AutoCAD.Windows;
using System.Drawing;

namespace CuocSongQuanhTa.CadTools.UI;

internal static class PaletteHost
{
    private static readonly Guid PaletteId = new("9B48B495-6C6B-43F1-86D2-9D5736120C57");
    private static PaletteSet? _palette;

    public static void Show()
    {
        if (_palette is null)
        {
            _palette = new PaletteSet("CUOC SONG QUANH TA - CAD TOOLS", PaletteId)
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.ShowPropertiesMenu,
                MinimumSize = new Size(330, 520),
                Size = new Size(370, 720),
                DockEnabled = DockSides.Left | DockSides.Right
            };
            _palette.AddVisual("Tao bien", new BoundaryPaletteView());
        }

        _palette.Visible = true;
    }

    public static void Dispose()
    {
        _palette?.Dispose();
        _palette = null;
    }
}
