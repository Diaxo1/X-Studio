using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XStudio;

internal sealed class SceneSourceEditorWindow : Window
{
    private readonly ComboBox kindBox = new();
    private readonly TextBox nameBox = new();
    private readonly TextBox contentBox = new();
    private readonly ComboBox phoneTypeBox = new();
    private readonly ComboBox appWindowBox = new();
    private readonly TextBox colorBox = new();
    private readonly TextBox xBox = new();
    private readonly TextBox yBox = new();
    private readonly TextBox widthBox = new();
    private readonly TextBox heightBox = new();
    private readonly TextBox fontSizeBox = new();
    private readonly TextBox cropLeftBox = new();
    private readonly TextBox cropTopBox = new();
    private readonly TextBox cropRightBox = new();
    private readonly TextBox cropBottomBox = new();
    private readonly TextBlock contentLabel = new();
    private readonly TextBlock colorLabel = new() { Text = "TEXT COLOR · HEX" };
    private readonly SceneSource? original;

    public SceneSource? Result { get; private set; }

    public SceneSourceEditorWindow(StudioScene scene, SceneSource? source = null)
    {
        original = source;
        Title = source is null ? "Add source · X Studio" : "Edit source · X Studio";
        Width = 470;
        Height = 690;
        MinWidth = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#191A1D");
        Foreground = Brush("#F3F2EF");
        FontFamily = new FontFamily("Segoe UI");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(24) };
        scroll.Content = root;
        Content = scroll;
        root.Children.Add(new TextBlock { Text = "SCENE SOURCE", Foreground = Brush("#F04455"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        root.Children.Add(new TextBlock { Text = source is null ? "Add a source" : "Edit source", FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new TextBlock { Text = $"Place content on {scene.Name}. Position and width use percentages of the canvas.", Foreground = Brush("#999A9E"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        AddField(root, "SOURCE TYPE", kindBox);
        kindBox.Items.Add("Text");
        kindBox.Items.Add("Image");
        kindBox.Items.Add("Video");
        kindBox.Items.Add("Background");
        kindBox.Items.Add("Display");
        kindBox.Items.Add("App");
        kindBox.Items.Add("Camera");
        kindBox.Items.Add("Phone");
        kindBox.SelectionChanged += (_, _) => UpdateKindFields();
        AddField(root, "SOURCE NAME", nameBox);
        contentLabel.Text = "TEXT";
        contentLabel.Foreground = Brush("#C7C7C5");
        contentLabel.FontSize = 10;
        contentLabel.FontWeight = FontWeights.SemiBold;
        contentLabel.Margin = new Thickness(0, 0, 0, 5);
        root.Children.Add(contentLabel);
        var contentDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 13) };
        contentBox.Height = 38;
        contentBox.Padding = new Thickness(10, 8, 10, 7);
        StyleControl(contentBox);
        var browse = Button("Browse…", false);
        browse.Margin = new Thickness(7, 0, 0, 0);
        browse.Click += BrowseImage;
        DockPanel.SetDock(browse, Dock.Right);
        contentDock.Children.Add(browse);
        var refreshApps = Button("Refresh apps", false);
        refreshApps.Margin = new Thickness(0, 0, 7, 0);
        refreshApps.Visibility = Visibility.Collapsed;
        refreshApps.Click += (_, _) =>
        {
            var selected = contentBox.Text;
            appWindowBox.Items.Clear();
            foreach (var title in CaptureWindowService.GetVisibleWindowTitles()) appWindowBox.Items.Add(title);
            appWindowBox.SelectedItem = appWindowBox.Items.Cast<string>().FirstOrDefault(title => title.Equals(selected, StringComparison.OrdinalIgnoreCase));
        };
        DockPanel.SetDock(refreshApps, Dock.Right);
        contentDock.Children.Add(refreshApps);
        contentDock.Children.Add(contentBox);
        phoneTypeBox.Height = 38;
        phoneTypeBox.Items.Add("Android"); phoneTypeBox.Items.Add("iPhone");
        StyleControl(phoneTypeBox);
        phoneTypeBox.Visibility = Visibility.Collapsed;
        contentDock.Children.Add(phoneTypeBox);
        root.Children.Add(contentDock);
        appWindowBox.Height = 38;
        appWindowBox.Margin = new Thickness(0, -8, 0, 13);
        foreach (var title in CaptureWindowService.GetVisibleWindowTitles()) appWindowBox.Items.Add(title);
        appWindowBox.SelectionChanged += (_, _) => { if (appWindowBox.SelectedItem is string title) contentBox.Text = title; };
        StyleControl(appWindowBox);
        appWindowBox.Visibility = Visibility.Collapsed;
        root.Children.Add(appWindowBox);
        colorLabel.Foreground = Brush("#C7C7C5"); colorLabel.FontSize = 10; colorLabel.FontWeight = FontWeights.SemiBold; colorLabel.Margin = new Thickness(0, 0, 0, 5);
        root.Children.Add(colorLabel);
        colorBox.Height = 38; colorBox.Margin = new Thickness(0, 0, 0, 13); StyleControl(colorBox); root.Children.Add(colorBox);

        var position = new Grid { Margin = new Thickness(0, 2, 0, 13) };
        for (var i = 0; i < 5; i++) position.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddCompact(position, "X %", xBox, 0);
        AddCompact(position, "Y %", yBox, 1);
        AddCompact(position, "WIDTH %", widthBox, 2);
        AddCompact(position, "HEIGHT %", heightBox, 3);
        AddCompact(position, "FONT PX", fontSizeBox, 4);
        root.Children.Add(position);
        var cropGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (var i = 0; i < 4; i++) cropGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddCompact(cropGrid, "CROP L %", cropLeftBox, 0); AddCompact(cropGrid, "T %", cropTopBox, 1); AddCompact(cropGrid, "R %", cropRightBox, 2); AddCompact(cropGrid, "B %", cropBottomBox, 3);
        root.Children.Add(cropGrid);
        root.Children.Add(new TextBlock { Text = "Display and App are independent capture layers. Keep a Background at the bottom, then put display, app, cast, camera, and text above it. Crop edges trim a source before it is fitted.", Foreground = Brush("#999A9E"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) });

        var actions = new DockPanel { LastChildFill = false };
        root.Children.Add(actions);
        var cancel = Button("Cancel", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        actions.Children.Add(cancel);
        var save = Button(source is null ? "Add source" : "Save source", true);
        save.Margin = new Thickness(0, 0, 9, 0);
        save.Click += SaveClick;
        DockPanel.SetDock(save, Dock.Right);
        actions.Children.Add(save);

        if (source is null)
        {
            kindBox.SelectedItem = "Text";
            nameBox.Text = "Text";
            contentBox.Text = "Your text";
            phoneTypeBox.SelectedItem = "Android";
            colorBox.Text = "#FFFFFF";
            cropLeftBox.Text = cropTopBox.Text = cropRightBox.Text = cropBottomBox.Text = "0";
            xBox.Text = "8"; yBox.Text = "8"; widthBox.Text = "42"; heightBox.Text = ""; fontSizeBox.Text = "48";
        }
        else
        {
            kindBox.SelectedItem = source.Kind;
            nameBox.Text = source.Name;
            contentBox.Text = source.Content;
            appWindowBox.SelectedItem = appWindowBox.Items.Cast<string>().FirstOrDefault(title => title.Equals(source.Content, StringComparison.OrdinalIgnoreCase));
            phoneTypeBox.SelectedItem = source.Content is "Android" or "iPhone" ? source.Content : "Android";
            colorBox.Text = source.Color;
            xBox.Text = source.X.ToString("0.##"); yBox.Text = source.Y.ToString("0.##"); widthBox.Text = source.Width.ToString("0.##"); heightBox.Text = source.Height == 0 ? "" : source.Height.ToString("0.##"); fontSizeBox.Text = source.FontSize.ToString();
            cropLeftBox.Text = source.CropLeft.ToString("0.##"); cropTopBox.Text = source.CropTop.ToString("0.##"); cropRightBox.Text = source.CropRight.ToString("0.##"); cropBottomBox.Text = source.CropBottom.ToString("0.##");
        }
    }

    private void UpdateKindFields()
    {
        if (contentBox is null || kindBox.SelectedItem is null) return;
        var kind = kindBox.SelectedItem.ToString();
        contentLabel.Text = kind switch { "Image" => "IMAGE FILE", "Video" => "VIDEO FILE", "Background" => "BACKGROUND FILE · OR ‘transparent’", "App" => "APP WINDOW TITLE", "Phone" => "PHONE TYPE", _ => "TEXT CONTENT" };
        contentBox.Visibility = kind is "Camera" or "Phone" or "Display" ? Visibility.Collapsed : Visibility.Visible;
        phoneTypeBox.Visibility = kind == "Phone" ? Visibility.Visible : Visibility.Collapsed;
        appWindowBox.Visibility = kind == "App" ? Visibility.Visible : Visibility.Collapsed;
        if (contentBox.Parent is DockPanel contentParent)
            foreach (UIElement child in contentParent.Children)
                if (child is Button browse) browse.Visibility = browse.Content?.ToString() == "Refresh apps"
                    ? kind == "App" ? Visibility.Visible : Visibility.Collapsed
                    : kind is "Image" or "Video" or "Background" ? Visibility.Visible : Visibility.Collapsed;
        contentLabel.Text = kind switch { "Camera" => "CAMERA SOURCE", "Phone" => "PHONE TYPE", "Image" => "IMAGE FILE", "Video" => "VIDEO FILE", "Background" => "BACKGROUND FILE · OR ‘transparent’", "App" => "APP WINDOW TITLE", _ => "TEXT CONTENT" };
        if (kind == "Phone" && string.IsNullOrWhiteSpace(contentBox.Text)) contentBox.Text = "Android";
        if (kind == "Phone" && contentBox.Text == "Your text") contentBox.Text = "Android";
        if (kind == "Background" && contentBox.Text is "Your text" or "") contentBox.Text = "transparent";
        if (kind == "Display" && nameBox.Text is "" or "Text") nameBox.Text = "Full display";
        else if (kind == "App" && nameBox.Text is "" or "Text") nameBox.Text = "App window";
        else if (kind == "Video" && nameBox.Text is "" or "Text") nameBox.Text = "Video";
        else if (kind == "Background" && nameBox.Text is "" or "Text") nameBox.Text = "Background";
        colorBox.Visibility = kind == "Text" ? Visibility.Visible : Visibility.Collapsed;
        colorLabel.Visibility = kind == "Text" ? Visibility.Visible : Visibility.Collapsed;
        fontSizeBox.Visibility = kind == "Text" ? Visibility.Visible : Visibility.Collapsed;
        if (nameBox.Text.Length == 0) nameBox.Text = kind ?? "Source";
    }

    private void BrowseImage(object sender, RoutedEventArgs e)
    {
        var kind = kindBox.SelectedItem?.ToString();
        var picker = new OpenFileDialog { Title = "Choose scene media", Filter = kind == "Video" ? "Video files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All files|*.*" : "Media files|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.avi|All files|*.*" };
        if (picker.ShowDialog(this) == true)
        {
            contentBox.Text = picker.FileName;
            if (nameBox.Text is "" or "Text") nameBox.Text = Path.GetFileNameWithoutExtension(picker.FileName);
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var kind = kindBox.SelectedItem?.ToString() ?? "Text";
        var name = nameBox.Text.Trim();
        if (name.Length == 0) { Warn("Enter a name for this source."); return; }
        var content = kind == "Camera" ? "Settings camera" : kind == "Phone" ? phoneTypeBox.SelectedItem?.ToString() ?? "Android" : kind == "Display" ? "desktop" : contentBox.Text.Trim();
        if (kind == "Text" && content.Length == 0) { Warn("Enter the text to show in the scene."); return; }
        if (kind == "Image" && !File.Exists(content)) { Warn("Choose an image file that exists on this PC."); return; }
        if (kind == "Video" && !File.Exists(content)) { Warn("Choose a video file that exists on this PC."); return; }
        if (kind == "Background" && content != "transparent" && !File.Exists(content)) { Warn("Choose an existing image or video, or enter ‘transparent’ for an empty background."); return; }
        if (kind == "App" && content.Length == 0) { Warn("Enter all or part of the app window title to capture."); return; }
        if (kind == "Phone" && content is not ("Android" or "iPhone")) { Warn("Choose Android or iPhone as the phone source."); return; }
        if (!double.TryParse(xBox.Text, out var x) || !double.TryParse(yBox.Text, out var y) || !double.TryParse(widthBox.Text, out var width) || x is < 0 or > 100 || y is < 0 or > 100 || width is <= 0 or > 100) { Warn("Enter X and Y between 0 and 100, and a width between 1 and 100."); return; }
        var height = string.IsNullOrWhiteSpace(heightBox.Text) ? 0 : double.TryParse(heightBox.Text, out var enteredHeight) ? enteredHeight : -1;
        if (height is < 0 or > 100) { Warn("Leave height blank to keep the source aspect ratio, or enter a value between 1 and 100%."); return; }
        if (original is null && kind is "Display" or "App" or "Background") { x = 0; y = 0; width = 100; height = 100; }
        if (!int.TryParse(fontSizeBox.Text, out var fontSize)) fontSize = 48;
        var color = colorBox.Text.Trim();
        if (kind == "Text" && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) { Warn("Use a six-digit hex color, for example #FFFFFF."); return; }
        double Crop(TextBox box) => double.TryParse(box.Text, out var value) ? Math.Clamp(value, 0, 90) : 0;
        var cropLeft = Crop(cropLeftBox); var cropRight = Crop(cropRightBox); var cropTop = Crop(cropTopBox); var cropBottom = Crop(cropBottomBox);
        if (cropLeft + cropRight >= 95 || cropTop + cropBottom >= 95) { Warn("Crop edges must leave at least 5% of the source visible."); return; }
        Result = new SceneSource { Id = original?.Id ?? Guid.NewGuid().ToString("N"), Kind = kind, Name = name, Content = content, Color = color, X = x, Y = y, Width = width, Height = height, CropLeft = cropLeft, CropTop = cropTop, CropRight = cropRight, CropBottom = cropBottom, FontSize = Math.Clamp(fontSize, 10, 180) };
        DialogResult = true;
        Close();
    }

    private static void AddField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#C7C7C5"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        field.Height = 38;
        field.Margin = new Thickness(0, 0, 0, 13);
        StyleControl(field);
        panel.Children.Add(field);
    }

    private static void AddCompact(Grid grid, string label, TextBox box, int column)
    {
        var block = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 5, 0, 5, 0) };
        block.Children.Add(new TextBlock { Text = label, Foreground = Brush("#999A9E"), FontSize = 9, Margin = new Thickness(0, 0, 0, 4) });
        box.Height = 34; box.Padding = new Thickness(7, 6, 6, 5); StyleControl(box);
        block.Children.Add(box);
        Grid.SetColumn(block, column); grid.Children.Add(block);
    }

    private static void StyleControl(Control control)
    {
        control.Foreground = Brush("#F3F2EF"); control.Background = Brush("#222327"); control.BorderBrush = Brush("#38393F");
        if (control is TextBox textBox) textBox.CaretBrush = Brush("#F04455");
    }

    private static Button Button(string text, bool primary) => new()
    {
        Content = text, MinWidth = primary ? 115 : 82, Height = 36, Padding = new Thickness(12, 0, 12, 0),
        Background = Brush(primary ? "#F04455" : "#222327"), Foreground = Brushes.White,
        BorderBrush = Brush(primary ? "#F04455" : "#38393F"), BorderThickness = new Thickness(1),
        FontSize = 11, FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand
    };

    private void Warn(string message) => MessageBox.Show(this, message, "Check source details", MessageBoxButton.OK, MessageBoxImage.Information);
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}

