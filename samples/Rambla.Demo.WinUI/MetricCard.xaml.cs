using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rambla.Demo.WinUI;

public sealed partial class MetricCard : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    public MetricCard() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
