using System.IO;

namespace XStudio;

public sealed class StudioScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New scene";
    public string Format { get; set; } = "Wide · 16:9";
    public string Template { get; set; } = "Blank";
    public List<SceneSource> Sources { get; set; } = [];
}

public sealed class SceneSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Text";
    public string Name { get; set; } = "Text";
    public string Content { get; set; } = "Your text";
    public string Color { get; set; } = "#FFFFFF";
    public double X { get; set; } = 8;
    public double Y { get; set; } = 8;
    public double Width { get; set; } = 42;
    public int FontSize { get; set; } = 48;
    public bool Enabled { get; set; } = true;
}

internal sealed record SceneTemplate(string Name, string Format, IReadOnlyList<SceneSource> Sources)
{
    public StudioScene CreateScene(string sceneName) => new()
    {
        Name = sceneName, Format = Format, Template = Name,
        Sources = Sources.Select(source => new SceneSource
        {
            Kind = source.Kind, Name = source.Name, Content = source.Content, Color = source.Color,
            X = source.X, Y = source.Y, Width = source.Width, FontSize = source.FontSize
        }).ToList()
    };

    private static readonly List<SceneTemplate> templates =
    [
        new("Blank", "Wide · 16:9", []),
        new("Gameplay", "Wide · 16:9", [
            new() { Name = "Live label", Content = "LIVE", Color = "#FF6978", X = 5, Y = 6, Width = 30, FontSize = 24 },
            new() { Name = "Stream title", Content = "YOUR TITLE HERE", X = 5, Y = 78, Width = 58, FontSize = 36 },
            new() { Kind = "Camera", Name = "Face camera", Content = "Settings camera", X = 73, Y = 7, Width = 24 }
        ]),
        new("Just chatting", "Wide · 16:9", [
            new() { Name = "Show title", Content = "LET'S TALK", X = 6, Y = 8, Width = 45, FontSize = 32 },
            new() { Name = "Topic", Content = "Add today's topic", Color = "#FF8790", X = 6, Y = 84, Width = 50, FontSize = 23 },
            new() { Kind = "Camera", Name = "Camera", Content = "Settings camera", X = 62, Y = 22, Width = 33 }
        ]),
        new("Vertical live", "Vertical · 9:16", [
            new() { Name = "Live label", Content = "LIVE NOW", Color = "#FF6978", X = 7, Y = 7, Width = 55, FontSize = 24 },
            new() { Name = "Caption", Content = "YOUR STREAM TITLE", X = 7, Y = 78, Width = 75, FontSize = 30 },
            new() { Kind = "Camera", Name = "Camera", Content = "Settings camera", X = 6, Y = 28, Width = 88 }
        ]),
        new("Phone vertical", "Vertical · 9:16", [
            new() { Kind = "Phone", Name = "Phone screen", Content = "Android", X = 4, Y = 4, Width = 92 },
            new() { Name = "LIVE", Content = "LIVE NOW", Color = "#F04455", X = 6, Y = 88, Width = 48, FontSize = 26 }
        ]),
        new("Screen tutorial", "Wide · 16:9", [
            new() { Kind = "Camera", Name = "Presenter", Content = "Settings camera", X = 74, Y = 70, Width = 22 },
            new() { Name = "Tutorial title", Content = "HOW TO · YOUR TITLE", Color = "#FFFFFF", X = 4, Y = 86, Width = 64, FontSize = 28 }
        ]),
        new("Vertical showcase", "Vertical · 9:16", [
            new() { Kind = "Phone", Name = "Phone screen", Content = "Android", X = 24, Y = 5, Width = 52 },
            new() { Kind = "Camera", Name = "Presenter", Content = "Settings camera", X = 69, Y = 73, Width = 25 },
            new() { Name = "Caption", Content = "YOUR NEXT FIND", Color = "#FFFFFF", X = 7, Y = 87, Width = 75, FontSize = 28 }
        ])
    ];

    public static IReadOnlyList<SceneTemplate> All => templates;

    public static void SaveCustom(string name, StudioScene scene)
    {
        if (BuiltInNames.Contains(name)) throw new InvalidOperationException("Choose a different name from the built-in templates.");
        var custom = templates.Where(template => !BuiltInNames.Contains(template.Name)).ToList();
        custom.RemoveAll(template => template.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        custom.Add(new SceneTemplate(name, scene.Format, scene.Sources.Select(CopySource).ToList()));
        templates.RemoveAll(template => !BuiltInNames.Contains(template.Name));
        templates.AddRange(custom);
        var file = CustomTemplatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var saved = custom.Select(template => new StudioScene { Name = template.Name, Format = template.Format, Sources = template.Sources.Select(CopySource).ToList() }).ToList();
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(saved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly HashSet<string> BuiltInNames = templates.Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static SceneTemplate()
    {
        try
        {
            var file = CustomTemplatePath();
            if (!File.Exists(file)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<StudioScene>>(File.ReadAllText(file)) ?? [];
            foreach (var scene in saved.Where(scene => !BuiltInNames.Contains(scene.Name)))
                templates.Add(new SceneTemplate(scene.Name, scene.Format, scene.Sources.Select(CopySource).ToList()));
        }
        catch (IOException) { }
        catch (System.Text.Json.JsonException) { }
    }

    private static string CustomTemplatePath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStudio", "templates.json");
    private static SceneSource CopySource(SceneSource source) => new() { Kind = source.Kind, Name = source.Name, Content = source.Content, Color = source.Color, X = source.X, Y = source.Y, Width = source.Width, FontSize = source.FontSize, Enabled = source.Enabled };
}

internal sealed class SceneLibrary
{
    private readonly string filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XStudio", "scenes.json");

    public SceneState Load()
    {
        if (!File.Exists(filePath))
        {
            var first = new StudioScene { Name = "Starting soon" };
            return new SceneState([first], first.Id);
        }
        return System.Text.Json.JsonSerializer.Deserialize<SceneState>(File.ReadAllText(filePath))
            ?? new SceneState([], string.Empty);
    }

    public void Save(SceneState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(state,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, filePath, overwrite: true);
    }
}

internal sealed class SceneState(List<StudioScene> scenes, string activeSceneId)
{
    public List<StudioScene> Scenes { get; set; } = scenes;
    public string ActiveSceneId { get; set; } = activeSceneId;
}
