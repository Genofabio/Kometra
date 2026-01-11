using System;

namespace KomaLab.Services.Undo;

public interface IUndoableAction : IDisposable
{
    string Name { get; }
    void Execute(); // Redo / Prima esecuzione
    void Undo();    // Annulla
}