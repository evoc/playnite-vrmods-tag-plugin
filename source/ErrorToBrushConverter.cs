using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Playnite.VRMods
{
    /// <summary>
    /// WPF value converter for status text → Brush (color).
    ///
    /// Used in the settings UI to color-code source status text (and the status dot):
    /// - "Last scan: ..." → positive/healthy green
    /// - "Error: ..." → negative/error red
    /// - "No data: ..." → mixed/attention yellow (succeeded but returned 0 entries)
    /// - Any other text → muted neutral (never scanned or unknown state)
    ///
    /// The colors resolve from Playnite's active theme (PositiveRatingBrush /
    /// NegativeRatingBrush / MixedRatingBrush / TextBrushDarker) so they stay in
    /// sync with the user's chosen theme, falling back to static brushes when the
    /// theme resources are unavailable (e.g. unit tests running without a theme).
    ///
    /// The converter is one-way (Convert only) — ConvertBack throws NotImplementedException.
    /// </summary>
    public class ErrorToBrushConverter : IValueConverter
    {
        /// <summary>
        /// Converts a status text string to a theme-aligned Brush for display.
        ///
        /// Checks the text prefix to determine the color:
        /// - Starts with "Error:" (case-insensitive) → negative/error red
        /// - Starts with "No data:" (case-insensitive) → mixed/attention yellow
        /// - Starts with "Last scan:" → positive/healthy green
        /// - Anything else → muted neutral
        /// </summary>
        /// <param name="value">The status text (e.g., "Last scan: 1/1/2024 12:00 PM").</param>
        /// <param name="targetType">Expected type (ignored).</param>
        /// <param name="parameter">Converter parameter (not used).</param>
        /// <param name="culture">Culture info (not used).</param>
        /// <returns>A theme brush: error red, no-data yellow, healthy green, or neutral.</returns>
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            if (value is string text)
            {
                if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    return ThemeBrush("NegativeRatingBrush", Brushes.Red);
                if (text.StartsWith("No data:", StringComparison.OrdinalIgnoreCase))
                    return ThemeBrush("MixedRatingBrush", Brushes.Orange);
                if (text.StartsWith("Last scan:", StringComparison.OrdinalIgnoreCase))
                    return ThemeBrush("PositiveRatingBrush", Brushes.Green);
            }
            return ThemeBrush("TextBrushDarker", Brushes.Gray);
        }

        /// <summary>
        /// Resolves a Playnite theme brush by resource key so status colors track the active
        /// theme. Falls back to <paramref name="fallback"/> when the key is unavailable
        /// (no application, or the theme does not define the key — e.g. unit tests).
        /// </summary>
        /// <param name="key">The theme resource key (e.g. "PositiveRatingBrush").</param>
        /// <param name="fallback">Static brush returned when the key is unavailable.</param>
        private static Brush ThemeBrush(string key, Brush fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryFindResource(key) is Brush brush)
                return brush;
            return fallback;
        }

        /// <summary>
        /// Not implemented (this converter is one-way only).
        /// </summary>
        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// WPF value converter: bool → Visibility, for empty-state hints and conditional visibility.
    ///
    /// By default maps true→Visible / false→Collapsed. Pass <c>ConverterParameter="Invert"</c>
    /// to invert the mapping (true→Collapsed / false→Visible), which is how an "empty state"
    /// hint text is shown only when the corresponding collection is empty.
    ///
    /// The converter is one-way (Convert only) — ConvertBack throws NotImplementedException.
    /// </summary>
    public class BoolToVisConverter : IValueConverter
    {
        /// <summary>
        /// Converts a bool to a Visibility value.
        /// </summary>
        /// <param name="value">The bool value (true/false).</param>
        /// <param name="targetType">Expected type (ignored).</param>
        /// <param name="parameter">When the string "Invert", the mapping is inverted.</param>
        /// <param name="culture">Culture info (not used).</param>
        /// <returns>Visible or Collapsed.</returns>
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            var flag = value is bool b && b;
            if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Not implemented (this converter is one-way only).
        /// </summary>
        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            throw new NotImplementedException();
        }
    }
}
