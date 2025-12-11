using System.Collections.Generic;
using System.Threading.Tasks;

namespace KomaLab.Services.UI;

public interface IDialogService
{
    Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync();
    
    Task<string?> ShowSaveFitsFileDialogAsync(string defaultFileName);
    
    Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string pattern);
}