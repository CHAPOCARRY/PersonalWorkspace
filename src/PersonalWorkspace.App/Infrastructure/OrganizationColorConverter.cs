using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Infrastructure;

public sealed class OrganizationColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Application.Current.Resources[value switch
    {
        OrganizationColor.Blue => "Accent",
        OrganizationColor.Green => "Success",
        OrganizationColor.Amber => "Warning",
        OrganizationColor.Red => "Danger",
        _ => "TextSecondary"
    }];
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
