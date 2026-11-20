using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;

namespace Lathe.App.Infrastructure;

/// <summary>
/// Watches an object graph (INotifyPropertyChanged + INotifyCollectionChanged) and raises a single
/// debounced Changed event on the UI thread. Used for dirty tracking, autosave and live validation.
/// </summary>
public sealed class ChangeTracker : IDisposable
{
    private readonly HashSet<object> _watched = new(ReferenceEqualityComparer.Instance);
    private readonly DispatcherTimer _debounce;
    private object? _root;
    public event Action? Changed;

    public ChangeTracker(TimeSpan delay)
    {
        _debounce = new DispatcherTimer { Interval = delay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
    }

    public void Attach(object root) { Detach(); _root = root; Walk(root); }

    public void Detach()
    {
        foreach (var o in _watched)
        {
            if (o is INotifyPropertyChanged n) n.PropertyChanged -= OnProp;
            if (o is INotifyCollectionChanged c) c.CollectionChanged -= OnColl;
        }
        _watched.Clear();
    }

    private void Walk(object? o)
    {
        if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !_watched.Add(o)) return;
        if (o is INotifyPropertyChanged n) n.PropertyChanged += OnProp;
        if (o is INotifyCollectionChanged c) c.CollectionChanged += OnColl;
        if (o is IEnumerable e and not string) foreach (var item in e) Walk(item);
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            var t = p.PropertyType;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t.IsValueType) continue;
            if (p.Name is "Summary") continue;
            try { Walk(p.GetValue(o)); } catch { /* computed property */ }
        }
    }

    private void OnProp(object? s, PropertyChangedEventArgs e) { if (e.PropertyName != "Summary") Kick(); }
    private void OnColl(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (_root is not null) { Detach(); Walk(_root); }
        Kick();
    }
    private void Kick() { _debounce.Stop(); _debounce.Start(); }
    public void Dispose() { Detach(); _debounce.Stop(); }
}
