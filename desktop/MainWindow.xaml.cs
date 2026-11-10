using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace XStudio;

public partial class MainWindow : Window
{
    private readonly DestinationStore store = new();
    private readonly SceneLibrary sceneLibrary = new();
    private readonly CapturePreferences preferences = new();
    private readonly StreamEngine engine = new();
    private readonly ScenePreviewService scenePreview = new();
    private readonly PhoneCastService phoneCast = new();
    private readonly MicrophoneTestService microphoneTest = new();
    private readonly LocalRtmpService localRtmp = new();
    private readonly DispatcherTimer previewWatchdog = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<Destination> destinations = [];
    private readonly List<StudioScene> scenes = [];
    private string activeSceneId = string.Empty;
    private bool loaded;
    private bool syncingDevices;
    private bool refreshingScenePicker;
    private bool verticalPreview;
    private bool previewProfileInitialized;
    private bool cropPreviewMode;
    private string cropEdge = string.Empty;
    private double cropStartValue;
    private bool restartingPreview;
    private bool previewRestartPending;
    private bool preparingBroadcast;
    private string? previewInputSignature;
    private SceneSource? draggedPreviewSource;
    private string? selectedPreviewSourceId;
    private Point dragOrigin;
    private double dragSourceX;
    private double dragSourceY;
    private SceneSource? resizingPreviewSource;
    private double resizeStartWidth;
    private double resizeStartSourceHeight;
    private double resizeStartX;
    private double resizeStartY;
    private double resizeStartHeightPercent;
    private Rect resizeStartBounds;
    private double resizeAccumulatedX;
    private double resizeAccumulatedY;
    private BitmapSource? livePreviewFrame;
    private byte[]? pendingPreviewJpeg;
    private int previewFrameDispatchScheduled;
    private readonly Dictionary<string, double> videoDurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> probingVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Slider> videoSliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Border, Point> layerDragOrigins = [];
    private readonly Dictionary<Border, Point> sceneDragOrigins = [];

    public MainWindow()
    {
        InitializeComponent();
        engine.StatusChanged += update => Dispatcher.BeginInvoke(() => ApplyEngineUpdate(update));
        scenePreview.FrameReady += QueuePreviewFrame;
        scenePreview.PreviewFailed += message => Dispatcher.BeginInvoke(() => ApplyPreviewFailure(message));
        microphoneTest.LevelChanged += level => Dispatcher.BeginInvoke(() => UpdateMicrophoneMeter(level));
        microphoneTest.Failed += message => Dispatcher.BeginInvoke(() =>
        {
            MicTestStatus.Text = "Microphone input stopped";
            EngineErrorText.Text = message;
            UpdateMicrophoneMeter(0);
        });
        previewWatchdog.Tick += (_, _) => RefreshLivePreviewInputs();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        try { preferences.Load(); }
        catch (Exception ex) { MessageBox.Show(this, $"X Studio couldn't load capture settings.\n\n{ex.Message}", "Settings error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        try { destinations.AddRange(store.Load()); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"X Studio couldn't decrypt its saved destinations for this Windows account.\n\n{ex.Message}", "Destination storage error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        try
        {
            var savedScenes = sceneLibrary.Load();
            scenes.AddRange(savedScenes.Scenes);
            activeSceneId = savedScenes.ActiveSceneId;
            if (scenes.Count == 0)
            {
                var first = NewScene("Starting soon", "Wide · 16:9");
                scenes.Add(first);
                activeSceneId = first.Id;
                SaveScenes();
            }
            if (!scenes.Any(scene => scene.Id == activeSceneId)) activeSceneId = scenes[0].Id;
            SaveScenes();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"X Studio couldn't load its scene library.\n\n{ex.Message}", "Scene library error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RenderScenes();
        RenderDestinations();
        LoadWindowChoices();
        try
        {
            var devices = await engine.GetAudioDevicesAsync();
            AudioDeviceCombo.Items.Clear();
            SettingsAudioCombo.Items.Clear();
            if (devices.Count == 0)
            {
                AudioDeviceCombo.Items.Add("No microphone detected");
                SettingsAudioCombo.Items.Add("No microphone detected");
            }
            else
            {
                foreach (var device in devices) { AudioDeviceCombo.Items.Add(device); SettingsAudioCombo.Items.Add(device); }
            }
            var audio = devices.Contains(preferences.AudioDevice) ? preferences.AudioDevice : devices.FirstOrDefault();
            AudioDeviceCombo.SelectedItem = SettingsAudioCombo.SelectedItem = audio ?? AudioDeviceCombo.Items[0];
            if (audio is not null) preferences.AudioDevice = audio;
        }
        catch (Exception ex)
        {
            AudioDeviceCombo.Items.Clear();
            AudioDeviceCombo.Items.Add("FFmpeg unavailable");
            SettingsAudioCombo.Items.Clear(); SettingsAudioCombo.Items.Add("FFmpeg unavailable");
            AudioDeviceCombo.SelectedIndex = SettingsAudioCombo.SelectedIndex = 0;
            EncoderStatusText.Text = "FFmpeg unavailable";
            EngineErrorText.Text = ex.Message;
        }
        try
        {
            var cameras = await engine.GetVideoDevicesAsync();
            SettingsCameraCombo.Items.Clear();
            LiveCameraCombo.Items.Clear();
            SettingsCameraCombo.Items.Add("Choose camera…"); LiveCameraCombo.Items.Add("Choose camera…");
            foreach (var camera in cameras) { SettingsCameraCombo.Items.Add(camera); LiveCameraCombo.Items.Add(camera); }
            SettingsCameraCombo.SelectedItem = cameras.Contains(preferences.CameraDevice) ? preferences.CameraDevice : "Choose camera…";
            if (SettingsCameraCombo.SelectedItem?.ToString() == "Choose camera…") preferences.CameraDevice = string.Empty;
            LiveCameraCombo.SelectedItem = SettingsCameraCombo.SelectedItem;
        }
        catch
        {
            SettingsCameraCombo.Items.Clear(); SettingsCameraCombo.Items.Add("Camera discovery unavailable"); SettingsCameraCombo.SelectedIndex = 0;
            LiveCameraCombo.Items.Clear(); LiveCameraCombo.Items.Add("Camera discovery unavailable"); LiveCameraCombo.SelectedIndex = 0;
            preferences.CameraDevice = string.Empty;
        }
        DrawActiveScenePreview();
        previewWatchdog.Start();
        syncingDevices = true;
        MicrophoneEnabled.IsChecked = SettingsMicEnabled.IsChecked = preferences.MicrophoneEnabled;
        AudioDeviceCombo.IsEnabled = SettingsAudioCombo.IsEnabled = preferences.MicrophoneEnabled;
        syncingDevices = false;
        ShowLivePage(null, null!);
        await StartActiveScenePreviewAsync();
    }

    private void ShowLivePage(object? sender, RoutedEventArgs? e)
    {
        LivePage.Visibility = Visibility.Visible;
        DestinationsPage.Visibility = Visibility.Collapsed;
        ScenesPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LiveNav.Tag = "Active";
        DestinationsNav.Tag = null;
        ScenesNav.Tag = null;
        SettingsNav.Tag = null;
        UpdateNavStyles();
        RenderLiveDestinations();
    }

    private void ShowDestinationsPage(object? sender, RoutedEventArgs e)
    {
        LivePage.Visibility = Visibility.Collapsed;
        ScenesPage.Visibility = Visibility.Collapsed;
        DestinationsPage.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LiveNav.Tag = null;
        DestinationsNav.Tag = "Active";
        ScenesNav.Tag = null;
        SettingsNav.Tag = null;
        UpdateNavStyles();
        RenderDestinations();
    }

    private void ShowSettingsPage(object? sender, RoutedEventArgs e)
    {
        LivePage.Visibility = Visibility.Collapsed;
        DestinationsPage.Visibility = Visibility.Collapsed;
        ScenesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        LiveNav.Tag = DestinationsNav.Tag = ScenesNav.Tag = null;
        SettingsNav.Tag = "Active";
        UpdateNavStyles();
    }

    private void ShowScenesPage(object? sender, RoutedEventArgs e)
    {
        LivePage.Visibility = Visibility.Collapsed;
        DestinationsPage.Visibility = Visibility.Collapsed;
        ScenesPage.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LiveNav.Tag = null;
        DestinationsNav.Tag = null;
        ScenesNav.Tag = "Active";
        UpdateNavStyles();
        RenderScenes();
    }

    private void ShowAboutPage(object? sender, RoutedEventArgs e)
    {
        LivePage.Visibility = Visibility.Collapsed;
        DestinationsPage.Visibility = Visibility.Collapsed;
        ScenesPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Visible;
        LiveNav.Tag = null;
        DestinationsNav.Tag = null;
        ScenesNav.Tag = null;
        SettingsNav.Tag = null;
        UpdateNavStyles();
    }

    private void UpdateNavStyles()
    {
        LiveNav.InvalidateVisual();
        DestinationsNav.InvalidateVisual();
        ScenesNav.InvalidateVisual();
        SettingsNav.InvalidateVisual();
    }

    private void LoadWindowChoices()
    {
        CaptureTargetCombo.Items.Clear(); LiveCaptureTargetCombo.Items.Clear();
        var desktop = new CaptureChoice("Full screen · entire desktop", "desktop");
        var transparent = new CaptureChoice("Transparent canvas", "transparent");
        CaptureTargetCombo.Items.Add(transparent); LiveCaptureTargetCombo.Items.Add(transparent);
        CaptureTargetCombo.Items.Add(desktop); LiveCaptureTargetCombo.Items.Add(desktop);
        // App windows are scene layers. Keep the canvas selector focused on
        // the two canvas modes requested: transparent or full-screen desktop.
        var targetValue = preferences.CaptureTarget.StartsWith("window:", StringComparison.OrdinalIgnoreCase)
            ? "desktop" : preferences.CaptureTarget;
        var selected = CaptureTargetCombo.Items.Cast<CaptureChoice>().FirstOrDefault(item => item.Value == targetValue) ?? (CaptureChoice)CaptureTargetCombo.Items[0];
        var migrated = preferences.CaptureTarget != selected.Value;
        preferences.CaptureTarget = selected.Value;
        if (migrated) SaveCapturePreferences();
        CaptureTargetCombo.SelectedItem = selected;
        LiveCaptureTargetCombo.SelectedItem = LiveCaptureTargetCombo.Items.Cast<CaptureChoice>().FirstOrDefault(item => item.Value == selected.Value) ?? LiveCaptureTargetCombo.Items[0];
    }

    private void CaptureTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDevices) return;
        if (CaptureTargetCombo?.SelectedItem is not CaptureChoice choice) return;
        preferences.CaptureTarget = choice.Value;
        if (LiveCaptureTargetCombo.SelectedItem is not CaptureChoice current || current.Value != choice.Value)
        {
            syncingDevices = true; LiveCaptureTargetCombo.SelectedItem = LiveCaptureTargetCombo.Items.Cast<CaptureChoice>().FirstOrDefault(item => item.Value == choice.Value); syncingDevices = false;
        }
        SaveCapturePreferences();
        DrawActiveScenePreview();
        _ = StartActiveScenePreviewAsync();
    }

    private void LiveCaptureTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDevices || LiveCaptureTargetCombo?.SelectedItem is not CaptureChoice choice) return;
        syncingDevices = true; CaptureTargetCombo.SelectedItem = CaptureTargetCombo.Items.Cast<CaptureChoice>().FirstOrDefault(item => item.Value == choice.Value); syncingDevices = false;
        preferences.CaptureTarget = choice.Value;
        SaveCapturePreferences(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync();
    }

    private void LiveCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDevices) return;
        var selected = LiveCameraCombo?.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selected)) return;
        syncingDevices = true;
        SettingsCameraCombo.SelectedItem = selected;
        syncingDevices = false;
        preferences.CameraDevice = selected == "Choose camera…" ? string.Empty : selected;
        SaveCapturePreferences(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync();
    }

    private void CameraDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDevices) return;
        if (SettingsCameraCombo?.SelectedItem is not string device) return;
        preferences.CameraDevice = device is "Choose camera…" or "No camera" || device.StartsWith("Camera", StringComparison.OrdinalIgnoreCase) ? string.Empty : device;
        if (LiveCameraCombo.SelectedItem?.ToString() != device)
        {
            syncingDevices = true; LiveCameraCombo.SelectedItem = device; syncingDevices = false;
        }
        SaveCapturePreferences();
        DrawActiveScenePreview();
        _ = StartActiveScenePreviewAsync();
    }

    private void AudioDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDevices) return;
        var selected = (sender as ComboBox)?.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selected)) return;
        syncingDevices = true;
        if (!AudioDeviceCombo.Items.Contains(selected)) AudioDeviceCombo.Items.Add(selected);
        if (!SettingsAudioCombo.Items.Contains(selected)) SettingsAudioCombo.Items.Add(selected);
        AudioDeviceCombo.SelectedItem = SettingsAudioCombo.SelectedItem = selected;
        syncingDevices = false;
        preferences.AudioDevice = selected is "No microphone detected" or "FFmpeg unavailable" ? string.Empty : selected;
        microphoneTest.Stop();
        if (MicTestButton is not null) MicTestButton.Content = "Test microphone";
        if (MicTestStatus is not null) MicTestStatus.Text = "Ready to test";
        if (MicMeterFill is not null) MicMeterFill.Width = 0;
        SaveCapturePreferences();
    }

    private async void MicTestClick(object sender, RoutedEventArgs e)
    {
        if (engine.IsRunning)
        {
            MessageBox.Show(this, "Stop the broadcast before testing the microphone.", "Microphone is live", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (microphoneTest.IsRunning)
        {
            microphoneTest.Stop();
            MicTestButton.Content = "Test microphone";
            MicTestStatus.Text = "Test stopped";
            UpdateMicrophoneMeter(0);
            return;
        }
        var device = preferences.AudioDevice;
        if (string.IsNullOrWhiteSpace(device))
        {
            MicTestStatus.Text = "Choose a microphone first";
            return;
        }
        try
        {
            EngineErrorText.Text = string.Empty;
            MicTestStatus.Text = "Listening · speak now";
            MicTestButton.Content = "Stop test";
            await microphoneTest.StartAsync(device);
        }
        catch (Exception ex)
        {
            microphoneTest.Stop();
            MicTestButton.Content = "Test microphone";
            MicTestStatus.Text = "Could not open microphone";
            EngineErrorText.Text = ex.Message;
        }
    }

    private void UpdateMicrophoneMeter(double level)
    {
        if (MicMeterTrack is null || MicMeterFill is null) return;
        MicMeterFill.Width = Math.Max(0, (MicMeterTrack.ActualWidth - 2) * Math.Clamp(level, 0, 100) / 100);
        if (level > 1 && microphoneTest.IsRunning) MicTestStatus.Text = $"Mic signal · {level:0}%";
    }

    private void QualityChanged(object sender, SelectionChangedEventArgs e) => DrawActiveScenePreview();

    private void SettingsMicToggleChanged(object sender, RoutedEventArgs e)
    {
        if (syncingDevices || MicrophoneEnabled is null || SettingsMicEnabled is null || AudioDeviceCombo is null || SettingsAudioCombo is null) return;
        syncingDevices = true;
        MicrophoneEnabled.IsChecked = SettingsMicEnabled.IsChecked;
        AudioDeviceCombo.IsEnabled = SettingsAudioCombo.IsEnabled = SettingsMicEnabled.IsChecked == true;
        syncingDevices = false;
        preferences.MicrophoneEnabled = SettingsMicEnabled.IsChecked == true;
        SaveCapturePreferences();
    }

    private void SaveCapturePreferences()
    {
        try { preferences.Save(); }
        catch (Exception ex) { MessageBox.Show(this, $"X Studio couldn't save capture settings.\n\n{ex.Message}", "Settings save failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RefreshInputsClick(object sender, RoutedEventArgs e)
    {
        if (engine.IsRunning)
        {
            MessageBox.Show(this, "Stop the broadcast before refreshing capture devices.", "Broadcast is active", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        syncingDevices = true;
        try
        {
            LoadWindowChoices();
            var audioDevices = await engine.GetAudioDevicesAsync();
            AudioDeviceCombo.Items.Clear(); SettingsAudioCombo.Items.Clear();
            var audioChoices = audioDevices.Count > 0 ? audioDevices : ["No microphone detected"];
            foreach (var device in audioChoices) { AudioDeviceCombo.Items.Add(device); SettingsAudioCombo.Items.Add(device); }
            var audio = audioDevices.Contains(preferences.AudioDevice) ? preferences.AudioDevice : audioDevices.FirstOrDefault();
            AudioDeviceCombo.SelectedItem = SettingsAudioCombo.SelectedItem = audio ?? audioChoices[0];
            preferences.AudioDevice = audio ?? string.Empty;

            var cameraDevices = await engine.GetVideoDevicesAsync();
            SettingsCameraCombo.Items.Clear(); LiveCameraCombo.Items.Clear();
            SettingsCameraCombo.Items.Add("Choose camera…"); LiveCameraCombo.Items.Add("Choose camera…");
            foreach (var device in cameraDevices) { SettingsCameraCombo.Items.Add(device); LiveCameraCombo.Items.Add(device); }
            SettingsCameraCombo.SelectedItem = cameraDevices.Contains(preferences.CameraDevice) ? preferences.CameraDevice : "Choose camera…";
            LiveCameraCombo.SelectedItem = SettingsCameraCombo.SelectedItem;
            preferences.CameraDevice = cameraDevices.Contains(preferences.CameraDevice) ? preferences.CameraDevice : string.Empty;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"X Studio couldn't refresh capture devices.\n\n{ex.Message}", "Device refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AudioDeviceCombo.IsEnabled = SettingsAudioCombo.IsEnabled = preferences.MicrophoneEnabled;
            syncingDevices = false;
            SaveCapturePreferences();
            DrawActiveScenePreview();
            _ = StartActiveScenePreviewAsync();
        }
    }

    private void AddSceneClick(object sender, RoutedEventArgs e)
    {
        var editor = new SceneEditorWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        scenes.Add(editor.Result);
        activeSceneId = editor.Result.Id;
        SaveScenes();
        RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private void EditScene(StudioScene scene)
    {
        var editor = new SceneEditorWindow(scene) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = scenes.FindIndex(item => item.Id == scene.Id);
        if (index >= 0) scenes[index] = editor.Result;
        SaveScenes();
        RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private void ActivateScene(StudioScene scene)
    {
        activeSceneId = scene.Id;
        SaveScenes();
        RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private void DuplicateScene(StudioScene scene)
    {
        var copy = new StudioScene
        {
            Name = $"{scene.Name} copy", Format = scene.Format, Template = scene.Template,
            Sources = scene.Sources.Select(source => new SceneSource
            {
                Kind = source.Kind, Name = source.Name, Content = source.Content, Color = source.Color,
                X = source.X, Y = source.Y, Width = source.Width, Height = source.Height, CropLeft = source.CropLeft, CropTop = source.CropTop, CropRight = source.CropRight, CropBottom = source.CropBottom, FontSize = source.FontSize, Enabled = source.Enabled, PlaybackPositionSeconds = source.PlaybackPositionSeconds, WindowProcessId = source.WindowProcessId
            }).ToList()
        };
        scenes.Insert(scenes.IndexOf(scene) + 1, copy);
        activeSceneId = copy.Id;
        SaveScenes();
        RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private void RemoveScene(StudioScene scene)
    {
        if (scenes.Count == 1)
        {
            MessageBox.Show(this, "Keep at least one scene in your library.", "Last scene", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete the scene ‘{scene.Name}’?", "Delete scene", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        scenes.RemoveAll(item => item.Id == scene.Id);
        if (activeSceneId == scene.Id) activeSceneId = scenes[0].Id;
        SaveScenes();
        RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private void SaveScenes()
    {
        try { sceneLibrary.Save(new SceneState(scenes, activeSceneId)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"X Studio couldn't save your scene library.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderScenes()
    {
        SceneCardList.Items.Clear();
        SceneCountText.Text = $"{scenes.Count} {(scenes.Count == 1 ? "scene" : "scenes")}";
        refreshingScenePicker = true;
        ActiveSceneCombo.Items.Clear();
        foreach (var item in scenes) ActiveSceneCombo.Items.Add(new SceneChoice(item.Name, item.Id));
        ActiveSceneCombo.SelectedItem = ActiveSceneCombo.Items.Cast<SceneChoice>().FirstOrDefault(item => item.Id == activeSceneId);
        refreshingScenePicker = false;
        foreach (var scene in scenes)
        {
            var active = scene.Id == activeSceneId;
            var portrait = scene.Format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase);
            var card = new Border
            {
                Background = Brush(active ? "#21191C" : "#191A1D"),
                BorderBrush = Brush(active ? "#8A3541" : "#38393F"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 11, 11),
                Width = 248,
                Tag = scene.Id,
                AllowDrop = true
            };
            card.PreviewMouseLeftButtonDown += (_, e) => sceneDragOrigins[card] = e.GetPosition(card);
            card.PreviewMouseMove += SceneCardPreviewMouseMove;
            card.DragOver += SceneCardDragOver;
            card.Drop += SceneCardDrop;
            var stack = new StackPanel();
            card.Child = stack;
            var preview = new Border
            {
                Height = 116,
                Background = Brush("#101113"),
                BorderBrush = Brush("#38393F"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 11)
            };
            var aspectFrame = new Border
            {
                Width = portrait ? 54 : 108,
                Height = portrait ? 96 : 61,
                Background = Brush("#1C1D21"),
                BorderBrush = Brush(active ? "#F04455" : "#55565C"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            aspectFrame.Child = BuildScenePreview(scene, aspectFrame.Width, aspectFrame.Height);
            preview.Child = aspectFrame;
            stack.Children.Add(preview);

            var heading = new DockPanel();
            var badge = MakeText(active ? "ACTIVE" : scene.Format.ToUpperInvariant(), 9, Brush(active ? "#FF6978" : "#999A9E"));
            badge.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(badge, Dock.Right);
            heading.Children.Add(badge);
            heading.Children.Add(new TextBlock { Text = scene.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F3F2EF"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(heading);
            var sourceCount = scene.Sources.Count(source => source.Enabled);
            stack.Children.Add(new TextBlock { Text = $"{scene.Format} · {sourceCount} {(sourceCount == 1 ? "source" : "sources")} · {scene.Template}", FontSize = 10, Foreground = Brush("#999A9E"), Margin = new Thickness(0, 4, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });

            var actions = new WrapPanel();
            if (!active)
            {
                var use = MakeButton("Make active", true);
                use.Click += (_, _) => ActivateScene(scene);
                actions.Children.Add(use);
            }
            var edit = MakeButton("Edit", false);
            edit.Margin = new Thickness(5, 0, 0, 0);
            edit.Click += (_, _) => EditScene(scene);
            actions.Children.Add(edit);
            var duplicate = MakeButton("Copy", false);
            duplicate.Margin = new Thickness(5, 0, 0, 0);
            duplicate.Click += (_, _) => DuplicateScene(scene);
            actions.Children.Add(duplicate);
            var remove = MakeButton("Delete", false);
            remove.Margin = new Thickness(5, 0, 0, 0);
            remove.Click += (_, _) => RemoveScene(scene);
            actions.Children.Add(remove);
            stack.Children.Add(actions);
            SceneCardList.Items.Add(card);
        }
        var current = scenes.FirstOrDefault(scene => scene.Id == activeSceneId);
        if (current is not null)
        {
            ActiveSceneText.Text = current.Name;
            if (!previewProfileInitialized)
            {
                verticalPreview = current.Format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase);
                previewProfileInitialized = true;
            }
            UpdatePreviewProfileButtons();
        }
        RenderSceneLayers();
        DrawActiveScenePreview();
    }

    private void RenderSceneLayers()
    {
        SceneLayerList.Items.Clear();
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        var layers = scene?.Sources.AsEnumerable().Reverse().ToList() ?? [];
        SceneLayerEmptyText.Visibility = layers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < layers.Count; index++)
        {
            var source = layers[index];
            var order = index == 0 ? "FRONT" : index == layers.Count - 1 ? "BACK" : $"LAYER {index + 1}";
            var row = new Border
            {
                Tag = new LayerChoice(source.Id), AllowDrop = true,
                Background = Brush(source.Enabled ? "#222327" : "#1A1B1F"),
                BorderBrush = Brush("#38393F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 8, 7), Margin = new Thickness(0, 0, 0, 5),
                ToolTip = "Drag this row to change which source appears in front"
            };
            row.PreviewMouseMove += SceneLayerPreviewMouseMove;
            row.PreviewMouseLeftButtonDown += (_, e) => layerDragOrigins[row] = e.GetPosition(row);
            row.DragOver += SceneLayerDragOver;
            row.Drop += SceneLayerDrop;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = source.Name, Foreground = Brush(source.Enabled ? "#F3F2EF" : "#77787E"), FontSize = 11, FontWeight = FontWeights.SemiBold });
            copy.Children.Add(new TextBlock { Text = $"{order} · {source.Kind.ToUpperInvariant()}{(source.Enabled ? string.Empty : " · HIDDEN")}", Foreground = Brush("#999A9E"), FontSize = 9, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(copy);
            var front = MakeButton("Front", false); front.Height = 25; front.Margin = new Thickness(6, 0, 0, 0);
            front.Click += (_, _) => MoveSourceLayer(source.Id, true);
            Grid.SetColumn(front, 1); grid.Children.Add(front);
            var back = MakeButton("Back", false); back.Height = 25; back.Margin = new Thickness(6, 0, 0, 0);
            back.Click += (_, _) => MoveSourceLayer(source.Id, false);
            Grid.SetColumn(back, 2); grid.Children.Add(back);
            var isVideo = IsVideoFile(source.Content) && File.Exists(source.Content);
            if (!isVideo)
            {
                var visibility = MakeButton(source.Enabled ? "Hide" : "Show", false); visibility.Height = 25; visibility.Margin = new Thickness(6, 0, 0, 0);
                visibility.Click += (_, _) => { source.Enabled = !source.Enabled; SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync(); };
                Grid.SetColumn(visibility, 3); grid.Children.Add(visibility);
            }
            if (isVideo)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(copy, 0); Grid.SetColumnSpan(copy, 4);
                var controls = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
                var toggle = MakeButton(source.Enabled ? "Stop" : "Play", false); toggle.Height = 24;
                toggle.Click += (_, _) => { source.Enabled = !source.Enabled; SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync(); };
                DockPanel.SetDock(toggle, Dock.Left); controls.Children.Add(toggle);
                var seek = new Slider { Minimum = 0, Maximum = videoDurations.GetValueOrDefault(source.Id, 100), Value = Math.Clamp(source.PlaybackPositionSeconds, 0, videoDurations.GetValueOrDefault(source.Id, 100)), IsEnabled = videoDurations.ContainsKey(source.Id), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Seek video" };
                seek.ValueChanged += (_, _) => { if (seek.IsMouseCaptureWithin) source.PlaybackPositionSeconds = seek.Value; };
                seek.PreviewMouseLeftButtonUp += (_, _) => { SaveScenes(); _ = StartActiveScenePreviewAsync(); };
                videoSliders[source.Id] = seek;
                controls.Children.Add(seek);
                Grid.SetRow(controls, 1); Grid.SetColumnSpan(controls, 4); grid.Children.Add(controls);
                if (!videoDurations.ContainsKey(source.Id) && probingVideoIds.Add(source.Id)) _ = ProbeVideoDurationAsync(source);
            }
            row.Child = grid;
            SceneLayerList.Items.Add(row);
        }
    }

    private void MoveSourceLayer(string sourceId, bool toFront)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null) return;
        var visualOrder = scene.Sources.AsEnumerable().Reverse().ToList();
        var index = visualOrder.FindIndex(source => source.Id == sourceId);
        if (index < 0) return;
        var source = visualOrder[index]; visualOrder.RemoveAt(index);
        visualOrder.Insert(toFront ? 0 : visualOrder.Count, source);
        scene.Sources = visualOrder.AsEnumerable().Reverse().ToList();
        SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync();
    }

    private void SceneLayerPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Border row || row.Tag is not LayerChoice layer || !layerDragOrigins.TryGetValue(row, out var origin)) return;
        if (e.OriginalSource is DependencyObject original && HasButtonAncestor(original, row)) return;
        var point = e.GetPosition(row);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(row, new DataObject("XStudio.SceneLayer", layer.SourceId), DragDropEffects.Move);
    }

    private void SceneLayerDragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent("XStudio.SceneLayer") ? DragDropEffects.Move : DragDropEffects.None;

    private void SceneLayerDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: LayerChoice target } || !e.Data.GetDataPresent("XStudio.SceneLayer")) return;
        var sourceId = e.Data.GetData("XStudio.SceneLayer") as string;
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (sourceId is null || scene is null || sourceId == target.SourceId) return;
        var visualOrder = scene.Sources.AsEnumerable().Reverse().ToList();
        var sourceIndex = visualOrder.FindIndex(source => source.Id == sourceId);
        var targetIndex = visualOrder.FindIndex(source => source.Id == target.SourceId);
        if (sourceIndex < 0 || targetIndex < 0) return;
        var source = visualOrder[sourceIndex]; visualOrder.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex) targetIndex--;
        visualOrder.Insert(targetIndex, source);
        scene.Sources = visualOrder.AsEnumerable().Reverse().ToList();
        SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync();
        e.Handled = true;
    }

    private void SceneCardPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Border card || card.Tag is not string sceneId || !sceneDragOrigins.TryGetValue(card, out var origin)) return;
        if (e.OriginalSource is DependencyObject original && HasButtonAncestor(original, card)) return;
        var point = e.GetPosition(card);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(card, new DataObject("XStudio.Scene", sceneId), DragDropEffects.Move);
    }

    private static bool HasButtonAncestor(DependencyObject element, DependencyObject stop)
    {
        for (var current = element; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private async Task ProbeVideoDurationAsync(SceneSource source)
    {
        try
        {
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(source.Content);
            using var process = Process.Start(start);
            if (process is null) return;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var match = System.Text.RegularExpressions.Regex.Match(error, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success || !double.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return;
            var duration = int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60 + seconds;
            Dispatcher.Invoke(() =>
            {
                videoDurations[source.Id] = duration;
                if (videoSliders.TryGetValue(source.Id, out var slider)) { slider.Maximum = duration; slider.IsEnabled = true; }
            });
        }
        catch { }
        finally { probingVideoIds.Remove(source.Id); }
    }

    private void SceneCardDragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent("XStudio.Scene") ? DragDropEffects.Move : DragDropEffects.None;

    private void SceneCardDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border target || target.Tag is not string targetId || !e.Data.GetDataPresent("XStudio.Scene")) return;
        var sourceId = e.Data.GetData("XStudio.Scene") as string;
        if (sourceId is null || sourceId == targetId) return;
        var from = scenes.FindIndex(scene => scene.Id == sourceId);
        var to = scenes.FindIndex(scene => scene.Id == targetId);
        if (from < 0 || to < 0) return;
        var scene = scenes[from]; scenes.RemoveAt(from);
        if (from < to) to--;
        var point = e.GetPosition(target);
        if (point.X > target.ActualWidth / 2) to++;
        scenes.Insert(Math.Clamp(to, 0, scenes.Count), scene);
        SaveScenes(); RenderScenes();
        e.Handled = true;
    }

    private void ActiveSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingScenePicker || ActiveSceneCombo?.SelectedItem is not SceneChoice choice) return;
        var scene = scenes.FirstOrDefault(item => item.Id == choice.Id);
        if (scene is null) return;
        activeSceneId = scene.Id; SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync();
    }

    private void NewSceneClick(object sender, RoutedEventArgs e) => AddSceneClick(sender, e);

    private void AddSourceToActiveSceneClick(object sender, RoutedEventArgs e)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null) return;
        var editor = new SceneSourceEditorWindow(scene) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (editor.Result.Kind == "Camera" && scene.Sources.Any(source => source.Kind == "Camera"))
        {
            MessageBox.Show(this, "This scene already has a camera source. Keep one camera layer per scene.", "Camera already added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (editor.Result.Kind == "Background") scene.Sources.Insert(0, editor.Result); else scene.Sources.Add(editor.Result);
        SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync();
        if (editor.Result.Kind == "Phone")
        {
            if (editor.Result.Content == "Android") _ = StartAndroidFromDeskAsync();
            else _ = StartIPhoneFromDeskAsync();
        }
    }

    private void SaveActiveAsTemplateClick(object sender, RoutedEventArgs e)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null) return;
        var title = new Window
        {
            Title = "Save layout template · X Studio", Width = 420, Height = 210, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = Brush("#191A1D"), Foreground = Brush("#F3F2EF"), FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };
        var panel = new StackPanel { Margin = new Thickness(22) }; title.Content = panel;
        panel.Children.Add(new TextBlock { Text = "SAVE YOUR LAYOUT", Foreground = Brush("#FF6978"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        panel.Children.Add(new TextBlock { Text = "Create a reusable template", FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        var name = new TextBox { Text = scene.Name, Height = 36, Padding = new Thickness(9, 7, 9, 6), Background = Brush("#222327"), Foreground = Brush("#F3F2EF"), BorderBrush = Brush("#38393F") }; panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) }; panel.Children.Add(actions);
        var cancel = MakeButton("Cancel", false); cancel.Click += (_, _) => title.DialogResult = false; actions.Children.Add(cancel);
        var save = MakeButton("Save template", true); save.Margin = new Thickness(8, 0, 0, 0); save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(title, "Enter a template name.", "Name needed", MessageBoxButton.OK, MessageBoxImage.Information); return; } title.Tag = name.Text.Trim(); title.DialogResult = true; }; actions.Children.Add(save);
        if (title.ShowDialog() != true || title.Tag is not string templateName) return;
        try
        {
            SceneTemplate.SaveCustom(templateName, scene);
            showSceneTemplateToast(templateName);
        }
        catch (Exception ex) { MessageBox.Show(this, $"X Studio couldn't save this template.\n\n{ex.Message}", "Template save failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void showSceneTemplateToast(string name) => MessageBox.Show(this, $"‘{name}’ is now available in the Templates picker when you create a scene.", "Template saved", MessageBoxButton.OK, MessageBoxImage.Information);

    private static StudioScene NewScene(string name, string format) => new()
    {
        Name = name, Format = format, Template = "Blank",
        Sources = [new SceneSource { Kind = "Background", Name = "Transparent canvas", Content = "transparent", X = 0, Y = 0, Width = 100, Height = 100 }]
    };

    private void AddDestinationClick(object sender, RoutedEventArgs e)
    {
        var editor = new DestinationEditorWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        destinations.Add(editor.Result);
        SaveDestinations();
        RenderDestinations();
        RenderLiveDestinations();
    }

    private async void AddLocalRtmpClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await localRtmp.StartAsync();
            var destination = destinations.FirstOrDefault(item => item.Id == "xstudio-local-rtmp");
            if (destination is null)
            {
                destination = new Destination { Id = "xstudio-local-rtmp", Platform = "Local RTMP", Name = "Local RTMP test", ServerUrl = $"rtmp://127.0.0.1:{localRtmp.Port}/xstudio", StreamKey = "alpha", UseForStream = true };
                destinations.Add(destination);
            }
            else { destination.ServerUrl = $"rtmp://127.0.0.1:{localRtmp.Port}/xstudio"; destination.UseForStream = true; }
            SaveDestinations(); RenderDestinations(); RenderLiveDestinations();
            MessageBox.Show(this, $"{status}\n\nLocal output added and selected: rtmp://127.0.0.1:{localRtmp.Port}/xstudio/alpha\n\nStart broadcast from Live Desk to publish. This server listens on this PC only.", "Local RTMP ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Local RTMP unavailable", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void StartAndroidCastClick(object sender, RoutedEventArgs e)
    {
        await StartAndroidFromDeskAsync();
    }

    private async void StartIPhoneCastClick(object sender, RoutedEventArgs e)
    {
        await StartIPhoneFromDeskAsync();
    }

    private void StopPhoneCastClick(object sender, RoutedEventArgs e)
    {
        phoneCast.StopAndroid(); phoneCast.StopIPhone();
        PhoneCastStatus.Text = "Phone cast receivers stopped.";
        DrawActiveScenePreview();
        _ = StartActiveScenePreviewAsync();
    }

    private void EditDestination(Destination destination)
    {
        var editor = new DestinationEditorWindow(destination) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = destinations.FindIndex(item => item.Id == destination.Id);
        if (index >= 0) destinations[index] = editor.Result;
        SaveDestinations();
        RenderDestinations();
        RenderLiveDestinations();
    }

    private void RemoveDestination(Destination destination)
    {
        if (engine.IsRunning)
        {
            MessageBox.Show(this, "Stop the current broadcast before removing a destination.", "Broadcast is active", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(this, $"Remove {destination.Name} from X Studio? Its saved key will also be removed.", "Remove destination", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        destinations.RemoveAll(item => item.Id == destination.Id);
        SaveDestinations();
        RenderDestinations();
        RenderLiveDestinations();
    }

    private void ToggleDestination(Destination destination)
    {
        destination.UseForStream = !destination.UseForStream;
        SaveDestinations();
        RenderDestinations();
        RenderLiveDestinations();
    }

    private void SaveDestinations()
    {
        try { store.Save(destinations); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"X Studio couldn't save the destination list.\n\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderDestinations()
    {
        DestinationProfileList.Items.Clear();
        EmptyDestinationsText.Visibility = destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var destination in destinations)
        {
            var card = new Border
            {
                Background = Brush("#191A1D"),
                BorderBrush = Brush("#38393F"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 9)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Child = grid;

            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = destination.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F3F2EF") });
            var outputShape = destination.Platform.Contains("TikTok", StringComparison.OrdinalIgnoreCase) ? "Portrait · 9:16" : "Wide · 16:9";
            details.Children.Add(new TextBlock { Text = $"{destination.Platform}  ·  {outputShape}  ·  {GetServerLabel(destination.ServerUrl)}", FontSize = 11, Foreground = Brush("#999A9E"), Margin = new Thickness(0, 4, 0, 0) });
            details.Children.Add(new TextBlock { Text = destination.StreamKey.Length > 0 ? "Stream key saved · encrypted for this Windows account" : "Stream key missing", FontSize = 10, Foreground = Brush(destination.StreamKey.Length > 0 ? "#B6B7BB" : "#FF8790"), Margin = new Thickness(0, 5, 0, 0) });
            grid.Children.Add(details);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var useButton = MakeButton(destination.UseForStream ? "IN NEXT STREAM" : "ADD TO STREAM", destination.UseForStream);
            useButton.Click += (_, _) => ToggleDestination(destination);
            actions.Children.Add(useButton);
            var edit = MakeButton("Edit", false);
            edit.Margin = new Thickness(7, 0, 0, 0);
            edit.Click += (_, _) => EditDestination(destination);
            actions.Children.Add(edit);
            var remove = MakeButton("Remove", false);
            remove.Margin = new Thickness(7, 0, 0, 0);
            remove.Click += (_, _) => RemoveDestination(destination);
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            DestinationProfileList.Items.Add(card);
        }
    }

    private void RenderLiveDestinations()
    {
        LiveDestinationList.Items.Clear();
        NoDestinationText.Visibility = destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var destination in destinations)
        {
            var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
            var state = MakeText(engineStateFor(destination), 10, Brush("#999A9E"));
            DockPanel.SetDock(state, Dock.Right);
            line.Children.Add(state);
            var select = new Button
            {
                Content = destination.UseForStream ? "✓" : "+",
                Width = 21,
                Height = 21,
                Margin = new Thickness(0, 0, 8, 0),
                Background = destination.UseForStream ? Brush("#F04455") : Brush("#292A2F"),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = destination.UseForStream ? "Remove from next stream" : "Include in next stream"
            };
            select.Click += (_, _) => ToggleDestination(destination);
            DockPanel.SetDock(select, Dock.Left);
            line.Children.Add(select);
            var name = MakeText($"{destination.Name} · {(destination.Platform.Contains("TikTok", StringComparison.OrdinalIgnoreCase) ? "PORTRAIT" : "WIDE")}", 11, Brush("#F3F2EF"));
            name.FontWeight = FontWeights.SemiBold;
            line.Children.Add(name);
            LiveDestinationList.Items.Add(line);
        }
    }

    private string engineStateFor(Destination destination)
    {
        if (destinationStatesView.TryGetValue(destination.Id, out var currentState)) return currentState;
        if (!engine.IsRunning) return destination.UseForStream ? "Selected" : "Not selected";
        return destination.UseForStream ? "In this stream" : "Standby";
    }

    private async void StartStopClick(object sender, RoutedEventArgs e)
    {
        if (engine.IsRunning)
        {
            engine.Stop();
            return;
        }
        var selected = destinations.Where(destination => destination.UseForStream).ToList();
        if (selected.Count == 0)
        {
            if (destinations.Count == 0) ShowDestinationsPage(null, null!);
            MessageBox.Show(this, destinations.Count == 0
                ? "Add an RTMP destination first, then mark it for the next stream."
                : "Choose at least one destination for this stream.", "No destination selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var activeScene = scenes.FirstOrDefault(scene => scene.Id == activeSceneId);
        if (activeScene is null) { MessageBox.Show(this, "Create or choose a scene before starting the broadcast.", "No active scene", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var audio = preferences.MicrophoneEnabled ? preferences.AudioDevice : null;
        if (audio is "No microphone detected" or "FFmpeg unavailable") audio = null;
        if (preferences.MicrophoneEnabled && string.IsNullOrWhiteSpace(audio))
        {
            MessageBox.Show(this, "Choose a microphone or turn off ‘Include mic’ before starting the broadcast.", "Microphone not selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var camera = string.IsNullOrWhiteSpace(preferences.CameraDevice) ? null : preferences.CameraDevice;
        var captureTarget = preferences.CaptureTarget.StartsWith("window:", StringComparison.OrdinalIgnoreCase) ? preferences.CaptureTarget[7..] : preferences.CaptureTarget;
        if (activeScene.Sources.Any(source => source.Enabled && source.Kind == "Phone" && source.Content == "Android") && phoneCast.AndroidStatus != "Android receiver running")
        {
            try { PhoneCastStatus.Text = await phoneCast.StartAndroidAsync(); LoadWindowChoices(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Android phone cast", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        }
        if (activeScene.Sources.Any(source => source.Enabled && source.Kind == "Phone" && source.Content == "iPhone") && phoneCast.IPhoneStatus != "AirPlay receiver running")
        {
            try { PhoneCastStatus.Text = await phoneCast.StartIPhoneAsync(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "iPhone phone cast", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        }
        preparingBroadcast = true;
        var phoneInputs = GetPhoneCaptureInputs(activeScene);
        microphoneTest.Stop();
        MicTestButton.Content = "Test microphone";
        MicTestStatus.Text = audio is null ? "Ready to test" : "Checking microphone…";
        UpdateMicrophoneMeter(0);
        if (audio is not null)
        {
            try { await microphoneTest.ProbeAsync(audio); }
            catch (Exception ex)
            {
                EngineErrorText.Text = $"Microphone check failed: {ex.Message}";
                MessageBox.Show(this, EngineErrorText.Text, "Microphone unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
                preparingBroadcast = false;
                _ = StartActiveScenePreviewAsync();
                return;
            }
        }
        if (audio is not null) MicTestStatus.Text = "Microphone ready for stream";
        var qualityTag = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "720p30";
        try
        {
            EngineErrorText.Text = string.Empty;
            await scenePreview.StopAsync();
            PreviewStatusText.Text = "PREVIEW PAUSED DURING BROADCAST";
            await engine.StartAsync(selected, activeScene, captureTarget, camera, audio, qualityTag, phoneInputs);
        }
        catch (Exception ex)
        {
            EngineErrorText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Could not start the broadcast", MessageBoxButton.OK, MessageBoxImage.Error);
            _ = StartActiveScenePreviewAsync();
        }
        finally { preparingBroadcast = false; }
    }

    private void ApplyEngineUpdate(EngineUpdate update)
    {
        EngineStatusText.Text = update.State switch
        {
            "Live" => "Streaming",
            "Starting" => "Starting stream",
            "Stopping" => "Stopping stream",
            "Fallback" => "Switching encoder",
            "Error" => "Stream error",
            _ => "Engine idle"
        };
        EngineDot.Fill = Brush(update.State switch { "Live" => "#F04455", "Error" => "#FF8790", "Starting" or "Stopping" or "Fallback" => "#D3B079", _ => "#77787E" });
        StartStopButton.Content = update.State is "Live" or "Starting" or "Fallback" or "Stopping" ? "■  Stop broadcast" : "◉  Start broadcast";
        StartStopButton.IsEnabled = update.State != "Stopping";
        EncoderStatusText.Text = update.Encoder;
        CaptureStatusText.Text = update.State == "Live" ? "Scene output active" : update.State == "Starting" ? "Preparing scene…" : "Ready for capture";
        EngineErrorText.Text = update.State is "Error" or "Fallback" ? update.Message : string.Empty;
        foreach (var destination in destinations)
        {
            if (update.DestinationStates.TryGetValue(destination.Id, out var targetState))
                destinationStatesView[destination.Id] = targetState;
            else
                destinationStatesView[destination.Id] = destination.UseForStream ? "Selected" : "Not selected";
        }
        RenderLiveDestinations();
        RenderDestinations();
        if (update.State is "Live" or "Starting" or "Fallback" or "Stopping")
        {
            scenePreview.Stop();
            PreviewStatusText.Text = "PREVIEW PAUSED DURING BROADCAST";
        }
        else if (update.State is "Idle" or "Error") _ = StartActiveScenePreviewAsync();
    }

    private readonly Dictionary<string, string> destinationStatesView = [];

    private void MicrophoneToggleChanged(object sender, RoutedEventArgs e)
    {
        // XAML can raise Checked/Unchecked while InitializeComponent is still
        // constructing the visual tree, before the named combo is assigned.
        if (AudioDeviceCombo is not null && MicrophoneEnabled is not null)
            AudioDeviceCombo.IsEnabled = MicrophoneEnabled.IsChecked == true;
        if (!syncingDevices && MicrophoneEnabled is not null && SettingsMicEnabled is not null)
        {
            syncingDevices = true;
            SettingsMicEnabled.IsChecked = MicrophoneEnabled.IsChecked;
            SettingsAudioCombo.IsEnabled = MicrophoneEnabled.IsChecked == true;
            syncingDevices = false;
            preferences.MicrophoneEnabled = MicrophoneEnabled.IsChecked == true;
            SaveCapturePreferences();
        }
    }

    private void WidePreviewClick(object sender, RoutedEventArgs e) => ActivateFormatProfile(vertical: false);

    private void TikTokPreviewClick(object sender, RoutedEventArgs e) => ActivateFormatProfile(vertical: true);

    private void ActivateFormatProfile(bool vertical)
    {
        if (verticalPreview == vertical) return;
        verticalPreview = vertical;
        UpdatePreviewProfileButtons();
        PreviewStatusText.Text = vertical ? "SWITCHING PREVIEW TO TIKTOK · 9:16" : "SWITCHING PREVIEW TO WIDE · 16:9";
        DrawActiveScenePreview();
        _ = StartActiveScenePreviewAsync();
    }

    private void UpdatePreviewProfileButtons()
    {
        if (WidePreviewButton is null || TikTokPreviewButton is null) return;
        WidePreviewButton.Background = Brush(verticalPreview ? "#222327" : "#321C21");
        WidePreviewButton.Foreground = Brush(verticalPreview ? "#D3D3D5" : "#FF6978");
        TikTokPreviewButton.Background = Brush(verticalPreview ? "#321C21" : "#222327");
        TikTokPreviewButton.Foreground = Brush(verticalPreview ? "#FF6978" : "#D3D3D5");
    }

    private async Task StartActiveScenePreviewAsync()
    {
        if (!loaded || engine.IsRunning || preparingBroadcast) return;
        if (restartingPreview) { previewRestartPending = true; return; }
        restartingPreview = true;
        try
        {
            do
            {
                previewRestartPending = false;
                var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId)
                    ?? new StudioScene { Name = "Live preview", Format = verticalPreview ? "Vertical · 9:16" : "Wide · 16:9" };
                livePreviewFrame = null;
                PreviewStatusText.Text = verticalPreview ? "OPENING TIKTOK PREVIEW · 4 FPS" : "OPENING LIVE PREVIEW · 6 FPS";
                var phoneInputs = GetPhoneCaptureInputs(scene);
                previewInputSignature = GetPreviewInputSignature(scene, phoneInputs);
                var target = scene.Sources.Any(source => source.Enabled && (source.Kind is "Display" or "App" or "Background"))
                    ? "desktop"
                    : preferences.CaptureTarget.StartsWith("window:", StringComparison.OrdinalIgnoreCase) ? preferences.CaptureTarget[7..] : preferences.CaptureTarget;
                var previewScene = new StudioScene
                {
                    Id = scene.Id,
                    Name = scene.Name,
                    Format = verticalPreview ? "Vertical · 9:16" : "Wide · 16:9",
                    Template = scene.Template,
                    Sources = scene.Sources
                };
                await scenePreview.StartAsync(previewScene, target, string.IsNullOrWhiteSpace(preferences.CameraDevice) ? null : preferences.CameraDevice, phoneInputs);
                if (scenePreview.CaptureTargetFallback)
                    PreviewStatusText.Text = "WINDOW NOT FOUND · SHOWING DESKTOP UNTIL IT OPENS";
            }
            while (previewRestartPending && !engine.IsRunning);
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text = "LIVE PREVIEW UNAVAILABLE";
            if (livePreviewFrame is null) DrawActiveScenePreview();
            EngineErrorText.Text = ex.Message;
        }
        finally
        {
            restartingPreview = false;
            if (previewRestartPending && !engine.IsRunning) _ = StartActiveScenePreviewAsync();
        }
    }

    private Dictionary<string, string> GetPhoneCaptureInputs(StudioScene scene) => scene.Sources
        .Where(source => source.Enabled && source.Kind == "Phone")
        .Select(source => (source.Id, input: phoneCast.GetWindowCaptureInput(source.Content)))
        .Where(entry => entry.input is not null)
        .ToDictionary(entry => entry.Id, entry => entry.input!);

    private string GetPreviewInputSignature(StudioScene scene, IReadOnlyDictionary<string, string> phoneInputs)
    {
        var captureTarget = preferences.CaptureTarget.StartsWith("window:", StringComparison.OrdinalIgnoreCase) ? preferences.CaptureTarget[7..] : preferences.CaptureTarget;
        var window = captureTarget == "desktop" ? "desktop" : CaptureWindowService.FindByTitle(captureTarget).ToInt64().ToString();
        return string.Join("|", scene.Id, captureTarget, window, preferences.CameraDevice,
            string.Join(",", phoneInputs.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}")),
            string.Join(",", scene.Sources.Where(source => source.Enabled && source.Kind == "App").Select(source => $"{source.Id}:{CaptureWindowService.FindBySource(source).ToInt64()}")));
    }

    private void RefreshLivePreviewInputs()
    {
        if (!loaded || engine.IsRunning || preparingBroadcast || restartingPreview) return;
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId)
            ?? new StudioScene { Name = "Live preview", Format = verticalPreview ? "Vertical · 9:16" : "Wide · 16:9" };
        var signature = GetPreviewInputSignature(scene, GetPhoneCaptureInputs(scene));
        if (signature != previewInputSignature || !scenePreview.IsRunning)
            _ = StartActiveScenePreviewAsync();
    }

    private void ApplyPreviewFrame(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            livePreviewFrame = bitmap;
            PreviewStatusText.Text = scenePreview.CaptureTargetFallback
                ? "WINDOW NOT FOUND · SHOWING DESKTOP UNTIL IT OPENS"
                : engine.IsRunning ? "STREAMING · LAST PREVIEW FRAME" : "LIVE PREVIEW · 6 FPS";
            var image = ScenePreviewCanvas.Children.OfType<Image>().FirstOrDefault(item => Equals(item.Tag, "LivePreviewImage"));
            if (image is null) DrawActiveScenePreview();
            else image.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            PreviewStatusText.Text = "DECODING PREVIEW FRAME…";
        }
    }

    private void QueuePreviewFrame(byte[] jpeg)
    {
        Interlocked.Exchange(ref pendingPreviewJpeg, jpeg);
        SchedulePreviewFrameDispatch();
    }

    private void SchedulePreviewFrameDispatch()
    {
        if (Interlocked.Exchange(ref previewFrameDispatchScheduled, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            var latest = Interlocked.Exchange(ref pendingPreviewJpeg, null);
            if (latest is not null) ApplyPreviewFrame(latest);
            Volatile.Write(ref previewFrameDispatchScheduled, 0);
            if (Volatile.Read(ref pendingPreviewJpeg) is not null) SchedulePreviewFrameDispatch();
        });
    }

    private void ApplyPreviewFailure(string message)
    {
        PreviewStatusText.Text = "PREVIEW INPUT NEEDS ATTENTION";
        if (livePreviewFrame is null) DrawActiveScenePreview();
        EngineErrorText.Text = message;
    }

    private void PreviewCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawActiveScenePreview();

    private void DrawActiveScenePreview()
    {
        if (ScenePreviewCanvas is null || ScenePreviewCanvas.ActualWidth < 20 || ScenePreviewCanvas.ActualHeight < 20) return;
        var canvas = ScenePreviewCanvas;
        var width = canvas.ActualWidth; var height = canvas.ActualHeight;
        canvas.Children.Clear();
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        var vertical = verticalPreview;
        var viewport = GetPreviewViewport(vertical, width, height);
        var lowBandwidth = (QualityCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "480p30";
        var outputSize = vertical ? (lowBandwidth ? "480 × 854" : "720 × 1280") : (lowBandwidth ? "854 × 480" : "1280 × 720");
        var sceneCaptureSources = scene?.Sources.Where(source => source.Enabled && (source.Kind is "Display" or "App")).ToList() ?? [];
        var sceneOwnsCanvas = scene?.Sources.Any(source => source.Enabled && (source.Kind is "Display" or "App" or "Background")) == true;
        var capture = sceneCaptureSources.Count > 0 ? string.Join(" + ", sceneCaptureSources.Select(source => source.Name))
            : sceneOwnsCanvas ? "EMPTY SCENE CANVAS" : LiveCaptureTargetCombo?.SelectedItem?.ToString() ?? "Full screen · entire desktop";
        var stageBorder = new Border
        {
            Width = viewport.Width, Height = viewport.Height, Background = Brush("#090A0C"),
            BorderBrush = Brush("#55565C"), BorderThickness = new Thickness(1), IsHitTestVisible = false
        };
        Canvas.SetLeft(stageBorder, viewport.X); Canvas.SetTop(stageBorder, viewport.Y); canvas.Children.Add(stageBorder);
        var formatBadge = new Border
        {
            Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4),
            Background = Brush("#D91A1B1F"), BorderBrush = Brush("#4C4D53"), BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = $"{(vertical ? "VERTICAL · 9:16" : "WIDE · 16:9")} · {outputSize} · SEPARATE SCENE LAYOUT",
                Foreground = Brush("#E5E5E7"), FontSize = 9, FontWeight = FontWeights.SemiBold
            }
        };
        if (livePreviewFrame is not null)
        {
            var liveImage = new Image
            {
                Source = livePreviewFrame, Width = viewport.Width, Height = viewport.Height, Stretch = Stretch.Uniform,
                IsHitTestVisible = false, Tag = "LivePreviewImage"
            };
            Canvas.SetLeft(liveImage, viewport.X); Canvas.SetTop(liveImage, viewport.Y); canvas.Children.Add(liveImage);
        }
        else
        {
            var baseSurface = new Border
            {
                Width = viewport.Width, Height = viewport.Height, Background = Brush("#17191D"), BorderBrush = Brush("#38393F"), BorderThickness = new Thickness(1),
                Child = new Grid
                {
                    Children =
                    {
                        new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new Border { Width = 42, Height = 30, CornerRadius = new CornerRadius(5), Background = Brush("#25272D"), BorderBrush = Brush("#55565C"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, Child = new TextBlock { Text = "▣", FontSize = 16, Foreground = Brush("#BFC0C4"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                                new TextBlock { Text = sceneCaptureSources.Count > 0 ? "CONNECTING TO SCENE CAPTURE" : sceneOwnsCanvas ? "EMPTY CANVAS · ADD DISPLAY, APP, PHONE, CAMERA, OR TEXT" : capture.StartsWith("Window", StringComparison.OrdinalIgnoreCase) ? "GAME / WINDOW CAPTURE" : "FULL SCREEN CAPTURE", Foreground = Brush("#D5D5D7"), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 3), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                                new TextBlock { Text = sceneCaptureSources.Count > 0 ? string.Join(" · ", sceneCaptureSources.Select(source => source.Name)) : sceneOwnsCanvas ? "Choose + Source to arrange this scene." : "Connecting to live capture…", Foreground = Brush("#9A9BA0"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }
                            }
                        },
                        new Border { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8), Padding = new Thickness(7, 4, 7, 4), CornerRadius = new CornerRadius(4), Background = Brush("#222327"), Child = new TextBlock { Text = capture, Foreground = Brush("#B9BABE"), FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(60, viewport.Width * .42) } }
                    }
                }
            };
            Canvas.SetLeft(baseSurface, viewport.X); Canvas.SetTop(baseSurface, viewport.Y);
            canvas.Children.Add(baseSurface);
        }
        Canvas.SetLeft(formatBadge, viewport.X + 9); Canvas.SetTop(formatBadge, viewport.Y + 9); canvas.Children.Add(formatBadge);
        if (scene is null)
        {
            var emptyHint = new TextBlock { Text = "Live desktop preview · add a scene to arrange sources", Foreground = Brush("#F3F2EF"), FontSize = 12, Width = viewport.Width, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            Canvas.SetLeft(emptyHint, viewport.X); Canvas.SetTop(emptyHint, viewport.Y + viewport.Height / 2 - 10); canvas.Children.Add(emptyHint);
            return;
        }
        foreach (var source in scene.Sources.Where(source => source.Enabled))
        {
            var bounds = GetPreviewBounds(scene, source, viewport);
            var selected = source.Id == selectedPreviewSourceId;
            FrameworkElement layer;
            if (livePreviewFrame is not null && scenePreview.CapturesSource(source.Id))
            {
                layer = new Border
                {
                    Width = bounds.Width, Height = bounds.Height,
                    Background = Brush("#01000000"), BorderBrush = Brush(selected ? "#F04455" : "#01FFFFFF"),
                    BorderThickness = new Thickness(selected ? 1.5 : 0), Tag = source, Cursor = Cursors.SizeAll,
                    ToolTip = selected ? $"{source.Name} · drag to move · drag a corner to resize" : source.Name
                };
            }
            else
            {
                layer = BuildPreviewSourceVisual(source, bounds, viewport.Width, vertical);
                layer.Tag = source;
                layer.Cursor = Cursors.SizeAll;
                if (layer is Border layerBorder)
                {
                    layerBorder.BorderBrush = Brush(selected ? "#F04455" : source.Kind == "Text" ? "#625058" : "#55565C");
                    layerBorder.BorderThickness = new Thickness(selected ? 2 : 1);
                }
            }
            Canvas.SetLeft(layer, bounds.X); Canvas.SetTop(layer, bounds.Y);
            canvas.Children.Add(layer);
            if (!selected) continue;
            AddResizeHandle(canvas, source, bounds, true, true);
            AddResizeHandle(canvas, source, bounds, false, true);
            AddResizeHandle(canvas, source, bounds, true, false);
            AddResizeHandle(canvas, source, bounds, false, false);
            var remove = new Button
            {
                Content = "×", Width = 22, Height = 22, Padding = new Thickness(0),
                Background = Brush("#F04455"), Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 15, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
                ToolTip = $"Remove {source.Name} from this scene", Tag = source
            };
            remove.Click += (_, _) => RemovePreviewSource(source);
            Canvas.SetLeft(remove, Math.Clamp(bounds.Right - 11, 0, width - 22)); Canvas.SetTop(remove, Math.Max(0, bounds.Top - 11));
            canvas.Children.Add(remove);
        }
        if (PhoneDeskStatus is not null)
        {
            var android = phoneCast.AndroidStatus.EndsWith("running", StringComparison.OrdinalIgnoreCase);
            var iphone = phoneCast.IPhoneStatus.EndsWith("running", StringComparison.OrdinalIgnoreCase);
            PhoneDeskStatus.Text = android ? "ANDROID RECEIVER ON" : iphone ? "AIRPLAY RECEIVER ON" : "NOT CONNECTED";
            PhoneDeskHint.Text = android ? "Unlock your phone and approve USB debugging." : iphone ? "Keep both devices on Wi-Fi, then select this PC in Screen Mirroring." : "Connect a phone, then add its source to the scene.";
        }
    }

    private static Rect GetPreviewViewport(bool vertical, double canvasWidth, double canvasHeight)
    {
        var aspect = vertical ? 9d / 16d : 16d / 9d;
        var width = Math.Min(canvasWidth, canvasHeight * aspect);
        var height = width / aspect;
        if (height > canvasHeight) { height = canvasHeight; width = height * aspect; }
        return new Rect((canvasWidth - width) / 2, (canvasHeight - height) / 2, width, height);
    }

    private Rect GetPreviewBounds(StudioScene scene, SceneSource source, Rect viewport)
    {
        var canvasWidth = viewport.Width; var canvasHeight = viewport.Height;
        var width = Math.Clamp(canvasWidth * source.Width / 100, 14, canvasWidth);
        var aspect = 16d / 9d;
        if (source.Kind == "Phone") aspect = 9d / 16d;
        else if ((source.Kind == "Image" || source.Kind == "Background" && !source.Content.Equals("transparent", StringComparison.OrdinalIgnoreCase)) && File.Exists(source.Content) && !Path.GetExtension(source.Content).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(source.Content); bitmap.DecodePixelWidth = 48; bitmap.EndInit(); aspect = bitmap.Width / bitmap.Height;
            }
            catch { }
        }
        aspect *= Math.Max(.05, 1 - (source.CropLeft + source.CropRight) / 100) / Math.Max(.05, 1 - (source.CropTop + source.CropBottom) / 100);
        double height;
        if (source.Kind == "Text")
        {
            var maxChars = Math.Max(8, (int)(source.Width * (verticalPreview ? 360 : 640) / 100 / Math.Max(1, source.FontSize * .56)));
            var wrapped = WrapPreviewText(source.Content, maxChars);
            var lines = Math.Max(1, wrapped.Count(character => character == '\n') + 1);
            height = Math.Max(20, canvasHeight * source.FontSize * lines * 1.25 / (verticalPreview ? 1280 : 720));
        }
        else if (source.Kind == "Background") height = canvasHeight;
        else height = source.Height > 0 ? canvasHeight * source.Height / 100 : width / aspect;
        height = Math.Min(Math.Max(18, height), Math.Max(canvasHeight, height));
        var x = Math.Clamp(viewport.X + canvasWidth * source.X / 100, viewport.X, viewport.Right - Math.Min(width, canvasWidth));
        var y = Math.Clamp(viewport.Y + canvasHeight * source.Y / 100, viewport.Y, Math.Max(viewport.Y, viewport.Bottom - Math.Min(height, canvasHeight)));
        return new Rect(x, y, Math.Min(width, canvasWidth), height);
    }

    private FrameworkElement BuildPreviewSourceVisual(SceneSource source, Rect bounds, double canvasWidth, bool vertical)
    {
        var fontScale = canvasWidth / (vertical ? 720d : 1280d);
        FrameworkElement content;
        if (source.Kind == "Text")
        {
            content = new Border
            {
                Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(3), Background = Brush("#7A111214"),
                Child = new TextBlock { Text = source.Content, Foreground = Brush(source.Color), FontSize = Math.Clamp(source.FontSize * fontScale, 8, 36), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap }
            };
        }
        else if (source.Kind == "Background" && source.Content.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            content = new Border { Background = Brush("#090A0C"), BorderBrush = Brush("#38393F"), BorderThickness = new Thickness(1) };
        }
        else if ((source.Kind == "Image" || source.Kind == "Background" && !source.Content.Equals("transparent", StringComparison.OrdinalIgnoreCase)) && File.Exists(source.Content) && !IsVideoFile(source.Content))
        {
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(source.Content); bitmap.DecodePixelWidth = Math.Max(32, (int)bounds.Width); bitmap.EndInit(); bitmap.Freeze();
                content = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Width = bounds.Width, Height = bounds.Height };
            }
            catch { content = MakeSourceBadge("IMAGE · UNAVAILABLE", bounds.Width, bounds.Height, "#77787E"); }
        }
        else
        {
            var label = source.Kind == "Camera"
                ? string.IsNullOrWhiteSpace(preferences.CameraDevice) ? "CAMERA · CHOOSE DEVICE" : livePreviewFrame is null ? "CAMERA · CONNECTING" : "CAMERA · PREVIEW UNAVAILABLE"
                : source.Kind == "Phone" ? $"{source.Content.ToUpperInvariant()} · WAITING FOR CAST"
                : source.Kind == "App" ? $"APP · {source.Content}"
                : source.Kind == "Display" ? "FULL DISPLAY · CONNECTING"
                : source.Kind == "Video" ? "VIDEO · CONNECTING"
                : source.Kind == "Background" ? "BACKGROUND · CONNECTING"
                : source.Kind.ToUpperInvariant();
            content = MakeSourceBadge(label, bounds.Width, bounds.Height, source.Kind is "Camera" or "Phone" ? "#F04455" : "#66676D");
        }
        var layer = new Border { Width = bounds.Width, Height = bounds.Height, Child = content, Background = Brushes.Transparent };
        if (content is FrameworkElement child)
        {
            child.Width = bounds.Width;
            child.Height = bounds.Height;
        }
        return layer;
    }

    private static bool IsVideoFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v";

    private void AddResizeHandle(Canvas canvas, SceneSource source, Rect bounds, bool left, bool top)
    {
        var handle = new Thumb
        {
            Width = 11, Height = 11, Background = Brushes.White, BorderBrush = Brush("#F04455"), BorderThickness = new Thickness(2),
            Cursor = left == top ? Cursors.SizeNWSE : Cursors.SizeNESW, Tag = new PreviewResizeTag(source, left, top),
            ToolTip = "Drag to resize"
        };
        handle.DragStarted += PreviewResizeStarted;
        handle.DragDelta += PreviewResizeDelta;
        handle.DragCompleted += PreviewResizeCompleted;
        Canvas.SetLeft(handle, left ? bounds.Left - 5.5 : bounds.Right - 5.5);
        Canvas.SetTop(handle, top ? bounds.Top - 5.5 : bounds.Bottom - 5.5);
        canvas.Children.Add(handle);
    }

    private void PreviewResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: PreviewResizeTag handle }) return;
        selectedPreviewSourceId = handle.Source.Id;
        resizingPreviewSource = handle.Source;
        resizeStartWidth = handle.Source.Width; resizeStartSourceHeight = handle.Source.Height; resizeStartX = handle.Source.X; resizeStartY = handle.Source.Y;
        resizeAccumulatedX = resizeAccumulatedY = 0;
        var scene = scenes.First(scene => scene.Id == activeSceneId);
        var viewport = GetPreviewViewport(verticalPreview, ScenePreviewCanvas.ActualWidth, ScenePreviewCanvas.ActualHeight);
        resizeStartBounds = GetPreviewBounds(scene, handle.Source, viewport);
        resizeStartHeightPercent = resizeStartBounds.Height / viewport.Height * 100;
    }

    private void PreviewResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: PreviewResizeTag handle } || resizingPreviewSource is null) return;
        var canvas = ScenePreviewCanvas;
        var source = handle.Source;
        resizeAccumulatedX += e.HorizontalChange;
        resizeAccumulatedY += e.VerticalChange;
        var horizontal = handle.Left ? -resizeAccumulatedX : resizeAccumulatedX;
        var vertical = handle.Top ? -resizeAccumulatedY : resizeAccumulatedY;
        var widthScale = Math.Max(.05, (resizeStartBounds.Width + horizontal) / Math.Max(1, resizeStartBounds.Width));
        var heightScale = Math.Max(.05, (resizeStartBounds.Height + vertical) / Math.Max(1, resizeStartBounds.Height));
        source.Width = Math.Clamp(resizeStartWidth * widthScale, 2, 100);
        source.Height = Math.Clamp(resizeStartHeightPercent * heightScale, 1, 100);
        if (handle.Left) source.X = Math.Clamp(resizeStartX + resizeStartWidth - source.Width, 0, 100 - source.Width);
        if (handle.Top) source.Y = Math.Clamp(resizeStartY + resizeStartHeightPercent - source.Height, 0, 100 - source.Height);
        UpdatePreviewTransform(canvas, source);
    }

    private void PreviewResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (resizingPreviewSource is null) return;
        resizingPreviewSource = null; SaveScenes(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync();
    }

    private void UpdatePreviewTransform(Canvas canvas, SceneSource source)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null || canvas.ActualWidth < 1 || canvas.ActualHeight < 1) return;
        var viewport = GetPreviewViewport(verticalPreview, canvas.ActualWidth, canvas.ActualHeight);
        var bounds = GetPreviewBounds(scene, source, viewport);
        var layer = canvas.Children.OfType<FrameworkElement>().FirstOrDefault(item => item.Tag is SceneSource candidate && candidate.Id == source.Id);
        if (layer is not null)
        {
            layer.Width = bounds.Width; layer.Height = bounds.Height;
            Canvas.SetLeft(layer, bounds.Left); Canvas.SetTop(layer, bounds.Top);
            if (layer is Border border && border.Child is FrameworkElement inner)
            {
                inner.Width = bounds.Width; inner.Height = bounds.Height;
                if (inner is Border textBorder && textBorder.Child is TextBlock text) text.Width = Math.Max(20, bounds.Width - 10);
            }
        }
        foreach (var handle in canvas.Children.OfType<Thumb>().Where(item => item.Tag is PreviewResizeTag tag && tag.Source.Id == source.Id))
        {
            var tag = (PreviewResizeTag)handle.Tag;
            Canvas.SetLeft(handle, (tag.Left ? bounds.Left : bounds.Right) - handle.Width / 2);
            Canvas.SetTop(handle, (tag.Top ? bounds.Top : bounds.Bottom) - handle.Height / 2);
        }
        var remove = canvas.Children.OfType<Button>().FirstOrDefault(item => item.Tag is SceneSource candidate && candidate.Id == source.Id);
        if (remove is not null)
        {
            Canvas.SetLeft(remove, Math.Clamp(bounds.Right - remove.Width / 2, 0, canvas.ActualWidth - remove.Width));
            Canvas.SetTop(remove, Math.Max(0, bounds.Top - remove.Height / 2));
        }
    }

    private void RemovePreviewSource(SceneSource source)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null || !scene.Sources.Remove(source)) return;
        selectedPreviewSourceId = null;
        SaveScenes(); RenderScenes(); _ = StartActiveScenePreviewAsync();
    }

    private void PreviewCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            selectedPreviewSourceId = null; DrawActiveScenePreview(); e.Handled = true; return;
        }
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        var source = scene?.Sources.FirstOrDefault(item => item.Id == selectedPreviewSourceId);
        if (source is null) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            RemovePreviewSource(source); e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.E)
        {
            EditActiveSource(source); e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.R)
        {
            source.X = 8; source.Y = 8; source.Width = 42; source.Height = 0;
            SaveScenes(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.D)
        {
            var viewport = GetPreviewViewport(verticalPreview, ScenePreviewCanvas.ActualWidth, ScenePreviewCanvas.ActualHeight);
            var bounds = GetPreviewBounds(scene!, source, viewport);
            source.X = Math.Max(0, (100 - source.Width) / 2);
            source.Y = Math.Max(0, (100 - bounds.Height / viewport.Height * 100) / 2);
            SaveScenes(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            source.X = 0; source.Y = 0; source.Width = 100;
            SaveScenes(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync(); e.Handled = true; return;
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1d : .2d;
        var changed = true;
        switch (e.Key)
        {
            case Key.Left: source.X = Math.Max(0, source.X - step); break;
            case Key.Right: source.X = Math.Min(100 - source.Width, source.X + step); break;
            case Key.Up: source.Y = Math.Max(0, source.Y - step); break;
            case Key.Down: source.Y = Math.Min(99, source.Y + step); break;
            default: changed = false; break;
        }
        if (changed) { SaveScenes(); DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync(); e.Handled = true; }
    }

    private static string WrapPreviewText(string text, int maxCharacters)
    {
        var output = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > maxCharacters) { output.Add(line); line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            output.Add(line);
        }
        return string.Join('\n', output);
    }

    private static string WrapText(string text, int maxCharacters) => WrapPreviewText(text, maxCharacters);
    private void PreviewSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        var source = FindTaggedSource(e.OriginalSource as DependencyObject);
        canvas.Focus();
        if (source is null)
        {
            selectedPreviewSourceId = null; draggedPreviewSource = null; DrawActiveScenePreview();
            return;
        }
        selectedPreviewSourceId = source.Id;
        if (e.ClickCount > 1) { EditActiveSource(source); e.Handled = true; return; }
        draggedPreviewSource = source; dragOrigin = e.GetPosition(canvas); dragSourceX = source.X; dragSourceY = source.Y;
        if (cropPreviewMode)
        {
            var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
            if (scene is not null)
            {
                var viewport = GetPreviewViewport(verticalPreview, canvas.ActualWidth, canvas.ActualHeight);
                var bounds = GetPreviewBounds(scene, source, viewport);
                var point = e.GetPosition(canvas);
                var x = Math.Clamp((point.X - bounds.Left) / Math.Max(1, bounds.Width), 0, 1);
                var y = Math.Clamp((point.Y - bounds.Top) / Math.Max(1, bounds.Height), 0, 1);
                cropEdge = Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y)) == Math.Min(x, 1 - x)
                    ? (x < .5 ? "left" : "right") : (y < .5 ? "top" : "bottom");
                cropStartValue = cropEdge switch { "left" => source.CropLeft, "right" => source.CropRight, "top" => source.CropTop, _ => source.CropBottom };
            }
        }
        canvas.CaptureMouse(); DrawActiveScenePreview(); e.Handled = true;
    }

    private void PreviewSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas || draggedPreviewSource is null || !canvas.IsMouseCaptured) return;
        var point = e.GetPosition(canvas);
        var element = canvas.Children.OfType<FrameworkElement>().FirstOrDefault(item => item.Tag is SceneSource itemSource && itemSource.Id == draggedPreviewSource.Id);
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null) return;
        var viewport = GetPreviewViewport(verticalPreview, canvas.ActualWidth, canvas.ActualHeight);
        if (cropPreviewMode && cropEdge.Length > 0)
        {
            var bounds = GetPreviewBounds(scene, draggedPreviewSource, viewport);
            var deltaX = (point.X - dragOrigin.X) / Math.Max(1, bounds.Width) * 100;
            var deltaY = (point.Y - dragOrigin.Y) / Math.Max(1, bounds.Height) * 100;
            var delta = cropEdge is "left" or "right" ? deltaX : deltaY;
            var value = Math.Clamp(cropStartValue + (cropEdge is "left" or "top" ? delta : -delta), 0, 90);
            if (cropEdge == "left") draggedPreviewSource.CropLeft = Math.Min(value, 94 - draggedPreviewSource.CropRight);
            else if (cropEdge == "right") draggedPreviewSource.CropRight = Math.Min(value, 94 - draggedPreviewSource.CropLeft);
            else if (cropEdge == "top") draggedPreviewSource.CropTop = Math.Min(value, 94 - draggedPreviewSource.CropBottom);
            else draggedPreviewSource.CropBottom = Math.Min(value, 94 - draggedPreviewSource.CropTop);
            UpdatePreviewTransform(canvas, draggedPreviewSource);
            return;
        }
        var maxX = element is null ? 100 - draggedPreviewSource.Width : 100 - element.Width / viewport.Width * 100;
        var maxY = element is null ? 99 : 100 - element.Height / viewport.Height * 100;
        draggedPreviewSource.X = Math.Clamp(dragSourceX + (point.X - dragOrigin.X) / viewport.Width * 100, 0, Math.Max(0, maxX));
        draggedPreviewSource.Y = Math.Clamp(dragSourceY + (point.Y - dragOrigin.Y) / viewport.Height * 100, 0, Math.Max(0, maxY));
        UpdatePreviewTransform(canvas, draggedPreviewSource);
    }

    private void PreviewSourceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || draggedPreviewSource is null) return;
        canvas.ReleaseMouseCapture();
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is not null) SaveScenes();
        draggedPreviewSource = null; cropEdge = string.Empty; DrawActiveScenePreview(); _ = StartActiveScenePreviewAsync();
    }

    private void ToggleCropModeClick(object sender, RoutedEventArgs e)
    {
        cropPreviewMode = !cropPreviewMode;
        CropModeButton.Content = cropPreviewMode ? "Crop: on · drag edge" : "Crop: off";
        CropModeButton.Background = Brush(cropPreviewMode ? "#321C21" : "#222327");
        CropModeButton.Foreground = Brush(cropPreviewMode ? "#FF6978" : "#D3D3D5");
        PreviewStatusText.Text = cropPreviewMode ? "CROP MODE · SELECT A SOURCE AND DRAG ITS EDGE" : "LIVE SCENE PREVIEW";
    }

    private static SceneSource? FindTaggedSource(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement framework && framework.Tag is SceneSource source) return source;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void EditActiveSource(SceneSource source)
    {
        var scene = scenes.FirstOrDefault(item => item.Id == activeSceneId);
        if (scene is null) return;
        var editor = new SceneSourceEditorWindow(scene, source) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = scene.Sources.FindIndex(item => item.Id == source.Id);
        if (index >= 0)
        {
            scene.Sources.RemoveAt(index);
            if (editor.Result.Kind == "Background") scene.Sources.Insert(0, editor.Result);
            else scene.Sources.Insert(Math.Min(index, scene.Sources.Count), editor.Result);
        }
        SaveScenes(); RenderScenes();
        _ = StartActiveScenePreviewAsync();
    }

    private async Task StartAndroidFromDeskAsync()
    {
        try { PhoneCastStatus.Text = await phoneCast.StartAndroidAsync(); LoadWindowChoices(); DrawActiveScenePreview(); await StartActiveScenePreviewAsync(); }
        catch (Exception ex) { PhoneCastStatus.Text = ex.Message; MessageBox.Show(this, ex.Message, "Android phone cast", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private async Task StartIPhoneFromDeskAsync()
    {
        try { PhoneCastStatus.Text = await phoneCast.StartIPhoneAsync(); DrawActiveScenePreview(); await StartActiveScenePreviewAsync(); }
        catch (Exception ex) { PhoneCastStatus.Text = ex.Message; MessageBox.Show(this, ex.Message, "iPhone phone cast", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        previewWatchdog.Stop();
        microphoneTest.Dispose();
        scenePreview.Dispose();
        if (engine.IsRunning) engine.Stop();
        engine.Dispose();
        phoneCast.Dispose();
        localRtmp.Dispose();
    }

    private static TextBlock MakeText(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Canvas BuildScenePreview(StudioScene scene, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height, Background = Brush("#101113"), ClipToBounds = true };
        foreach (var source in scene.Sources.Where(source => source.Enabled))
        {
            var left = width * source.X / 100;
            var top = height * source.Y / 100;
            if (source.Kind == "Text")
            {
                var previewText = new TextBlock
                {
                    Text = source.Content,
                    Foreground = Brush(source.Color),
                    FontSize = Math.Clamp(source.FontSize * .18, 5, 12),
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Width = Math.Max(20, width * source.Width / 100)
                };
                Canvas.SetLeft(previewText, left); Canvas.SetTop(previewText, top); canvas.Children.Add(previewText);
            }
            else
            {
                FrameworkElement box;
                var boxWidth = Math.Max(13, width * source.Width / 100);
                var boxHeight = source.Kind == "Background" ? height : source.Height > 0 ? height * source.Height / 100 : Math.Max(12, height * .27);
                if ((source.Kind == "Image" || source.Kind == "Background" && !source.Content.Equals("transparent", StringComparison.OrdinalIgnoreCase)) && File.Exists(source.Content) && !Path.GetExtension(source.Content).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(source.Content); bitmap.DecodePixelWidth = Math.Max(32, (int)boxWidth); bitmap.EndInit(); bitmap.Freeze();
                        var image = new Image { Source = bitmap, Width = boxWidth, Height = boxHeight, Stretch = Stretch.UniformToFill, ClipToBounds = true };
                        box = image;
                    }
                    catch { box = MakeSourceBadge("IMG", boxWidth, height, "#66676D"); }
                }
                else box = new Border { Width = boxWidth, Height = boxHeight, Background = Brush(source.Kind switch { "Background" when source.Content == "transparent" => "#151619", "Background" => "#38313A", "Display" => "#282B31", "App" => "#343039", "Camera" or "Phone" => "#47232A", "Video" => "#303447", _ => "#33343A" }), BorderBrush = Brush(source.Kind is "Camera" or "Phone" ? "#F04455" : "#77787E"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = new TextBlock { Text = source.Kind switch { "Display" => "FULL DISPLAY", "App" => "APP · " + source.Content, "Camera" => "CAMERA", "Phone" => "PHONE CAST", "Video" => "VIDEO", "Background" => source.Content == "transparent" ? "TRANSPARENT CANVAS" : "BACKGROUND", _ => "IMAGE" }, Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) } };
                Canvas.SetLeft(box, left); Canvas.SetTop(box, top); canvas.Children.Add(box);
            }
        }
        return canvas;
    }

    private static Border MakeSourceBadge(string text, double width, double canvasHeight, string borderColor) => new()
    {
        Width = width, Height = Math.Max(12, canvasHeight * .27),
        Background = Brush("#33343A"), BorderBrush = Brush(borderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) }
    };

    private static Button MakeButton(string text, bool selected)
    {
        var button = new Button
        {
            Content = text,
            Height = 31,
            Padding = new Thickness(10, 0, 10, 0),
            Background = selected ? Brush("#321C21") : Brush("#222327"),
            Foreground = selected ? Brush("#FF6978") : Brush("#D3D3D5"),
            BorderBrush = selected ? Brush("#71333E") : Brush("#38393F"),
            BorderThickness = new Thickness(1),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        return button;
    }

    private static string GetServerLabel(string raw)
    {
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return uri.Host + uri.AbsolutePath;
        return raw;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private sealed record CaptureChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private sealed record SceneChoice(string Name, string Id)
    {
        public override string ToString() => Name;
    }

    private sealed record PreviewResizeTag(SceneSource Source, bool Left, bool Top);
    private sealed record LayerChoice(string SourceId);
}

