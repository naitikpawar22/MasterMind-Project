using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GeminiDesktop.Helpers;

/// <summary>
/// Converts bool true -> Visible, false -> Collapsed (supports Invert parameter).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert || string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isVisible = value is Visibility v && v == Visibility.Visible;
        if (Invert || string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible;
    }
}

/// <summary>
/// Inverts a boolean value for IsEnabled / RadioButton bindings.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

/// <summary>
/// Returns Visible when string is non-null/non-whitespace.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Compares an enum bound value against a ConverterParameter enum value for RadioButton bindings.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (parameter.GetType() == targetType)
            {
                return parameter;
            }

            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
            }
        }

        return Binding.DoNothing;
    }
}
