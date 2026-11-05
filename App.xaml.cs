using System.Windows;

namespace ProfileRefresher;

public partial class App : Application
{
    public App()
    {
#pragma warning disable WPF0001
        // Native Windows 11 look (light/dark follows the system setting).
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    }
}
