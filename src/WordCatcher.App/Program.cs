using System;
namespace WordCatcher.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
