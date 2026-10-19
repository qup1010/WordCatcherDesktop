using System;
using Velopack;

namespace WordCatcher.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Update/install hooks must run before WPF creates windows or the app takes its mutex.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
