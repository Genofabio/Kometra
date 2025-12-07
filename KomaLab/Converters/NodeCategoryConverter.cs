using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models;

namespace KomaLab.Converters;

public class NodeCategoryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeCategory category)
        {
            // Restituisce SOLO il nome: "Image", "Process", "Analysis"
            return category.ToString();
        }
        
        return "Image"; // Default
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}