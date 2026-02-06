using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace KomaLab.Models.Export;

public partial class ExportableItem : ObservableObject
{
    public string FullPath { get; }
    public string FileName { get; }

    // Usato per le CheckBox
    [ObservableProperty] 
    private bool _isSelected = true;

    // --- QUESTA È LA PROPRIETÀ CHE MANCAVA ---
    // Usata per mostrare l'icona "Occhio" sull'item visualizzato al centro
    [ObservableProperty] 
    private bool _isPreviewing; 

    // Stato per la progress bar/testo durante l'export
    [ObservableProperty] private string _status = "In attesa";
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _isError;

    public ExportableItem(string path)
    {
        FullPath = path;
        FileName = Path.GetFileName(path);
    }
}