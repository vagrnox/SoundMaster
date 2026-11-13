using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoundMaster.App.Views;

public partial class LogoMark : UserControl
{
    public LogoMark() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
