using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace CuocSongQuanhTa.CadLoader;

public sealed class LoaderCommands
{
    [CommandMethod("TMH", CommandFlags.Session)]
    public void ShowToolsShort()
    {
        ShowTools();
    }

    [CommandMethod("CQTOOLS", CommandFlags.Session)]
    public void ShowTools()
    {
        Invoke(module => module.ShowTools());
    }

    [CommandMethod("CQBD", CommandFlags.Modal | CommandFlags.Redraw)]
    public void RunBoundary()
    {
        Invoke(module => module.RunBoundary());
    }

    [CommandMethod("CQBDOUTPUT", CommandFlags.Modal)]
    public void ConfigureOutput()
    {
        Invoke(module => module.ConfigureOutput());
    }

    [CommandMethod("CQRELOAD", CommandFlags.Session)]
    public void Reload()
    {
        var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ModuleHost.Reload(out var error))
        {
            editor?.WriteMessage("\nDa nap lai module moi. Khong can khoi dong lai AutoCAD.");
        }
        else
        {
            editor?.WriteMessage($"\nCQRELOAD: {error}");
        }
    }

    private static void Invoke(Action<ICadToolsModule> action)
    {
        var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (!ModuleHost.TryGetModule(out var module, out var error) || module is null)
        {
            editor?.WriteMessage($"\nCQ Loader: {error}");
            return;
        }

        try
        {
            action(module);
        }
        catch (System.Exception exception)
        {
            editor?.WriteMessage($"\nCQ Tools: {exception.Message}");
        }
    }
}
