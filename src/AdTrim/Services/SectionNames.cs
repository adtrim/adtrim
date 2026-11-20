using System.Globalization;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

/// <summary>
/// Names for segments the user has not named. Kept segments are numbered in order, skipping
/// removed segments and segments with a name of their own, so the chapters of an export read
/// Part 1, Part 2, Part 3 whatever was cut out between them.
/// </summary>
public static class SectionNames
{
    public const string Placeholder = "{n}";
    public const string DefaultTemplate = "Part {n}";
    /// <summary>Shown on a removed segment that has no name. It is never exported.</summary>
    public const string Removed = "Removed";
    public const int MaximumLength = 40;

    /// <summary>A usable template has the number placeholder exactly once and fits a chapter name.</summary>
    public static bool IsValid(string? template)
    {
        if (template is null) return false;
        var text = template.Trim();
        return text.Length <= MaximumLength && !text.Any(char.IsControl)
            && text.Split(Placeholder).Length == 2;
    }

    public static string Normalize(string? template) => IsValid(template) ? template!.Trim() : DefaultTemplate;

    public static string Format(string? template, int number)
        => Normalize(template).Replace(Placeholder, number.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// True for a name that is only the template (or the built-in "Part N") with a number:
    /// one typed by hand or imported, which the automatic numbering would otherwise never touch.
    /// </summary>
    public static bool IsNumbered(string? name, string? template)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Matches(name.Trim(), Normalize(template)) || Matches(name.Trim(), DefaultTemplate);
    }

    private static bool Matches(string name, string template)
    {
        var parts = template.Split(Placeholder);
        return Regex.IsMatch(name, "^" + Regex.Escape(parts[0]) + "[0-9]{1,6}" + Regex.Escape(parts[1]) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
