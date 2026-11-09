using Avalonia.Controls;

namespace CanSatGSUI.Views;

/// <summary>Star button that pins a graph to the Graphs tab. DataContext: a GraphCardViewModel.</summary>
public partial class PinButton : UserControl
{
    public PinButton()
    {
        InitializeComponent();
    }
}
