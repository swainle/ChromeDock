using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ChromeDock;

public partial class StyledDialog : Window
{
    public StyledDialog() : this(string.Empty, string.Empty) { }

    private StyledDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    public static void ShowMessage(Window owner, string title, string message)
    {
        new StyledDialog(title, message).ShowDialog<bool>(owner);
    }

    public static Task<bool> ConfirmDelete(Window owner, string message)
    {
        var dialog = new StyledDialog("删除环境", message);
        dialog.CancelButton.IsVisible = true;
        dialog.ConfirmButton.Content = "删除";
        dialog.ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(217, 45, 32));
        return dialog.ShowDialog<bool>(owner);
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}

