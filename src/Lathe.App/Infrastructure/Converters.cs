using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lathe.Core;

namespace Lathe.App.Infrastructure;

public sealed class SeverityBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Severity.Error => Brushes.Firebrick, Severity.Warning => Brushes.DarkOrange, _ => Brushes.SteelBlue,
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SeverityIcon : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch { Severity.Error => "✖", Severity.Warning => "⚠", _ => "ℹ" };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class CheckBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is ViewModels.CheckItem ci
        ? ci.Ok ? Brushes.SeaGreen : ci.Blocking ? Brushes.Firebrick : Brushes.DarkOrange
        : Brushes.Black;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
