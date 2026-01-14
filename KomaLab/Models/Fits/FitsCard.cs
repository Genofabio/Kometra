namespace KomaLab.Models.Fits;

public class FitsCard
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Comment { get; set; }
    public bool IsCommentStyle { get; set; } 
    public string OriginalRawString { get; set; } = string.Empty;

    /// <summary>
    /// Crea una copia profonda (Deep Copy) della card.
    /// </summary>
    public FitsCard Clone()
    {
        return new FitsCard
        {
            Key = this.Key,
            Value = this.Value,
            Comment = this.Comment,
            IsCommentStyle = this.IsCommentStyle,
            OriginalRawString = this.OriginalRawString
        };
    }
}