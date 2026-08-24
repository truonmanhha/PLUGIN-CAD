using CuocSongQuanhTa.CadTools.Commands;
using CuocSongQuanhTa.CadTools.UI;
using CuocSongQuanhTa.CadLoader;

namespace CuocSongQuanhTa.CadTools;

public sealed class CadToolsModule : ICadToolsModule
{
    private readonly BoundaryCommands _commands = new();
    private bool _paletteWasShown;

    public void ShowTools()
    {
        _paletteWasShown = true;
        PaletteHost.Show();
    }

    public void RunBoundary()
    {
        _commands.CreateBoundaryPolyline();
    }

    public void ConfigureOutput()
    {
        _commands.ConfigureOutput();
    }

    public void Dispose()
    {
        if (_paletteWasShown)
        {
            PaletteHost.Dispose();
        }
    }
}
