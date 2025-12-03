using System;

namespace KomaLab.Services.Undo;

public interface IUndoableAction : IDisposable
{
    string Name { get; }
    void Execute(); // Redo
    void Undo();    // Undo
}

// Un'implementazione generica comoda per evitare di creare 100 classi
public class DelegateAction : IUndoableAction
{
    private readonly Action _execute;
    private readonly Action _undo;
    private readonly Action? _dispose; // Opzionale: logica di pulizia specifica

    public string Name { get; }

    public DelegateAction(string name, Action execute, Action undo, Action? onDispose = null)
    {
        Name = name;
        _execute = execute;
        _undo = undo;
        _dispose = onDispose;
    }

    public void Execute() => _execute();
    
    public void Undo() => _undo();

    public void Dispose()
    {
        // Se c'è logica di pulizia personalizzata (es. eliminare file temp), eseguila qui.
        _dispose?.Invoke();
        
        // SuppressFinalize non serve qui perché non abbiamo finalizzatore, 
        // ma è buona norma se questa classe diventasse più complessa.
        GC.SuppressFinalize(this);
    }
}