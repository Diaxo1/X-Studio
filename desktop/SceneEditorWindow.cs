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
        AddField(root, "CANVAS FORMAT", formatBox);
        formatBox.Items.Add("Wide · 16:9");
        formatBox.Items.Add("Vertical · 9:16");
        formatBox.SelectedItem = scene?.Format ?? "Wide · 16:9";
        if (scene is null)
        {
            AddField(root, "START FROM TEMPLATE", templateBox);
            foreach (var template in SceneTemplate.All) templateBox.Items.Add(template.Name);
            templateBox.SelectedItem = "Blank";
            templateBox.SelectionChanged += (_, _) => ApplySelectedTemplate();
        }

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
        root.Children.Add(new TextBlock { Text = "Desktop is the base layer. Add camera, image, or text overlays above it. Source positions use canvas percentages.", Foreground = Brush("#999A9E"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });

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
        var template = SceneTemplate.All.FirstOrDefault(item => item.Name == templateBox.SelectedItem?.ToString());
        if (template is null) return;
        formatBox.SelectedItem = template.Format;
        sources.Clear();
        sources.AddRange(template.Sources.Select(CloneSource));
        RenderSources();
    }

    private void AddSource(object sender, RoutedEventArgs e)
    {
        if (sources.Any(source => source.Kind == "Camera"))
        {
            MessageBox.Show(this, "This scene already has a camera source. Choose the camera device in Settings and keep one camera layer per scene.", "Camera source already added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var scene = new StudioScene { Name = nameBox.Text.Trim() is { Length: > 0 } n ? n : "New scene", Format = formatBox.SelectedItem?.ToString() ?? "Wide · 16:9" };
        var editor = new SceneSourceEditorWindow(scene) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        sources.Add(editor.Result);
        RenderSources();
    }

    private void EditSource(SceneSource source)
    {
        var scene = new StudioScene { Name = nameBox.Text.Trim(), Format = formatBox.SelectedItem?.ToString() ?? "Wide · 16:9" };
        var editor = new SceneSourceEditorWindow(scene, source) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = sources.FindIndex(item => item.Id == source.Id);
        if (index >= 0) sources[index] = editor.Result;
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
        X = source.X, Y = source.Y, Width = source.Width, FontSize = source.FontSize, Enabled = source.Enabled
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
