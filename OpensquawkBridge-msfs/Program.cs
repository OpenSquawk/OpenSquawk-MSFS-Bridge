#nullable enable
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var manager = new BridgeManager();
        using var mainForm = new MainForm(manager);

        Application.Run(mainForm);
    }
}
