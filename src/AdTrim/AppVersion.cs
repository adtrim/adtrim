namespace AdTrim;

/// <summary>
/// Single source of truth for the user-visible app version. Referenced by
/// the status bar (MainWindow.xaml), the About dialog (AboutDialog.xaml),
/// and the export metadata comment (ExportService). Bump <see cref="Numeric"/>
/// per build; <see cref="Display"/> picks up the "v" prefix automatically.
/// </summary>
public static class AppVersion
{
    public const string Numeric = "1.0.41";
    public const string Display = "v" + Numeric;
}
