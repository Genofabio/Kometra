using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kometra.Services.UI;

public interface IDialogService
{
    Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync();
    
    Task<string?> ShowSaveFitsFileDialogAsync(string defaultFileName);
    
    Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string pattern);
    
    Task<string?> ShowOpenFolderDialogAsync(string title);
}