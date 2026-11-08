using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XStudio;

internal sealed class DestinationEditorWindow : Window
{
    private readonly ComboBox platform = new();
    private readonly TextBox name = new();
    private readonly TextBox serverUrl = new();
    private readonly PasswordBox streamKey = new();
    private readonly Destination? original;

    public Destination? Result { get; private set; }

    public DestinationEditorWindow(Destination? existing = null)
    {
        original = existing;
        Title = existing is null ? "Add destination · X Studio" : "Edit destination · X Studio";
        Width = 500;
        Height = 610;
        MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("#191A1D");
        Foreground = Brush("#F3F2EF");
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(24) };
        Content = root;
        root.Children.Add(new TextBlock { Text = existing is null ? "NEW OUTPUT" : "EDIT OUTPUT", Foreground = Brush("#F04455"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        root.Children.Add(new TextBlock { Text = existing is null ? "Add a destination" : "Update destination", FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        root.Children.Add(new TextBlock { Text = "Use the RTMP server URL and stream key from your streaming account.", Foreground = Brush("#999A9E"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });

        AddField(root, "PLATFORM", platform);
        platform.Items.Add("TikTok LIVE");
        platform.Items.Add("YouTube Live");
        platform.Items.Add("Twitch");
        platform.Items.Add("Custom RTMP");
        platform.SelectedItem = existing?.Platform ?? "TikTok LIVE";
        platform.SelectionChanged += (_, _) => { if (existing is null && string.IsNullOrWhiteSpace(name.Text)) name.Text = platform.SelectedItem?.ToString() ?? ""; };

        AddField(root, "DESTINATION NAME", name, "e.g. TikTok LIVE");
        AddField(root, "RTMP SERVER URL", serverUrl, "rtmp(s)://ingest-server/app");
        AddField(root, "STREAM KEY", streamKey, "Paste the stream key from your account");
        root.Children.Add(new TextBlock { Text = "X Studio encrypts this key for your Windows account. The active FFmpeg process receives it while streaming.", Foreground = Brush("#999A9E"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) });

        var buttons = new DockPanel { LastChildFill = false };
        root.Children.Add(buttons);
        var cancel = CreateButton("Cancel", primary: false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        var save = CreateButton(existing is null ? "Save destination" : "Save changes", primary: true);
        save.Margin = new Thickness(0, 0, 9, 0);
        save.Click += SaveClick;
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(save);

        if (existing is not null)
        {
            name.Text = existing.Name;
            serverUrl.Text = existing.ServerUrl;
            streamKey.Password = existing.StreamKey;
        }
        else
        {
            name.Text = "TikTok LIVE";
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var label = name.Text.Trim();
        var address = serverUrl.Text.Trim();
        var secret = streamKey.Password;
        if (string.IsNullOrWhiteSpace(label)) { ShowError("Give this destination a name."); return; }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "rtmp" && uri.Scheme != "rtmps") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        { ShowError("Enter a base rtmp:// or rtmps:// server URL. Keep the stream key in its own field."); return; }
        if (string.IsNullOrWhiteSpace(secret)) { ShowError("Paste the stream key for this destination."); return; }
        if (address.Contains('|') || address.Contains('[') || address.Contains(']') || secret.Contains('|') || secret.Contains('[') || secret.Contains(']') || secret.Contains('\\') || secret.Contains('\r') || secret.Contains('\n'))
        { ShowError("The server URL or key contains a separator FFmpeg's multi-output format cannot safely accept."); return; }

        Result = new Destination
        {
            Id = original?.Id ?? Guid.NewGuid().ToString("N"),
            Platform = platform.SelectedItem?.ToString() ?? "Custom RTMP",
            Name = label,
            ServerUrl = address.TrimEnd('/'),
            StreamKey = secret,
            UseForStream = original?.UseForStream ?? true
        };
        DialogResult = true;
        Close();
    }

    private static void AddField(Panel panel, string label, Control control, string? placeholder = null)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#C7C7C5"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        control.Height = 38;
        control.Margin = new Thickness(0, 0, 0, 13);
        if (control is TextBox textBox)
        {
            textBox.Foreground = Brush("#F3F2EF");
            textBox.Background = Brush("#222327");
            textBox.BorderBrush = Brush("#38393F");
            textBox.Padding = new Thickness(10, 8, 10, 7);
            textBox.FontSize = 12;
            textBox.ToolTip = placeholder;
        }
        else if (control is PasswordBox passwordBox)
        {
            passwordBox.Foreground = Brush("#F3F2EF");
            passwordBox.Background = Brush("#222327");
            passwordBox.BorderBrush = Brush("#38393F");
            passwordBox.Padding = new Thickness(10, 8, 10, 7);
            passwordBox.FontSize = 12;
            passwordBox.ToolTip = placeholder;
        }
        else if (control is ComboBox combo)
        {
            combo.Foreground = Brush("#F3F2EF");
            combo.Background = Brush("#222327");
            combo.BorderBrush = Brush("#38393F");
            combo.Padding = new Thickness(8, 6, 8, 6);
            combo.FontSize = 12;
            combo.Margin = new Thickness(0, 0, 0, 13);
        }
        panel.Children.Add(control);
    }

    private static Button CreateButton(string content, bool primary) => new()
    {
        Content = content,
        MinWidth = primary ? 132 : 84,
        Height = 36,
        Padding = new Thickness(13, 0, 13, 0),
        Background = primary ? Brush("#F04455") : Brush("#222327"),
        Foreground = Brushes.White,
        BorderBrush = primary ? Brush("#F04455") : Brush("#38393F"),
        BorderThickness = new Thickness(1),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private void ShowError(string text) => MessageBox.Show(this, text, "Check destination details", MessageBoxButton.OK, MessageBoxImage.Warning);
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
