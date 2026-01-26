using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace KomaLab.Converters;

/// <summary>
/// Helper statico che espone convertitori funzionali veloci per la View.
/// </summary>
public static class ObjectConverters
{
    /// <summary>
    /// Ritorna True se il valore (double) è esattamente 0.
    /// Utilizzato per impostare IsIndeterminate=True quando non c'è ancora progresso.
    /// </summary>
    public static readonly IValueConverter IsZero = 
        new FuncValueConverter<double, bool>(val => Math.Abs(val) < double.Epsilon);

    /// <summary>
    /// Ritorna True se il valore non è nullo.
    /// </summary>
    public static readonly IValueConverter IsNotNull = 
        new FuncValueConverter<object?, bool>(val => val != null);
}