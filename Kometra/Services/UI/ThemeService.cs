using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System;
using System.Linq;

namespace Kometra.Services.UI;

public static class ThemeService
{
    public static void ApplyPrimaryColor(string hexColor)
    {
        if (!Color.TryParse(hexColor, out var baseColor)) return;

        // 1. Convertiamo in HSL per generare la nostra palette personalizzata
        HslColor hsl = new HslColor(baseColor);

        // --- AGGIORNAMENTO NOSTRE RISORSE (AppPalette.axaml) ---
        UpdateResource("Color.Primary.Base", baseColor);
        UpdateResource("Color.Primary.Light", CreateColor(hsl.H, hsl.S + 0.22, hsl.L + 0.08));
        UpdateResource("Color.Primary.Dark", CreateColor(hsl.H, hsl.S - 0.08, hsl.L - 0.06));
        UpdateResource("Color.Primary.Deep", CreateColor(hsl.H, hsl.S - 0.35, hsl.L - 0.36));
        UpdateResource("Color.Primary.ImageBase", CreateColor(hsl.H, hsl.S - 0.40, hsl.L - 0.45));
        UpdateResource("Color.Primary.Muted", CreateColor(hsl.H, hsl.S - 0.45, hsl.L - 0.28));
        
        // Overlay con Alpha B3 (179)
        UpdateResource("Color.Primary.Overlay", HslColor.ToRgb(hsl.H, hsl.S - 0.40, hsl.L - 0.40, 179.0 / 255.0));

        // --- AGGIORNAMENTO PALETTE DI SISTEMA (FluentTheme) ---
        UpdateFluentThemeAccent(baseColor);
    }

    /// <summary>
    /// Modifica l'accent color del FluentTheme per aggiornare bottoni, checkbox e controlli standard.
    /// </summary>
    private static void UpdateFluentThemeAccent(Color newAccent)
    {
        if (Application.Current == null) return;

        // Cerchiamo il FluentTheme tra gli stili dell'applicazione
        var fluentTheme = Application.Current.Styles.OfType<FluentTheme>().FirstOrDefault();

        if (fluentTheme != null)
        {
            // Aggiorniamo la palette Dark (che è quella che usi principalmente)
            if (fluentTheme.Palettes.TryGetValue(ThemeVariant.Dark, out var darkPalette) 
                && darkPalette is ColorPaletteResources darkResources)
            {
                darkResources.Accent = newAccent;
            }

            // Aggiorniamo anche la palette Light per coerenza (se l'utente cambiasse tema di sistema)
            if (fluentTheme.Palettes.TryGetValue(ThemeVariant.Light, out var lightPalette) 
                && lightPalette is ColorPaletteResources lightResources)
            {
                lightResources.Accent = newAccent;
            }
        }
    }

    private static Color CreateColor(double h, double s, double l)
    {
        // Usiamo il metodo statico ToRgb come da sorgente HslColor
        return HslColor.ToRgb(h, s, l, 1.0);
    }

    private static void UpdateResource(string key, object value)
    {
        if (Application.Current != null)
        {
            Application.Current.Resources[key] = value;
        }
    }
}