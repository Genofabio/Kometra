namespace KomaLab.Services.Undo;

public interface IUndoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    void RecordAction(IUndoableAction action);
    void Undo();
    void Redo();
}