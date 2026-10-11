using System;
using System.Globalization;
using System.Windows.Data;

namespace TimelapseChecker.App;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>ConverterParameter に "真の時;偽の時" を指定してbool→文字列へ変換する。</summary>
public class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? ";").Split(';');
        bool b = value is bool v && v;
        return b ? parts[0] : (parts.Length > 1 ? parts[1] : "");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
