using System.Windows;
using System.Windows.Input;

namespace VpsWatcher.App;

/// <summary>
/// Phase 3b transparent gadget window. Code-behind is limited to pure window operations that have
/// no MVVM equivalent: dragging the chromeless window and closing it. Always-on-top, metrics and
/// state stay in the bound ViewModels.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Raised after a drag finishes, with the new top-left (DIPs), so the app can persist it (§9.2).</summary>
    public event Action<double, double>? WindowMoved;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        // DragMove blocks until the mouse button is released; persist the final position afterwards.
        // Started only from the handle, so the pin/close buttons keep registering as clicks (§5.2).
        DragMove();
        WindowMoved?.Invoke(Left, Top);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
