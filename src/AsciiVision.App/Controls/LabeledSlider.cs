using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace AsciiVision.App.Controls;

/// <summary>"Label ............ value" above a slider; keeps the settings panel compact and consistent.</summary>
public sealed class LabeledSlider : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(LabeledSlider));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LabeledSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LabeledSlider)d).UpdateText()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(1.0));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.01));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(LabeledSlider),
        new PropertyMetadata("0.00", (d, _) => ((LabeledSlider)d).UpdateText()));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(LabeledSlider),
        new PropertyMetadata(string.Empty, (d, _) => ((LabeledSlider)d).UpdateText()));

    private static readonly DependencyPropertyKey ValueTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ValueText), typeof(string), typeof(LabeledSlider), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueTextProperty = ValueTextPropertyKey.DependencyProperty;

    public LabeledSlider()
    {
        Template = BuildTemplate();
        UpdateText();
    }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string ValueText => (string)GetValue(ValueTextProperty);

    private void UpdateText() => SetValue(ValueTextPropertyKey, Value.ToString(Format, CultureInfo.CurrentCulture) + Suffix);

    private static ControlTemplate BuildTemplate()
    {
        // Built in code so the control needs no generic.xaml; visuals come from the app theme.
        var template = new ControlTemplate(typeof(LabeledSlider));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(MarginProperty, new Thickness(0, 0, 0, 10));

        var row0 = new FrameworkElementFactory(typeof(RowDefinition));
        row0.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
        var row1 = new FrameworkElementFactory(typeof(RowDefinition));
        row1.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
        root.AppendChild(row0);
        root.AppendChild(row1);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Label)) { RelativeSource = RelativeSource.TemplatedParent });
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        label.SetValue(TextBlock.FontSizeProperty, 12.5);
        root.AppendChild(label);

        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(ValueText)) { RelativeSource = RelativeSource.TemplatedParent });
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        value.SetValue(TextBlock.FontSizeProperty, 12.0);
        value.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        root.AppendChild(value);

        var slider = new FrameworkElementFactory(typeof(Slider));
        slider.SetValue(Grid.RowProperty, 1);
        slider.SetValue(MarginProperty, new Thickness(0, 2, 0, 0));
        slider.SetValue(Slider.IsSnapToTickEnabledProperty, true);
        slider.SetValue(Slider.IsMoveToPointEnabledProperty, true);
        slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Value)) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        slider.SetBinding(RangeBase.MinimumProperty, new Binding(nameof(Minimum)) { RelativeSource = RelativeSource.TemplatedParent });
        slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Maximum)) { RelativeSource = RelativeSource.TemplatedParent });
        slider.SetBinding(Slider.TickFrequencyProperty, new Binding(nameof(Step)) { RelativeSource = RelativeSource.TemplatedParent });
        slider.SetBinding(RangeBase.SmallChangeProperty, new Binding(nameof(Step)) { RelativeSource = RelativeSource.TemplatedParent });
        slider.SetBinding(RangeBase.LargeChangeProperty, new Binding(nameof(Step)) { RelativeSource = RelativeSource.TemplatedParent, Converter = new TimesTen() });
        slider.SetBinding(AutomationNameProperty, new Binding(nameof(Label)) { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(slider);

        template.VisualTree = root;
        return template;
    }

    private static readonly DependencyProperty AutomationNameProperty = System.Windows.Automation.AutomationProperties.NameProperty;

    private sealed class TimesTen : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double d ? d * 10 : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
