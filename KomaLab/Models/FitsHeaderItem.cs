using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.Models;

public partial class FitsHeaderItem : ObservableObject
{
    private string _key = "";
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    private string _value = "";
    public string Value
    {
        get => _value;
        set 
        {
            if (SetProperty(ref _value, value))
            {
                IsModified = true; // Segna come modificato quando cambia il valore
            }
        }
    }

    private string _comment = "";
    public string Comment
    {
        get => _comment;
        set 
        {
            if (SetProperty(ref _comment, value))
            {
                IsModified = true;
            }
        }
    }

    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => SetProperty(ref _isReadOnly, value);
    }

    private bool _isModified;
    public bool IsModified
    {
        get => _isModified;
        set => SetProperty(ref _isModified, value);
    }
}