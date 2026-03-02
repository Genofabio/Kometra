namespace Kometra.Models.Processing.Batch;

public record BatchProgressReport(
    int CurrentFileIndex, 
    int TotalFiles, 
    string CurrentFileName, 
    double Percentage);