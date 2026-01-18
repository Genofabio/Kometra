namespace KomaLab.Models.Fits;

public record FitsCard(
    string Key, 
    string? Value = null, 
    string? Comment = null, 
    bool IsCommentStyle = false
);