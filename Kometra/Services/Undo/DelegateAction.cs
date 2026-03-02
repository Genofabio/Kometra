using System;

namespace Kometra.Services.Undo; 

public class DelegateAction : IUndoableAction
{
    private readonly Action _execute;
    private readonly Action _undo;
    private readonly Action<bool>? _onDispose;
    private bool _isDisposed;

    public string Name { get; }
    public bool IsExecuted { get; private set; }

    public DelegateAction(string name, Action execute, Action undo, Action<bool>? onDispose = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        _onDispose = onDispose;
        
        IsExecuted = true; 
    }

    public void Execute() 
    {
        if (_isDisposed) return;
        _execute();
        IsExecuted = true;
    }

    public void Undo() 
    {
        if (_isDisposed) return;
        _undo();
        IsExecuted = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _onDispose?.Invoke(IsExecuted);
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}