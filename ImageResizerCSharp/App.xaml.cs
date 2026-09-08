using System;
using System.Linq;
using System.Windows;
using ImageResizerCSharp.Tests;

namespace ImageResizerCSharp
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Contains("--test"))
            {
                try
                {
                    bool success = await TestRunner.RunAllTestsAsync();
                    Environment.Exit(success ? 0 : 1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] Test exception: {ex}");
                    Environment.Exit(1);
                }
                return;
            }

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
