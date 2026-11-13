using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SoundMaster.App.ViewModels;

namespace SoundMaster.App.Views;

public partial class StripView : UserControl
{
    public StripView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnFaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is StripViewModel vm) vm.GainDb = 0;
    }

    private void OnPanDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is StripViewModel vm) vm.Pan = 0;
    }
}
