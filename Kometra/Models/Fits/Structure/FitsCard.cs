namespace Kometra.Models.Fits.Structure;

public record FitsCard(
    string Key, 
    string? Value = null, 
    string? Comment = null, 
    bool IsCommentStyle = false
);