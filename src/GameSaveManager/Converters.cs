using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameSaveManager;

public class BoolToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is false ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>空字符串折叠（null/"" → Collapsed）。</summary>
public class BlankToCollapsed : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>null → Collapsed，非 null → Visible；ConverterParameter="Invert" 时取反。</summary>
public class NullToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool isNull = value == null;
        if (p is string s && s == "Invert") isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
