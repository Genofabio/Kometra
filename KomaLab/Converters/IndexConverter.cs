using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Data.Converters;
using System.Linq;

namespace KomaLab.Converters;

public class IndexConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] == null || values[1] == null) return "0";
        
        var item = values[0];
        var items = values[1] as System.Collections.IEnumerable;
        
        if (items == null) return "0";

        int index = 0;
        foreach (var current in items)
        {
            if (ReferenceEquals(current, item)) return (index + 1).ToString();
            index++;
        }
        return "0";
    }
}