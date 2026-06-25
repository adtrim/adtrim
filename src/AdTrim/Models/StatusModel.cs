namespace AdTrim.Models;

public enum StatusKind { Info, Success, Warning, Danger }

/// <summary>
/// Inline-banner descriptor rendered above the transport bar. `null` on the
/// view model hides the row entirely. Action labels are paired with handler
/// names so XAML can wire to commands without binding to delegates.
/// </summary>
public sealed record BannerInfo(
    StatusKind Kind,
    string Title,
    string Body,
    IReadOnlyList<BannerAction> Actions);

public sealed record BannerAction(string Label, Action Invoke, bool IsPrimary = false);
