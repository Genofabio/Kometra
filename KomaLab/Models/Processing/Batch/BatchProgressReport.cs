namespace KomaLab.Models.Processing;

public record BatchProgressReport(
    int CurrentFileIndex, 
    int TotalFiles, 
    string CurrentFileName, 
    double Percentage);