using Kometra.Models.Settings;

namespace Kometra.Services;

public interface IConfigurationService
{
    AppSettings Current { get; }
    void Save();
    void UpdateSettings(AppSettings newSettings);
    bool ValidateAstapFolder(string? folderPath);
}