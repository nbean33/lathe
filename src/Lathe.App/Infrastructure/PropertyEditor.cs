using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lathe.Core;
using Lathe.Core.Model;
using Lathe.Core.Threads;

namespace Lathe.App.Infrastructure;

public enum UnitKind { None, Length, Feed, Angle, Rpm, Fraction, Percent }

/// <summary>Human labels, units and tips for model properties (keyed by property name).</summary>
public static class Labels
{
    private static readonly Dictionary<string, (string Label, UnitKind Unit, string Tip)> Map = new()
    {
        ["Diameter"] = ("Diameter Ø", UnitKind.Length, "True diameter."),
        ["Length"] = ("Length", UnitKind.Length, "Length along Z."),
        ["StickOut"] = ("Stick-out from jaws", UnitKind.Length, "How far the raw bar protrudes from the chuck jaws."),
        ["FaceAllowance"] = ("Face allowance", UnitKind.Length, "Material to face off the end of the bar."),
        ["BoreDiameter"] = ("Tube bore Ø (0 = solid)", UnitKind.Length, "Inside diameter if the stock is tube."),
        ["StartDiameter"] = ("Start Ø (right end)", UnitKind.Length, ""),
        ["EndDiameter"] = ("End Ø (left end)", UnitKind.Length, ""),
        ["Radius"] = ("Radius", UnitKind.Length, ""),
        ["Convex"] = ("Convex (ball); untick for a cove", UnitKind.None, ""),
        ["StartEdge"] = ("Right edge", UnitKind.None, "Chamfer or radius on the corner at the right end."),
        ["EndEdge"] = ("Left edge", UnitKind.None, "Chamfer or radius on the corner at the left end."),
        ["Position"] = ("Distance from face", UnitKind.Length, "Measured from the finished face (Z0) to the right end of the feature."),
        ["Width"] = ("Width", UnitKind.Length, ""),
        ["Spec"] = ("Thread", UnitKind.None, "Pick from the table or choose Custom."),
        ["Internal"] = ("Internal", UnitKind.None, ""),
        ["CustomMajorDia"] = ("Custom major Ø", UnitKind.Length, "Only used when Thread = Custom."),
        ["CustomPitch"] = ("Custom pitch", UnitKind.Length, "Only used when Thread = Custom."),
        ["Number"] = ("Tool number", UnitKind.None, "T number (1-99)."),
        ["Name"] = ("Name", UnitKind.None, ""),
        ["Type"] = ("Type", UnitKind.None, ""),
        ["Shape"] = ("Insert shape", UnitKind.None, "C=80°, D=55°, V=35°, T=60°, W=80° trigon, Round."),
        ["Material"] = ("Tool material", UnitKind.None, ""),
        ["NoseRadius"] = ("Nose radius", UnitKind.Length, "Corner radius of the insert."),
        ["ApproachAngle"] = ("Approach angle", UnitKind.Angle, "Holder lead angle: 95° for most C holders, 93° for D/V."),
        ["InsertSize"] = ("Insert size (IC)", UnitKind.Length, ""),
        ["MaxDepth"] = ("Max reach / depth", UnitKind.Length, "Groove/part depth, boring depth, or drill flute length."),
        ["MinBoreDia"] = ("Min bore Ø", UnitKind.Length, "Smallest hole this bar fits in."),
        ["PointAngle"] = ("Point angle", UnitKind.Angle, ""),
        ["OffsetX"] = ("Offset X (Ø)", UnitKind.Length, "Relative to the master tool, in diameter. Learn it on the Machine tab."),
        ["OffsetZ"] = ("Offset Z", UnitKind.Length, "Relative to the master tool. Learn it on the Machine tab."),
        ["Notes"] = ("Notes", UnitKind.None, ""),
        ["Enabled"] = ("Enabled", UnitKind.None, ""),
        ["ToolNumber"] = ("Tool", UnitKind.None, ""),
        ["Rpm"] = ("Spindle rpm (0 = auto)", UnitKind.Rpm, "Leave 0 to use the material table."),
        ["Feed"] = ("Feed per rev (0 = auto)", UnitKind.Feed, "Leave 0 to use the material table."),
        ["Coolant"] = ("Coolant (M8)", UnitKind.None, ""),
        ["DepthOfCut"] = ("Depth of cut", UnitKind.Length, "Per pass (radial for turning, along Z for facing)."),
        ["FinishPass"] = ("Finish pass", UnitKind.Length, "Thickness of the last facing pass."),
        ["Side"] = ("Side", UnitKind.None, ""),
        ["Stage"] = ("Stage", UnitKind.None, ""),
        ["Allowance"] = ("Finish allowance", UnitKind.Length, "Material left for the finish pass."),
        ["AllowPockets"] = ("Rough into hollows", UnitKind.None, "Ramp into coves/necks when the tool can."),
        ["Depth"] = ("Depth (full Ø)", UnitKind.Length, "Depth of the full diameter; the point goes further."),
        ["PeckDepth"] = ("Peck depth (0 = none)", UnitKind.Length, ""),
        ["ChipBreakRetract"] = ("Chip-break retract", UnitKind.Length, ""),
        ["FullRetractEvery"] = ("Full retract every N pecks", UnitKind.None, ""),
        ["StepOver"] = ("Step-over (× width)", UnitKind.Fraction, ""),
        ["FinishAllowance"] = ("Finish allowance", UnitKind.Length, ""),
        ["FirstDepth"] = ("First pass depth", UnitKind.Length, "Radial. Later passes get smaller (constant chip area)."),
        ["MinDepth"] = ("Minimum pass depth", UnitKind.Length, ""),
        ["SpringPasses"] = ("Spring passes", UnitKind.None, "Extra passes at full depth."),
        ["InfeedAngle"] = ("Infeed angle", UnitKind.Angle, "29.5° flank infeed, 0 = straight in."),
        ["SlowBelowPercent"] = ("Slow down below % of Ø", UnitKind.Percent, ""),
        ["SlowFeedFactor"] = ("Slow feed factor", UnitKind.Fraction, ""),
    };

    public static (string Label, UnitKind Unit, string Tip) Get(string prop, Type owner)
    {
        if (owner == typeof(PartOffOp) && prop == "EndDiameter") return ("Stop at X (Ø)", UnitKind.Length, "Slightly negative cuts through; positive leaves a pip.");
        return Map.TryGetValue(prop, out var v) ? v : (prop, UnitKind.None, "");
    }
}

/// <summary>
/// Builds a simple form for any model object: numbers, booleans, enums, strings and EdgeBreaks.
/// Lengths are shown and entered in the project's units; the model always stores mm.
/// </summary>
public sealed class PropertyEditor : ContentControl
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(object), typeof(PropertyEditor),
        new PropertyMetadata(null, (d, _) => ((PropertyEditor)d).Rebuild()));
    public object? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(nameof(Units), typeof(UnitSystem), typeof(PropertyEditor),
        new PropertyMetadata(UnitSystem.Mm, (d, _) => ((PropertyEditor)d).Rebuild()));
    public UnitSystem Units { get => (UnitSystem)GetValue(UnitsProperty); set => SetValue(UnitsProperty, value); }

    /// <summary>Items for the ToolNumber combo (ToolDefs).</summary>
    public static readonly DependencyProperty ToolsProperty = DependencyProperty.Register(nameof(Tools), typeof(IEnumerable), typeof(PropertyEditor),
        new PropertyMetadata(null, (d, _) => ((PropertyEditor)d).Rebuild()));
    public IEnumerable? Tools { get => (IEnumerable?)GetValue(ToolsProperty); set => SetValue(ToolsProperty, value); }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Preferred field order; anything else follows in declaration order.</summary>
    private static readonly string[] Order =
    {
        "Enabled", "ToolNumber", "Number", "Name", "Type", "Shape", "Material", "Side", "Stage", "Spec", "Internal",
        "Diameter", "StartDiameter", "EndDiameter", "Position", "Length", "Width", "Radius", "Convex", "StartEdge", "EndEdge",
    };

    public void Rebuild()
    {
        var t = Target;
        if (t is null) { Content = new TextBlock { Text = "Select an item to edit it.", Foreground = Brushes.Gray, Margin = new Thickness(4) }; return; }
        var grid = new Grid { Margin = new Thickness(2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        var props = t.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select((p, i) => (p, order: Array.IndexOf(Order, p.Name) is var k and >= 0 ? k : 100 + i))
            .OrderBy(x => x.order).Select(x => x.p);
        foreach (var p in props)
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            bool isEdge = p.PropertyType == typeof(EdgeBreak);
            if (!isEdge && (!p.CanWrite || p.GetSetMethod() is null)) continue;
            var pt = p.PropertyType;
            if (!isEdge && !(pt == typeof(double) || pt == typeof(int) || pt == typeof(bool) || pt == typeof(string) || pt.IsEnum)) continue;
            var (label, unit, tip) = Labels.Get(p.Name, t.GetType());
            FrameworkElement editor = isEdge ? EdgeEditor((EdgeBreak)p.GetValue(t)!) : MakeEditor(t, p, unit);
            if (!string.IsNullOrEmpty(tip)) editor.ToolTip = tip;
            string suffix = unit switch
            {
                UnitKind.Length => $" ({Lathe.Core.Units.Suffix(Units)})",
                UnitKind.Feed => Units == UnitSystem.Mm ? " (mm/rev)" : " (in/rev)",
                UnitKind.Angle => " (°)",
                _ => "",
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lb = new TextBlock { Text = label + suffix, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3), TextWrapping = TextWrapping.Wrap, ToolTip = string.IsNullOrEmpty(tip) ? null : tip };
            Grid.SetRow(lb, row); grid.Children.Add(lb);
            Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); grid.Children.Add(editor);
            row++;
        }
        Content = grid;
    }

    private double Scale(UnitKind u) => (u is UnitKind.Length or UnitKind.Feed) && Units == UnitSystem.Inch ? 25.4 : 1;

    private FrameworkElement MakeEditor(object t, PropertyInfo p, UnitKind unit)
    {
        var pt = p.PropertyType;
        if (pt == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)p.GetValue(t)!, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
            cb.Click += (_, _) => p.SetValue(t, cb.IsChecked == true);
            return cb;
        }
        if (pt.IsEnum)
        {
            var combo = new ComboBox { ItemsSource = Enum.GetValues(pt), SelectedItem = p.GetValue(t), Margin = new Thickness(0, 2, 0, 2) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is not null) p.SetValue(t, combo.SelectedItem); };
            return combo;
        }
        if (p.Name == "ToolNumber" && Tools is not null)
        {
            var combo = new ComboBox { ItemsSource = Tools, DisplayMemberPath = "Summary", SelectedValuePath = "Number", SelectedValue = p.GetValue(t), Margin = new Thickness(0, 2, 0, 2) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is int n) p.SetValue(t, n); };
            return combo;
        }
        if (p.Name == "Spec" && t is ThreadFeature)
        {
            var names = ThreadTable.All.Select(x => x.Name).Append("Custom").ToList();
            var combo = new ComboBox { ItemsSource = names, IsEditable = true, Text = (string)p.GetValue(t)!, Margin = new Thickness(0, 2, 0, 2) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) p.SetValue(t, s); };
            combo.LostFocus += (_, _) => p.SetValue(t, combo.Text);
            return combo;
        }
        var tb = new TextBox { Margin = new Thickness(0, 2, 0, 2), VerticalContentAlignment = VerticalAlignment.Center };
        if (pt == typeof(string)) { tb.Text = (string?)p.GetValue(t) ?? ""; tb.LostFocus += (_, _) => p.SetValue(t, tb.Text); return tb; }

        double scale = Scale(unit);
        string Show() => pt == typeof(int) ? ((int)p.GetValue(t)!).ToString(Inv)
            : Math.Round((double)p.GetValue(t)! / scale, scale > 1 ? 5 : 4).ToString("0.#####", Inv);
        tb.Text = Show();
        void Commit()
        {
            var s = tb.Text.Trim().Replace(',', '.');
            bool ok;
            if (pt == typeof(int)) { ok = int.TryParse(s, NumberStyles.Integer, Inv, out var iv); if (ok) p.SetValue(t, iv); }
            else
            {
                ok = TryEval(s, out var dv) && double.IsFinite(dv);
                if (ok) p.SetValue(t, dv * scale);
            }
            tb.BorderBrush = ok ? SystemColors.ControlDarkBrush : Brushes.Red;
            tb.ToolTip = ok ? tb.ToolTip : "Not a number";
            if (ok) tb.Text = Show();
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return tb;
    }

    private FrameworkElement EdgeEditor(EdgeBreak e)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var kind = new ComboBox { ItemsSource = Enum.GetValues(typeof(EdgeKind)), SelectedItem = e.Kind, Width = 90 };
        var size = new TextBox { Text = Math.Round(e.Size / Scale(UnitKind.Length), 5).ToString("0.#####", Inv), Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        kind.SelectionChanged += (_, _) => { if (kind.SelectedItem is EdgeKind k) { e.Kind = k; if (k != EdgeKind.None && e.Size <= 0) { e.Size = 0.5; size.Text = Math.Round(0.5 / Scale(UnitKind.Length), 5).ToString("0.#####", Inv); } } };
        void Commit() { if (TryEval(size.Text.Trim().Replace(',', '.'), out var v) && v >= 0) { e.Size = v * Scale(UnitKind.Length); size.BorderBrush = SystemColors.ControlDarkBrush; } else size.BorderBrush = Brushes.Red; }
        size.LostFocus += (_, _) => Commit();
        size.KeyDown += (_, a) => { if (a.Key == Key.Enter) Commit(); };
        DockPanel.SetDock(kind, Dock.Left);
        panel.Children.Add(kind); panel.Children.Add(size);
        return panel;
    }

    /// <summary>Accepts plain numbers and simple fractions like "3/8" or "1 1/4".</summary>
    public static bool TryEval(string s, out double v)
    {
        v = 0;
        if (double.TryParse(s, NumberStyles.Float, Inv, out v)) return true;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = 0;
        foreach (var part in parts)
        {
            var f = part.Split('/');
            if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, Inv, out var a) && double.TryParse(f[1], NumberStyles.Float, Inv, out var b) && b != 0) total += a / b;
            else if (double.TryParse(part, NumberStyles.Float, Inv, out var w)) total += w;
            else return false;
        }
        v = total;
        return parts.Length > 0;
    }
}
