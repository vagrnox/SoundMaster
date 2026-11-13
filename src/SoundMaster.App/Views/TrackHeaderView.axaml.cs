using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SoundMaster.App.ViewModels;

namespace SoundMaster.App.Views;

public partial class TrackHeaderView : UserControl
{
    public TrackHeaderView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPanDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TrackViewModel vm) vm.Pan = 0;
    }
}
