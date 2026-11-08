using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XStudio;

internal sealed class SceneEditorWindow : Window
{
    private readonly TextBox nameBox = new();
    private readonly ComboBox formatBox = new();
    private readonly ComboBox templateBox = new();
    private readonly StackPanel sourceList = new();
    private readonly Canvas templatePreview = new() { Width = 290, Height = 164, ClipToBounds = true };
    private readonly TextBlock templateSummary = new();
    private bool updatingTemplates;
    private readonly List<SceneSource> sources;
    private readonly StudioScene? original;

    public StudioScene? Result { get; private set; }

    public SceneEditorWindow(StudioScene? scene = null)
    {
        original = scene;
        sources = scene?.Sources.Select(CloneSource).ToList() ?? [];
        Title = scene is null ? "New scene · X Studio" : "Edit scene · X Studio";
        Width = 570;
        Height = 700;
        MinWidth = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#191A1D");
        Foreground = Brush("#F3F2EF");
        FontFamily = new FontFamily("Segoe UI");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(24) };
        scroll.Content = root;
        Content = scroll;
        root.Children.Add(new TextBlock { Text = scene is null ? "SCENE LIBRARY" : "SCENE SETTINGS", Foreground = Brush("#F04455"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        root.Children.Add(new TextBlock { Text = scene is null ? "Create a scene" : "Edit scene", FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new TextBlock { Text = "Set the canvas and manage the sources layered into this scene.", Foreground = Brush("#999A9E"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        AddField(root, "SCENE NAME", nameBox);
        nameBox.Text = scene?.Name ?? "New scene";
        formatBox.Items.Add("Wide · 16:9");
        formatBox.Items.Add("Vertical · 9:16");
        formatBox.SelectedItem = scene?.Format ?? "Wide · 16:9";
        if (scene is null)
        {
            root.Children.Add(new TextBlock { Text = "TEMPLATE CANVAS", Foreground = Brush("#C7C7C5"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            var formatTabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 13) };
            var wideTab = Button("Wide · 16:9", false); wideTab.Width = 135;
            var verticalTab = Button("TikTok · 9:16", false); verticalTab.Width = 135; verticalTab.Margin = new Thickness(8, 0, 0, 0);
            wideTab.Click += (_, _) => SetTemplateFormat("Wide · 16:9");
            verticalTab.Click += (_, _) => SetTemplateFormat("Vertical · 9:16");
            formatTabs.Children.Add(wideTab); formatTabs.Children.Add(verticalTab); root.Children.Add(formatTabs);
            AddField(root, "START FROM TEMPLATE", templateBox);
            root.Children.Add(new Border { Background = Brush("#111214"), BorderBrush = Brush("#38393F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, Child = templatePreview });
            templateSummary.Foreground = Brush("#999A9E"); templateSummary.FontSize = 10; templateSummary.TextWrapping = TextWrapping.Wrap; templateSummary.Margin = new Thickness(0, 6, 0, 14); root.Children.Add(templateSummary);
            templateBox.SelectionChanged += (_, _) => ApplySelectedTemplate();
            SetTemplateFormat("Wide · 16:9");
        }
        else AddField(root, "CANVAS FORMAT", formatBox);

        var sourceHeader = new DockPanel { Margin = new Thickness(0, 1, 0, 8) };
        var sourceTitle = new TextBlock { Text = "SOURCES", Foreground = Brush("#C7C7C5"), FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        sourceHeader.Children.Add(sourceTitle);
        var add = Button("＋ Add source", true);
        add.Height = 32;
        add.Click += AddSource;
        DockPanel.SetDock(add, Dock.Right);
        sourceHeader.Children.Add(add);
        root.Children.Add(sourceHeader);
        root.Children.Add(sourceList);
        RenderSources();
        root.Children.Add(new TextBlock { Text = "Scene layers stack from back to front. Add an empty or image/video background, then full display, app windows, phone cast, camera, images, or text. Source position, size, and crop use canvas percentages.", Foreground = Brush("#999A9E"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });

        var actions = new DockPanel { LastChildFill = false };
        root.Children.Add(actions);
        var cancel = Button("Cancel", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        actions.Children.Add(cancel);
        var save = Button(scene is null ? "Create scene" : "Save changes", true);
        save.Margin = new Thickness(0, 0, 9, 0);
        save.Click += SaveClick;
        DockPanel.SetDock(save, Dock.Right);
        actions.Children.Add(save);
    }

    private void ApplySelectedTemplate()
    {
        if (updatingTemplates) return;
        var template = SceneTemplate.All.FirstOrDefault(item => item.Name == templateBox.SelectedItem?.ToString());
        if (template is null) return;
        updatingTemplates = true;
        formatBox.SelectedItem = template.Format;
        updatingTemplates = false;
        sources.Clear();
        sources.AddRange(template.Sources.Select(CloneSource));
        DrawTemplatePreview(template.Format, template.Sources);
        templateSummary.Text = $"{template.Name} · {template.Format} · {template.Sources.Count} layers\nCapture, background, phone, camera, and text layers stay editable after you create the scene.";
        RenderSources();
    }

    private void SetTemplateFormat(string format)
    {
        updatingTemplates = true;
        formatBox.SelectedItem = format;
        templateBox.Items.Clear();
        foreach (var template in SceneTemplate.All.Where(item => item.Format == format)) templateBox.Items.Add(template.Name);
        var preferred = format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase) ? "Vertical live" : "Blank";
        templateBox.SelectedItem = templateBox.Items.Cast<string>().FirstOrDefault(item => item == preferred) ?? templateBox.Items.Cast<string>().FirstOrDefault();
        updatingTemplates = false;
        ApplySelectedTemplate();
    }

    private void DrawTemplatePreview(string format, IReadOnlyList<SceneSource> templateSources)
    {
        templatePreview.Children.Clear();
        templatePreview.Background = Brush("#111214");
        var viewport = format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase)
            ? new Rect((templatePreview.Width - templatePreview.Height * 9 / 16) / 2, 0, templatePreview.Height * 9 / 16, templatePreview.Height)
            : new Rect(0, (templatePreview.Height - templatePreview.Width * 9 / 16) / 2, templatePreview.Width, templatePreview.Width * 9 / 16);
        var stage = new Border { Width = viewport.Width, Height = viewport.Height, Background = Brush("#202126"), BorderBrush = Brush("#55565C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        Canvas.SetLeft(stage, viewport.X); Canvas.SetTop(stage, viewport.Y); templatePreview.Children.Add(stage);
        foreach (var source in templateSources.Where(item => item.Enabled))
        {
            var width = viewport.Width * source.Width / 100;
            var height = source.Kind == "Background" ? viewport.Height : source.Height > 0 ? viewport.Height * source.Height / 100 : width / (source.Kind == "Phone" ? 9d / 16d : 16d / 9d);
            var tile = new Border { Width = Math.Max(8, width), Height = Math.Max(8, height), Background = Brush(source.Kind switch { "Text" => "#DDF04455", "Background" => "#151619", "Camera" => "#B02B333D", "Phone" => "#A9401F2A", _ => "#B028303A" }), BorderBrush = Brush(source.Kind == "Text" ? "#FF6978" : "#77787E"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = new TextBlock { Text = source.Kind == "Text" ? source.Content : source.Kind == "Display" ? "DISPLAY" : source.Kind == "App" ? "APP WINDOW" : source.Kind == "Phone" ? "PHONE CAST" : source.Kind == "Camera" ? "CAMERA" : source.Kind == "Background" ? "BACKGROUND" : "VIDEO / IMAGE", Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) } };
            Canvas.SetLeft(tile, viewport.X + viewport.Width * source.X / 100); Canvas.SetTop(tile, viewport.Y + viewport.Height * source.Y / 100); templatePreview.Children.Add(tile);
        }
    }

    private void AddSource(object sender, RoutedEventArgs e)
    {
        var scene = new StudioScene { Name = nameBox.Text.Trim() is { Length: > 0 } n ? n : "New scene", Format = formatBox.SelectedItem?.ToString() ?? "Wide · 16:9" };
        var editor = new SceneSourceEditorWindow(scene) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (editor.Result.Kind == "Camera" && sources.Any(source => source.Kind == "Camera"))
        {
            MessageBox.Show(this, "This scene already has a camera source. Choose one camera layer per scene.", "Camera source already added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (editor.Result.Kind == "Background") sources.Insert(0, editor.Result);
        else sources.Add(editor.Result);
        RenderSources();
    }

    private void EditSource(SceneSource source)
    {
        var scene = new StudioScene { Name = nameBox.Text.Trim(), Format = formatBox.SelectedItem?.ToString() ?? "Wide · 16:9" };
        var editor = new SceneSourceEditorWindow(scene, source) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = sources.FindIndex(item => item.Id == source.Id);
        if (index >= 0)
        {
            sources.RemoveAt(index);
            if (editor.Result.Kind == "Background") sources.Insert(0, editor.Result);
            else sources.Insert(Math.Min(index, sources.Count), editor.Result);
        }
        RenderSources();
    }

    private void RenderSources()
    {
        sourceList.Children.Clear();
        if (sources.Count == 0)
        {
            sourceList.Children.Add(new Border
            {
                Background = Brush("#151619"), BorderBrush = Brush("#38393F"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12),
                Child = new TextBlock { Text = "No overlays yet · Desktop capture will fill this canvas", Foreground = Brush("#999A9E"), FontSize = 11 }
            });
            return;
        }
        foreach (var source in sources)
        {
            var row = new DockPanel { LastChildFill = true, Background = Brush("#222327"), Margin = new Thickness(0, 0, 0, 6) };
            var remove = Button("×", false); remove.MinWidth = 32; remove.Height = 30; remove.Margin = new Thickness(5, 3, 5, 3);
            remove.Click += (_, _) => { sources.Remove(source); RenderSources(); };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var edit = Button("Edit", false); edit.MinWidth = 54; edit.Height = 30; edit.Margin = new Thickness(0, 3, 5, 3);
            edit.Click += (_, _) => EditSource(source);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var description = source.Kind == "Camera" ? "Camera · selected in Settings" : source.Kind == "Image" ? Path.GetFileName(source.Content) : source.Content;
            row.Children.Add(new TextBlock { Text = $"{source.Name}  ·  {description}", Foreground = Brush("#E5E5E6"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 7, 4, 7), TextTrimming = TextTrimming.CharacterEllipsis });
            sourceList.Children.Add(row);
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var sceneName = nameBox.Text.Trim();
        if (sceneName.Length == 0)
        {
            MessageBox.Show(this, "Enter a name for this scene.", "Scene name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var templateName = original is null ? templateBox.SelectedItem?.ToString() ?? "Blank" : original.Template;
        Result = new StudioScene
        {
            Id = original?.Id ?? Guid.NewGuid().ToString("N"),
            Name = sceneName,
            Format = formatBox.SelectedItem?.ToString() ?? "Wide · 16:9",
            Template = templateName,
            Sources = sources.Select(CloneSource).ToList()
        };
        DialogResult = true;
        Close();
    }

    private static SceneSource CloneSource(SceneSource source) => new()
    {
        Id = source.Id, Kind = source.Kind, Name = source.Name, Content = source.Content, Color = source.Color,
        X = source.X, Y = source.Y, Width = source.Width, Height = source.Height, CropLeft = source.CropLeft, CropTop = source.CropTop, CropRight = source.CropRight, CropBottom = source.CropBottom, FontSize = source.FontSize, Enabled = source.Enabled
    };

    private static void AddField(Panel parent, string title, Control field)
    {
        parent.Children.Add(new TextBlock { Text = title, Foreground = Brush("#C7C7C5"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        field.Height = 38; field.Margin = new Thickness(0, 0, 0, 12);
        field.Background = Brush("#222327"); field.Foreground = Brush("#F3F2EF"); field.BorderBrush = Brush("#38393F");
        if (field is TextBox textBox) textBox.Padding = new Thickness(10, 8, 10, 7);
        if (field is ComboBox combo) combo.Padding = new Thickness(8, 6, 8, 6);
        parent.Children.Add(field);
    }

    private static Button Button(string label, bool primary) => new()
    {
        Content = label, MinWidth = primary ? 118 : 82, Height = 36, Padding = new Thickness(12, 0, 12, 0),
        Background = Brush(primary ? "#F04455" : "#222327"), Foreground = Brushes.White,
        BorderBrush = Brush(primary ? "#F04455" : "#38393F"), BorderThickness = new Thickness(1),
        FontSize = 11, FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
