using System.Windows;
using System.Windows.Input;
using ClipVoice.Models;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class TemplateEditorWindow : Window
{
    public TextTemplate? Result { get; private set; }
    private readonly Guid _id;

    public TemplateEditorWindow(TextTemplate? source)
    {
        InitializeComponent();
        var settings = ((App)Application.Current).Controller.Settings;
        var gesture = (settings.TemplateMouseModifier ?? "Alt").Replace("+", " + ") + " + right-click";
        InsertionHintText.Text = $"This text will be inserted at the cursor through {gesture} or its template hotkey.";
        HotkeyHintText.Text = $"A hotkey is optional: click the field on the right and press a shortcut, such as Ctrl+Alt+1. All templates remain available through {gesture}.";
        _id = source?.Id ?? Guid.NewGuid();
        SecretCheck.IsChecked = source?.IsSecret ?? false;
        if (source is not null)
        {
            NameBox.Text = source.Name;
            ContentBox.Text = source.Content;
            HotkeyBox.Text = source.Hotkey;
        }
        Loaded += (_, _) => NameBox.Focus();
        LocalizationService.Apply(this, settings.Language);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            HotkeyBox.Clear(); e.Handled = true; return;
        }
        if (HotkeyParser.IsModifierKey(key)) { e.Handled = true; return; }
        var combo = HotkeyParser.FromKeyEvent(key, Keyboard.Modifiers);
        if (!string.IsNullOrWhiteSpace(combo)) HotkeyBox.Text = combo;
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var language = ((App)Application.Current).Controller.Settings.Language;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ErrorText.Text = LocalizationService.Translate("Enter a template name.", language); return; }
        if (string.IsNullOrWhiteSpace(ContentBox.Text)) { ErrorText.Text = LocalizationService.Translate("Template text cannot be empty.", language); return; }
        if (!string.IsNullOrWhiteSpace(HotkeyBox.Text))
        {
            if (!HotkeyParser.TryParse(HotkeyBox.Text, out _, out _, out var normalized, out var error))
            {
                ErrorText.Text = error;
                return;
            }

            HotkeyBox.Text = normalized;
        }

        Result = new TextTemplate
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Content = ContentBox.Text,
            Hotkey = HotkeyBox.Text.Trim(),
            PasteOnHotkey = true,
            IsSecret = SecretCheck.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
