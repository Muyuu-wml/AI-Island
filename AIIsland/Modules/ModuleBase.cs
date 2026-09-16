using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AIIsland.Modules;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public interface IIslandModule : INotifyPropertyChanged, IDisposable
{
    string Id { get; }
    bool IsVisible { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task RefreshAsync(ProcessSnapshot processes, CancellationToken cancellationToken);
}
public abstract class ModuleBase : Observable, IIslandModule
{
    private bool visible, busy, expanded = true;
    private string note = "";
    protected bool Disposed { get; private set; }
    public abstract string Id { get; }
    public bool IsVisible { get => visible; protected set { if (Set(ref visible, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public string Note { get => note; protected set => Set(ref note, value); }
    public virtual Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public abstract Task RefreshAsync(ProcessSnapshot processes, CancellationToken token);
    protected ICommand Action(Func<Task> run, Func<bool>? can = null) => new RelayCommand(async () => {
        IsBusy = true; Note = "";
        try { await run(); }
        catch (Exception e) { if (!Disposed) Note = "操作失败：" + e.Message; }
        finally { IsBusy = false; }
    }, () => !Disposed && IsVisible && !IsBusy && (can?.Invoke() ?? true));
    public virtual void Dispose() { Disposed = true; }
}
public sealed class RelayCommand : ICommand
{
    private readonly Action action;
    private readonly Func<bool> can;
    public RelayCommand(Action action, Func<bool>? can = null) { this.action = action; this.can = can ?? (() => true); }
    public bool CanExecute(object? parameter) => can();
    public void Execute(object? parameter) { if (CanExecute(parameter)) action(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
