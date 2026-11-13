using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private TrackViewModel? Track => DataContext as TrackViewModel;
    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>Bấm vào header (ngoài các nút) cũng chọn track.</summary>
    private void OnHeaderPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => Track?.RequestSelect();

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Main?.MoveTrackUpCommand.Execute(Track);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Main?.MoveTrackDownCommand.Execute(Track);
    private void OnRemove(object? sender, RoutedEventArgs e) => Main?.RemoveTrackCommand.Execute(Track);
}
