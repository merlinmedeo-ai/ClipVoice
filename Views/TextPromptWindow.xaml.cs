using System.Windows;
using System.Windows.Input;

namespace ClipVoice.Views;

public partial class TextPromptWindow : Window
{
    public string Value => ValueBox.Text;

    public TextPromptWindow(string title, string prompt, string initial, string acceptText = "Save")
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        AcceptButtonText.Text = acceptText;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
