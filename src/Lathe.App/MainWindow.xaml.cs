using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lathe.App.Infrastructure;
using Lathe.App.ViewModels;
using Lathe.Core.Project;

namespace Lathe.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _keyJogging, _scrollQueued;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.Notify += (t, m) => MessageBox.Show(this, m, t, MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.PropertyChanged += OnVmChanged;
        _vm.MachineVm.PropertyChanged += OnMachineChanged;
        _vm.MachineVm.ConsoleLines.CollectionChanged += (_, _) =>
        {
            // Never touch the list inside the change notification itself; scroll once things settle.
            if (_scrollQueued) return;
            _scrollQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                _scrollQueued = false;
                if (ConsoleList.Items.Count > 0) ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
            });
        };
        App.FatalUiError += () => { if (_vm.MachineVm.Running) _vm.MachineVm.FeedHoldCommand.Execute(null); };

        foreach (var t in Templates.All)
        {
            var mi = new MenuItem { Header = t.Name };
            string name = t.Name;
            mi.Click += (_, _) => _vm.NewFromTemplateCommand.Execute(name);
            TemplatesMenu.Items.Add(mi);
        }

        Loaded += (_, _) => OnLoaded();
        Deactivated += (_, _) => StopKeyJog();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Closing += OnClosing;
    }

    private void OnLoaded()
    {
        foreach (var w in _vm.PendingWarnings) MessageBox.Show(this, w, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) _vm.OpenPath(args[1]);
        else if (File.Exists(AppPaths.AutosaveFile)
                 && MessageBox.Show(this, "LatheStudio found unsaved work from last time. Restore it?", "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { _vm.Load(Store.LoadProject(AppPaths.AutosaveFile), null); _vm.Dirty = true; }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Could not restore", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        if (!_vm.Settings.FirstRunDone)
        {
            MessageBox.Show(this, FirstRunText, "Welcome to LatheStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            _vm.Settings.FirstRunDone = true; _vm.SaveSettings();
        }
    }

    private const string FirstRunText =
        "Quick start:\n\n" +
        "1 · Part: set the stock and add diameters, tapers, arcs, grooves and threads.\n" +
        "2 · Tools: describe the tools in your tool post.\n" +
        "3 · Program: add operations (or start from File > New from template) and press Generate. " +
        "The simulator checks for crashes, gouges and leftover material.\n" +
        "4 · Machine: connect (try the built-in simulator first), home, touch off X and Z, then run. " +
        "Use 'Air cut' for the first run of a new program.\n\n" +
        "Machine limits are in the data folder (Help menu): machine.json.";

    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.GcodeText)) Gcode.Text = _vm.GcodeText;
        if (e.PropertyName == nameof(MainViewModel.Cam)) { }
        if (e.PropertyName is nameof(MainViewModel.ProgramSummary)) Scrub.Maximum = Math.Max(0, (_vm.Cam?.Ops.Count ?? 1) - 1);
        if (e.PropertyName is nameof(MainViewModel.ProgramScene)) _vm.MachineVm.BuildSceneIfIdle();
    }

    private void OnMachineChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MachineViewModel.CurrentLine) && _vm.MachineVm.CurrentLine >= 0)
        {
            int line = _vm.MachineVm.CurrentLine + 1;
            if (line <= Gcode.Document.LineCount)
            {
                var dl = Gcode.Document.GetLineByNumber(line);
                Gcode.Select(dl.Offset, dl.Length);
                Gcode.ScrollToLine(line);
            }
        }
    }

    // ---------- Jogging ----------
    private static (char axis, int dir) Parse(object tag) { var s = (string)tag; return (s[0], s[1] == '+' ? 1 : -1); }
    private void Jog_Down(object sender, MouseButtonEventArgs e) { var (a, d) = Parse(((FrameworkElement)sender).Tag); _vm.MachineVm.JogStart(a, d); }
    private void Jog_Up(object sender, MouseButtonEventArgs e) => _vm.MachineVm.JogStop();
    private void Jog_Leave(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) _vm.MachineVm.JogStop(); }

    private bool MachineTabActive => Tabs.SelectedIndex == 3;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.MachineVm.Connected) { _vm.MachineVm.FeedHoldCommand.Execute(null); e.Handled = true; return; }
        if (!MachineTabActive || KeyJog.IsChecked != true || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        (char a, int d)? j = e.Key switch { Key.Left => ('Z', -1), Key.Right => ('Z', 1), Key.Up => ('X', 1), Key.Down => ('X', -1), _ => null };
        if (j is null) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        _keyJogging = true;
        _vm.MachineVm.JogStart(j.Value.a, j.Value.d);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _keyJogging) { StopKeyJog(); e.Handled = true; }
    }

    private void StopKeyJog() { _keyJogging = false; _vm.MachineVm.JogStop(); }

    private void Mdi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.MachineVm.SendMdiCommand.Execute(null); e.Handled = true; }
    }

    // ---------- Menu ----------
    private void RecentMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        foreach (var p in _vm.Settings.RecentFiles)
        {
            var mi = new MenuItem { Header = p };
            string path = p;
            mi.Click += (_, _) => { if (_vm.ConfirmDiscard()) _vm.OpenPath(path); };
            RecentMenu.Items.Add(mi);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void DataFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "LatheStudio 1.0\nSimple CAD, CAM and control for grblHAL lathes.\n\nG-code uses diameter mode (G7). Always prove a new program with an air cut.", "About", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Safety_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "• The software Stop/Hold buttons are not an E-stop. Wire a hardware E-stop that cuts motor/spindle power.\n" +
        "• Prove every new program with 'Air cut' (+20 mm in Z) before cutting metal.\n" +
        "• Home the machine and keep soft limits on so travel checks can protect you.\n" +
        "• Set work zero every time you re-chuck the stock; it is never carried over between sessions.\n" +
        "• Check that grblHAL is in lathe mode ($32=2). X values are diameters.\n" +
        "• Remove the chuck key, close guards, keep hands clear of rotating work and falling parts.\n" +
        "• If the connection drops or an alarm occurs, the machine state is unknown: re-home before continuing.",
        "Safety notes", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_vm.MachineVm.Running &&
            MessageBox.Show(this, "A program is running. Closing will stop the machine (feed hold, then reset). Close anyway?", "LatheStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        if (!_vm.ConfirmDiscard()) { e.Cancel = true; return; }
        _vm.SaveSettings();
        _vm.Dispose();
    }
}
