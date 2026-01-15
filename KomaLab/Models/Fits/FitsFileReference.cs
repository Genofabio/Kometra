namespace KomaLab.Models.Fits;

public class FitsFileReference
{
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    // Stato: Header modificato in RAM
    public FitsHeader? UnsavedHeader { get; set; }
    public bool HasUnsavedChanges => UnsavedHeader != null;

    public FitsFileReference(string path)
    {
        FilePath = path;
    }
}