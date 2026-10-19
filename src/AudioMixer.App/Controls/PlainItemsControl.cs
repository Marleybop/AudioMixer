using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AudioMixer.App.Controls;

/// <summary>
/// An ItemsControl whose items' contents are visible to screen readers. A plain ItemsControl reports each item as an
/// empty "data item", which hides the sliders and buttons inside it.
/// </summary>
public class PlainItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
