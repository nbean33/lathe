using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Lathe.Core;

public enum UnitSystem { Mm, Inch }

/// <summary>Everything is stored in millimetres; units only matter for display, entry and G-code output.</summary>
public static class Units
{
    public const double MmPerInch = 25.4;
    public static double ToDisplay(double mm, UnitSystem u) => u == UnitSystem.Mm ? mm : mm / MmPerInch;
    public static double FromDisplay(double v, UnitSystem u) => u == UnitSystem.Mm ? v : v * MmPerInch;
    public static string Suffix(UnitSystem u) => u == UnitSystem.Mm ? "mm" : "in";
    public static int Decimals(UnitSystem u) => u == UnitSystem.Mm ? 3 : 4;
    public static string Format(double mm, UnitSystem u) =>
        ToDisplay(mm, u).ToString("0." + new string('#', Decimals(u)), CultureInfo.InvariantCulture);
}

public enum Severity { Info, Warning, Error }

/// <summary>A validation or generation finding shown to the user.</summary>
public sealed record Issue(Severity Severity, string Message, string? Where = null)
{
    public static Issue Err(string m, string? w = null) => new(Severity.Error, m, w);
    public static Issue Warn(string m, string? w = null) => new(Severity.Warning, m, w);
    public static Issue Note(string m, string? w = null) => new(Severity.Info, m, w);
    public override string ToString() => Where is null ? $"{Severity}: {Message}" : $"{Severity}: {Where}: {Message}";
}

public static class IssueExt
{
    public static bool HasErrors(this IEnumerable<Issue> issues) => issues.Any(i => i.Severity == Severity.Error);
}

/// <summary>Minimal INotifyPropertyChanged base so the WPF UI can bind directly to the model.</summary>
public abstract class ModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }
    protected void OnChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name != nameof(Summary)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
    /// <summary>One-line description for lists.</summary>
    [JsonIgnore] public virtual string Summary => GetType().Name;
    public override string ToString() => Summary;
}

public static class Num
{
    public static string F(double v, int dec = 3) => Math.Round(v, dec).ToString("0." + new string('#', dec), CultureInfo.InvariantCulture);
}
