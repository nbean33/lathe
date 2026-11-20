using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lathe.Core.Project;
using Lathe.Core.Tools;

namespace Lathe.App.Infrastructure;

public static class CrashLog
{
    public static void Write(Exception e)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDir, $"log-{DateTime.Now:yyyyMMdd}.txt");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {e}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}

/// <summary>Modal prompt shown at every tool change. The spindle is already stopped when it appears.</summary>
public static class ToolChangeDialog
{
    public enum Result { UseOffsets, AlreadySet, Abort }

    public static Result Ask(Window? owner, ToolDef t, bool workZeroSet)
    {
        var result = Result.Abort;
        var w = new Window
        {
            Title = $"Tool change: T{t.Number}", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, Owner = owner,
            Topmost = true,
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = $"Install T{t.Number}: {t.Name}", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = Reference(t), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = $"Stored offsets: X {t.OffsetX:0.###} (Ø), Z {t.OffsetZ:0.###}", Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray });
        sp.Children.Add(new TextBlock { Text = "The spindle is stopped. Lock the tool post, then choose how to continue.", Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap });
        if (!workZeroSet)
            sp.Children.Add(new TextBlock { Text = "Work zero isn't set in this session, so offsets can't be applied automatically.", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button B(string text, Result r, bool enabled = true)
        {
            var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), IsEnabled = enabled };
            b.Click += (_, _) => { result = r; w.DialogResult = true; };
            return b;
        }
        var use = B("Installed – apply stored offsets", Result.UseOffsets, workZeroSet);
        use.IsDefault = workZeroSet;
        row.Children.Add(use);
        row.Children.Add(B("Installed – G54 already correct", Result.AlreadySet));
        var abort = B("Abort program", Result.Abort); abort.IsCancel = true;
        row.Children.Add(abort);
        sp.Children.Add(row);
        w.Content = sp;
        w.ShowDialog();
        return result;
    }

    public static string Reference(ToolDef t) => t.Type switch
    {
        ToolType.Turning => "Reference point: bottom of the nose for X, left side of the nose for Z.",
        ToolType.Boring => "Reference point: top of the nose (inside the bore) for X, left side of the nose for Z.",
        ToolType.Grooving or ToolType.PartOff => "Reference point: front edge for X, RIGHT side of the blade for Z. Blade square to the work, on center height.",
        ToolType.ThreadExternal or ToolType.ThreadInternal => "Reference point: the tip. Square the tool to the work with a fishtail gauge.",
        _ => "Reference point: the drill tip, on the centerline.",
    };
}
