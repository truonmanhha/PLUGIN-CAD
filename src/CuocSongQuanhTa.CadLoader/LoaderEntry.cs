using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CuocSongQuanhTa.CadLoader.LoaderEntry))]
[assembly: CommandClass(typeof(CuocSongQuanhTa.CadLoader.LoaderCommands))]

namespace CuocSongQuanhTa.CadLoader;

public sealed class LoaderEntry : IExtensionApplication
{
    public void Initialize()
    {
        var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ModuleHost.TryLoad(out var error))
        {
            editor?.WriteMessage(
                "\nCuocSongQuanhTa Loader da nap. Dung TMH; khi cap nhat dung CQRELOAD.");
        }
        else
        {
            editor?.WriteMessage($"\nCQ Loader: {error}");
        }
    }

    public void Terminate()
    {
        ModuleHost.Unload();
    }
}
