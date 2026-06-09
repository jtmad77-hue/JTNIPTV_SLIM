using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace IptvViewer;

public partial class MainWindow : Window
{
    private const string DefaultServiceName = "";
    private const string DefaultServerUrl = "";
    private const string DefaultUsername = "";
    private const string DefaultPassword = "";
    private const int ContinueWatchingLimit = 50;
    private const int HistoryLimit = 5000;
    private const int LiveBufferMinutes = 15;
    private const int LiveBufferSegmentSeconds = 6;
    private const long MinimumResumeMs = 10000;
    private const double WatchedThreshold = 0.95;
    private const string PlayIcon = "\uE768";
    private const string PauseIcon = "\uE769";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly EndpointDefinition[] Endpoints =
    [
        new("get_live_categories", "Live Category", null),
        new("get_live_streams", "Live Channel", "live"),
        new("get_vod_categories", "VOD Category", null),
        new("get_vod_streams", "VOD Movie", "movie"),
        new("get_series_categories", "Series Category", null),
        new("get_series", "Series", null)
    ];

    private static readonly EndpointDefinition[] LiveEndpoints = Endpoints
        .Where(endpoint => endpoint.Action is "get_live_categories" or "get_live_streams")
        .ToArray();

    private static readonly EndpointDefinition[] MovieEndpoints = Endpoints
        .Where(endpoint => endpoint.Action is "get_vod_categories" or "get_vod_streams")
        .ToArray();

    private static readonly EndpointDefinition[] SeriesEndpoints = Endpoints
        .Where(endpoint => endpoint.Action is "get_series_categories" or "get_series")
        .ToArray();

    private readonly ObservableCollection<SavedService> _services = [];
    private readonly ObservableCollection<IptvListItem> _results = [];
    private readonly ObservableCollection<IptvListItem> _favoritesHome = [];
    private readonly ObservableCollection<IptvListItem> _historyHome = [];
    private readonly ObservableCollection<EpgProgram> _epgPrograms = [];
    private readonly ObservableCollection<CategoryFilterItem> _categoryFilters = [];
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _resumeTimer;
    private readonly DispatcherTimer _overlayHideTimer;
    private readonly DispatcherTimer _playbackTimer;

    private bool _isBusy;
    private bool _isFullVideo;
    private bool _isFullscreen;
    private bool _isHomeView;
    private bool _isGuideView;
    private bool _playingTimeshiftBuffer;
    private bool _isOverlayInteractionActive;
    private bool _isInitialized;
    private bool _isUpdatingOverlayScreenSelector;
    private bool _isSeekingPlayback;
    private string _browseTypeFilter = "all";
    private string _categoryFilter = "all";
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;
    private Thickness _previousShellMargin;
    private double _previousLeft;
    private double _previousTop;
    private double _previousWidth;
    private double _previousHeight;
    private IptvListItem? _currentItem;
    private Process? _timeshiftProcess;
    private string? _timeshiftDirectory;

    private static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IptvViewer");

    private static string ServicesPath => Path.Combine(AppDataDirectory, "services.json");

    private static string FavoritesPath => Path.Combine(AppDataDirectory, "favorites.json");

    private static string HistoryPath => Path.Combine(AppDataDirectory, "history.json");

    private static string ContinueWatchingPath => Path.Combine(AppDataDirectory, "continue_watching.json");

    public ICollectionView ResultsView { get; }

    public ICollectionView EpgView { get; }

    public ObservableCollection<IptvListItem> FavoritesHome => _favoritesHome;

    public ObservableCollection<IptvListItem> HistoryHome => _historyHome;

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = 80;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        VideoPlayerView.MediaPlayer = _mediaPlayer;

        _resumeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _resumeTimer.Tick += ResumeTimer_Tick;

        _overlayHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _overlayHideTimer.Tick += OverlayHideTimer_Tick;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        ResultsView = CollectionViewSource.GetDefaultView(_results);
        ResultsView.Filter = FilterResult;
        EpgView = CollectionViewSource.GetDefaultView(_epgPrograms);
        EpgView.Filter = FilterEpgProgram;
        DataContext = this;
        CategoryFilterComboBox.ItemsSource = _categoryFilters;

        Directory.CreateDirectory(AppDataDirectory);
        LoadServices();
        ServicesComboBox.ItemsSource = _services;
        ServicesComboBox.SelectedItem = _services.FirstOrDefault(service => service.Name.Equals(DefaultServiceName, StringComparison.OrdinalIgnoreCase)) ??
                                        _services.FirstOrDefault();
        ApplySelectedServiceToForm();

        HomeTypeFilterComboBox.SelectedIndex = 0;
        OverlayScreenSelector.SelectedIndex = 0;
        ResetCategoryFilters();
        LoadHomeLists();
        ShowHomePanel();
        _isInitialized = true;
    }

    private async void RefreshCacheButton_Click(object sender, RoutedEventArgs e)
    {
        _browseTypeFilter = "all";
        _categoryFilter = "all";
        await LoadEndpointsAsync(Endpoints, replaceExisting: true, saveAsCache: true, cacheMode: "all");
    }

    private async void RefreshLiveButton_Click(object sender, RoutedEventArgs e)
    {
        _browseTypeFilter = "live";
        _categoryFilter = "all";
        await LoadEndpointsAsync(LiveEndpoints, replaceExisting: true, saveAsCache: true, cacheMode: "live");
    }

    private async void RefreshMoviesButton_Click(object sender, RoutedEventArgs e)
    {
        _browseTypeFilter = "vod";
        _categoryFilter = "all";
        await LoadEndpointsAsync(MovieEndpoints, replaceExisting: true, saveAsCache: true, cacheMode: "vod");
    }

    private async void RefreshSeriesButton_Click(object sender, RoutedEventArgs e)
    {
        _browseTypeFilter = "series";
        _categoryFilter = "all";
        await LoadEndpointsAsync(SeriesEndpoints, replaceExisting: true, saveAsCache: true, cacheMode: "series");
    }

    private async void RefreshGuideButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadGuideAsync(forceRefresh: true);
    }

    private void LoadCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        LoadAllLocalCachesIntoResults(credentials!.ServiceName);
    }

    private void ShowFavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadLocalFileIntoResults(FavoritesPath, "Favorites", "No favorites yet. Select a channel, movie, or series and press Add Favorite.");
    }

    private void ShowContinueWatchingButton_Click(object sender, RoutedEventArgs e)
    {
        LoadLocalFileIntoResults(ContinueWatchingPath, "Continue Watching", "No resumable videos yet. Stop a movie or episode partway through to add it here.");
    }

    private void ShowHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        LoadHomeLists();
        ShowHomePanel();
        StatusTextBlock.Text = "Showing Favorites and History.";
    }

    private void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            FileName = $"iptv-viewer-backup-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "IPTV Viewer backup (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IptvExportPackage package = new()
        {
            Services = ReadList<SavedService>(ServicesPath),
            Favorites = ReadList(FavoritesPath),
            ContinueWatching = ReadList(ContinueWatchingPath),
            History = ReadList(HistoryPath),
            Caches = Directory.EnumerateFiles(AppDataDirectory, "cache_*.json")
                              .Select(path => new CachedList(Path.GetFileName(path), ReadList(path)))
                              .ToList()
        };

        SaveJson(dialog.FileName, package);
        StatusTextBlock.Text = $"Exported saved data to {dialog.FileName}.";
    }

    private void ImportDataButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Filter = "IPTV Viewer backup (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            IptvExportPackage package = JsonSerializer.Deserialize<IptvExportPackage>(json) ?? new IptvExportPackage();

            SaveJson(ServicesPath, package.Services);
            SaveJson(FavoritesPath, package.Favorites);
            SaveJson(ContinueWatchingPath, package.ContinueWatching.Take(ContinueWatchingLimit).ToList());
            SaveJson(HistoryPath, package.History.Take(HistoryLimit).ToList());

            foreach (CachedList cache in package.Caches)
            {
                string fileName = Path.GetFileName(cache.FileName);
                if (fileName.StartsWith("cache_", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    SaveJson(Path.Combine(AppDataDirectory, fileName), cache.Items);
                }
            }

            LoadServices();
            ServicesComboBox.ItemsSource = _services;
            ServicesComboBox.SelectedItem = _services.FirstOrDefault();
            ApplySelectedServiceToForm();
            LoadHomeLists();
            ShowHomePanel();
            StatusTextBlock.Text = $"Imported saved data from {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Import failed: {ex.Message}";
        }
    }

    private void SaveServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        SavedService saved = new(credentials!.ServiceName, credentials.ServerUrl, credentials.Username, credentials.Password);
        SavedService? existing = _services.FirstOrDefault(service => service.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _services.Add(saved);
            ServicesComboBox.SelectedItem = saved;
        }
        else
        {
            int index = _services.IndexOf(existing);
            _services[index] = saved;
            ServicesComboBox.SelectedItem = saved;
        }

        SaveJson(ServicesPath, _services.ToList());
        StatusTextBlock.Text = $"Saved service '{saved.Name}'.";
    }

    private async void EndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        EndpointDefinition endpoint = Endpoints.Single(item => item.Action == action);
        _browseTypeFilter = endpoint.Action switch
        {
            "get_live_categories" or "get_live_streams" => "live",
            "get_vod_categories" or "get_vod_streams" => "vod",
            "get_series_categories" or "get_series" => "series",
            _ => "all"
        };
        _categoryFilter = "all";
        await LoadEndpointsAsync([endpoint], replaceExisting: true, saveAsCache: false, cacheMode: _browseTypeFilter);
    }

    private async void BrowseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string mode)
        {
            return;
        }

        if (mode == "home")
        {
            _browseTypeFilter = "all";
            _categoryFilter = "all";
            LoadHomeLists();
            ShowHomePanel();
            StatusTextBlock.Text = "Showing Favorites and History.";
            return;
        }

        if (mode == "guide")
        {
            await LoadGuideAsync(forceRefresh: false);
            return;
        }

        _browseTypeFilter = mode;
        _categoryFilter = "all";
        await LoadBrowseModeFromCacheAsync(mode);
    }

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized && ResultsView is null)
        {
            return;
        }

        _categoryFilter = CategoryFilterComboBox.SelectedValue as string ?? "all";
        ResultsView?.Refresh();
        EpgView?.Refresh();
        UpdateCount();
        UpdateSelectedPlayableState();
    }

    private void ServicesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedServiceToForm();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResultsView.Refresh();
        EpgView.Refresh();
        UpdateCount();
        UpdateSelectedPlayableState();
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedArtwork(ResultsGrid.SelectedItem as IptvListItem);
        UpdateSelectedPlayableState();
    }

    private void HomeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid selectedGrid)
        {
            DataGrid otherGrid = selectedGrid == FavoritesGrid ? HistoryGrid : FavoritesGrid;
            otherGrid.SelectedItem = null;
            ResultsGrid.SelectedItem = null;
        }

        UpdateSelectedArtwork(GetSelectedItem());
        UpdateSelectedPlayableState();
    }

    private void HomeTypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsView is null)
        {
            return;
        }

        LoadHomeLists();
    }

    private async void PlayableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetSelectedItem() is IptvListItem item)
        {
            if (IsCategoryRow(item))
            {
                await BrowseCategoryAsync(item);
                return;
            }

            await PlayItemAsync(item);
        }
    }

    private async void GuideGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GuideGrid.SelectedItem is not EpgProgram program)
        {
            return;
        }

        IptvListItem? channel = FindLiveChannelForGuide(program.ChannelId);
        if (channel is null)
        {
            StatusTextBlock.Text = $"No cached live channel matched guide id '{program.ChannelId}'. Refresh lists from server, then try again.";
            return;
        }

        ResultsGrid.SelectedItem = channel;
        await PlayItemAsync(channel);
    }

    private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItem() is not IptvListItem item)
        {
            StatusTextBlock.Text = "Select a channel, movie, or series before adding a favorite.";
            return;
        }

        if (!CanFavorite(item))
        {
            StatusTextBlock.Text = "Favorites are for channels, VOD movies, and series. Category rows are just for browsing.";
            return;
        }

        List<IptvListItem> favorites = ReadList(FavoritesPath);
        bool alreadySaved = favorites.Any(favorite => SameLibraryItem(favorite, item));
        if (!alreadySaved)
        {
            favorites.Insert(0, item with
            {
                LocalList = "Favorite",
                SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            SaveJson(FavoritesPath, favorites);
        }

        StatusTextBlock.Text = alreadySaved
            ? $"{item.Name} is already in favorites."
            : $"Added {item.Name} to favorites.";
        LoadHomeLists();
        UpdateSelectedPlayableState();
    }

    private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItem() is not IptvListItem item)
        {
            StatusTextBlock.Text = "Select a favorite to remove.";
            return;
        }

        List<IptvListItem> favorites = ReadList(FavoritesPath);
        int removed = favorites.RemoveAll(favorite => SameLibraryItem(favorite, item));
        SaveJson(FavoritesPath, favorites);
        LoadHomeLists();
        ResultsView.Refresh();
        StatusTextBlock.Text = removed > 0 ? $"Removed {item.Name} from favorites." : $"{item.Name} was not in favorites.";
        UpdateSelectedPlayableState();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SaveJson(HistoryPath, new List<IptvListItem>());
        LoadHomeLists();
        StatusTextBlock.Text = "History cleared. Continue Watching and Favorites were not changed.";
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItem() is not IptvListItem item)
        {
            StatusTextBlock.Text = "Select a live channel, VOD movie, series, or episode row before pressing Play.";
            return;
        }

        await PlayItemAsync(item);
    }

    private async Task PlayItemAsync(IptvListItem item)
    {
        if (item.EndpointAction == "get_series")
        {
            await LoadSeriesEpisodesAndPlayFirstAsync(item);
            return;
        }

        PlayStream(item);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaPlayer.CanPause)
        {
            StatusTextBlock.Text = "This stream does not support pause.";
            return;
        }

        _mediaPlayer.Pause();
        PauseButton.Content = _mediaPlayer.IsPlaying ? PauseIcon : PlayIcon;
        OverlayPauseButton.Content = PauseButton.Content;
        StatusTextBlock.Text = _mediaPlayer.IsPlaying ? "Playback resumed." : "Playback paused.";
    }

    private void PlaybackPositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSeekingPlayback = true;
    }

    private void PlaybackPositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mediaPlayer.IsSeekable)
        {
            _mediaPlayer.Time = (long)PlaybackPositionSlider.Value;
        }

        _isSeekingPlayback = false;
    }

    private void PlaybackPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeekingPlayback)
        {
            PlaybackPositionTextBlock.Text = FormatDuration((long)e.NewValue);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Volume = (int)e.NewValue;
    }

    private void PlayerSurface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            ShowVideoOverlay();
            e.Handled = true;
        }
    }

    private void PlayerSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (VideoOverlayPopup.IsOpen)
        {
            ScheduleVideoOverlayHide();
        }
    }

    private void PlayerSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoOverlay();
        e.Handled = true;
    }

    private void OverlayControlsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowVideoOverlay();
    }

    private void VideoOverlayPopup_Closed(object sender, EventArgs e)
    {
        _overlayHideTimer.Stop();
    }

    private void VideoOverlayControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isOverlayInteractionActive = true;
        _overlayHideTimer.Stop();
    }

    private void VideoOverlayControl_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isOverlayInteractionActive = false;
        ScheduleVideoOverlayHide();
    }

    private void VideoOverlayControl_Active(object sender, EventArgs e)
    {
        _isOverlayInteractionActive = true;
        _overlayHideTimer.Stop();
    }

    private void VideoOverlayControl_Done(object sender, EventArgs e)
    {
        _isOverlayInteractionActive = false;
        ScheduleVideoOverlayHide();
    }

    private void OverlayScreenSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _isUpdatingOverlayScreenSelector)
        {
            return;
        }

        if (OverlayScreenSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string mode)
        {
            return;
        }

        switch (mode)
        {
            case "25":
                ReturnToDefaultVideoLayout();
                break;
            case "50":
                ExitFullscreen();
                SetSplitVideoLayout(playerWeight: 1, resultsWeight: 1);
                SetVideoOverlayVisible(true);
                break;
            case "full":
                ExitFullscreen();
                SetFullVideoLayout(true);
                break;
            case "fullscreen":
                if (!_isFullscreen)
                {
                    EnterFullscreen();
                }

                break;
        }

        ScheduleVideoOverlayHide();
    }

    private void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaPlayer.IsSeekable)
        {
            StatusTextBlock.Text = _currentItem?.EndpointAction == "get_live_streams"
                ? "Live rewind needs ffmpeg available so the 15-minute buffer can run."
                : "This stream does not support rewind.";
            return;
        }

        _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 30000);
        StatusTextBlock.Text = "Rewound 30 seconds.";
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void FullVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        SetFullVideoLayout(!_isFullVideo);
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;
        _previousShellMargin = ShellGrid.Margin;
        _previousLeft = Left;
        _previousTop = Top;
        _previousWidth = Width;
        _previousHeight = Height;

        SetFullVideoLayout(true);
        ShellGrid.Margin = new Thickness(0);
        ControlsRow.Height = new GridLength(0);
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        Rect monitorBounds = GetCurrentMonitorBounds();
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;

        _isFullscreen = true;
        SetOverlayScreenMode("fullscreen");
        SetVideoOverlayVisible(true);
        FullScreenButton.Content = "↙";
        FullScreenButton.ToolTip = "Exit fullscreen";
    }

    private void FullscreenBack25Button_Click(object sender, RoutedEventArgs e)
    {
        ReturnToDefaultVideoLayout();
    }

    private void Video25Button_Click(object sender, RoutedEventArgs e)
    {
        ExitFullscreen();
        SetSplitVideoLayout(playerWeight: 1, resultsWeight: 3);
        SetOverlayScreenMode("25");
        SetVideoOverlayVisible(false);
    }

    private void Video50Button_Click(object sender, RoutedEventArgs e)
    {
        ExitFullscreen();
        SetSplitVideoLayout(playerWeight: 1, resultsWeight: 1);
        SetOverlayScreenMode("50");
        SetVideoOverlayVisible(true);
    }

    private void SetFullVideoLayout(bool isFullVideo)
    {
        _isFullVideo = isFullVideo;

        LeftPanel.Visibility = isFullVideo ? Visibility.Collapsed : Visibility.Visible;
        HeaderPanel.Visibility = isFullVideo ? Visibility.Collapsed : Visibility.Visible;
        SearchTextBox.Visibility = isFullVideo || _isHomeView ? Visibility.Collapsed : Visibility.Visible;
        BrowseFilterPanel.Visibility = isFullVideo || _isHomeView ? Visibility.Collapsed : Visibility.Visible;
        HomeFilterPanel.Visibility = isFullVideo || !_isHomeView ? Visibility.Collapsed : Visibility.Visible;
        HomePanel.Visibility = isFullVideo || !_isHomeView ? Visibility.Collapsed : Visibility.Visible;
        ResultsGrid.Visibility = isFullVideo || _isHomeView || _isGuideView ? Visibility.Collapsed : Visibility.Visible;
        GuideGrid.Visibility = isFullVideo || !_isGuideView ? Visibility.Collapsed : Visibility.Visible;

        LeftColumn.Width = isFullVideo ? new GridLength(0) : new GridLength(330);
        SpacerColumn.Width = isFullVideo ? new GridLength(0) : new GridLength(24);
        HeaderRow.Height = isFullVideo ? new GridLength(0) : GridLength.Auto;
        SearchRow.Height = isFullVideo ? new GridLength(0) : GridLength.Auto;
        ResultsRow.Height = isFullVideo ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PlayerRow.Height = isFullVideo ? new GridLength(1, GridUnitType.Star) : new GridLength(360);

        FullVideoButton.Content = isFullVideo ? "▥" : "▣";
        FullVideoButton.ToolTip = isFullVideo ? "Exit full video view" : "Full video view inside the app";
        SetOverlayScreenMode(isFullVideo ? "full" : "25");
        SetVideoOverlayVisible(isFullVideo);
    }

    private void SetSplitVideoLayout(double playerWeight, double resultsWeight)
    {
        SetFullVideoLayout(false);
        PlayerRow.Height = new GridLength(playerWeight, GridUnitType.Star);
        ResultsRow.Height = new GridLength(resultsWeight, GridUnitType.Star);
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        Topmost = _previousTopmost;
        Left = _previousLeft;
        Top = _previousTop;
        Width = _previousWidth;
        Height = _previousHeight;
        WindowState = _previousWindowState;
        ShellGrid.Margin = _previousShellMargin;
        ControlsRow.Height = GridLength.Auto;
        _isFullscreen = false;
        SetVideoOverlayVisible(_isFullVideo);
        SetOverlayScreenMode(_isFullVideo ? "full" : "25");
        FullScreenButton.Content = "⛶";
        FullScreenButton.ToolTip = "Fullscreen video";
    }

    private void SetVideoOverlayVisible(bool isVisible)
    {
        if (isVisible)
        {
            ShowVideoOverlay();
            return;
        }

        VideoOverlayPopup.IsOpen = false;
        _overlayHideTimer.Stop();
    }

    private void ShowVideoOverlay()
    {
        OverlayPauseButton.Content = PauseButton.Content;
        OverlayVolumeSlider.Value = _mediaPlayer.Volume;
        VideoOverlayPopup.HorizontalOffset = Math.Max(12, (PlayerSurface.ActualWidth - 470) / 2);
        VideoOverlayPopup.VerticalOffset = Math.Max(12, PlayerSurface.ActualHeight - 76);
        VideoOverlayPopup.IsOpen = true;
        ScheduleVideoOverlayHide();
    }

    private void ScheduleVideoOverlayHide()
    {
        if (_isOverlayInteractionActive)
        {
            return;
        }

        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    private void OverlayHideTimer_Tick(object? sender, EventArgs e)
    {
        if (_isOverlayInteractionActive)
        {
            return;
        }

        _overlayHideTimer.Stop();
        VideoOverlayPopup.IsOpen = false;
    }

    private void SetOverlayScreenMode(string mode)
    {
        _isUpdatingOverlayScreenSelector = true;
        try
        {
            foreach (object item in OverlayScreenSelector.Items)
            {
                if (item is ComboBoxItem comboBoxItem && comboBoxItem.Tag is string tag && tag == mode)
                {
                    OverlayScreenSelector.SelectedItem = comboBoxItem;
                    return;
                }
            }
        }
        finally
        {
            _isUpdatingOverlayScreenSelector = false;
        }
    }

    private Rect GetCurrentMonitorBounds()
    {
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        return new Rect(
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);
    }

    private void ReturnToDefaultVideoLayout()
    {
        ExitFullscreen();
        SetSplitVideoLayout(playerWeight: 1, resultsWeight: 3);
    }

    private async Task LoadEndpointsAsync(IEnumerable<EndpointDefinition> endpoints, bool replaceExisting, bool saveAsCache, string cacheMode)
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        XtreamCredentials activeCredentials = credentials!;
        SetBusy(true);
        StatusTextBlock.Text = "Loading...";

        if (replaceExisting)
        {
            _results.Clear();
        }

        try
        {
            ShowResultsPanel();
            List<IptvListItem> fetched = [];
            foreach (EndpointDefinition endpoint in endpoints)
            {
                StatusTextBlock.Text = $"Loading {endpoint.Action}...";
                IReadOnlyList<IptvListItem> items = await FetchEndpointAsync(activeCredentials, endpoint);
                fetched.AddRange(items);

                foreach (IptvListItem item in items)
                {
                    _results.Add(item);
                }
            }

            if (saveAsCache)
            {
                SaveJson(GetCachePath(activeCredentials.ServiceName, cacheMode), fetched);
            }

            PopulateCategoryFilters();
            LoadCachedGuideIntoMemory();
            ApplyEpgToResults();
            ResultsView.Refresh();
            UpdateCount();
            StatusTextBlock.Text = fetched.Count == 0
                ? "No items returned. Check the URL, credentials, or provider endpoint support."
                : saveAsCache
                    ? $"Loaded and cached {fetched.Count:N0} {GetBrowseModeLabel(cacheMode)} item(s)."
                    : $"Loaded {fetched.Count:N0} item(s).";
        }
        catch (HttpRequestException ex)
        {
            StatusTextBlock.Text = $"Network error: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Request timed out.";
        }
        catch (JsonException ex)
        {
            StatusTextBlock.Text = $"The provider returned invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<IReadOnlyList<IptvListItem>> FetchEndpointAsync(XtreamCredentials credentials, EndpointDefinition endpoint)
    {
        Uri requestUri = BuildPlayerApiUri(credentials, endpoint.Action);
        using HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();

        using Stream jsonStream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(jsonStream);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<IptvListItem> items = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string itemId = GetFirstString(element, "stream_id", "series_id", "category_id", "num");
            string extension = GetFirstString(element, "container_extension");

            items.Add(new IptvListItem
            {
                ServiceName = credentials.ServiceName,
                EndpointAction = endpoint.Action,
                EndpointLabel = endpoint.Label,
                Name = GetFirstString(element, "name", "category_name", "title"),
                CategoryId = GetFirstString(element, "category_id"),
                EpgChannelId = GetFirstString(element, "epg_channel_id", "channel_id"),
                ItemId = itemId,
                ContainerExtension = extension,
                ArtworkUrl = GetFirstString(element, "stream_icon", "cover", "cover_big", "movie_image"),
                PlaybackUrl = BuildPlaybackUrl(credentials, endpoint.StreamKind, itemId, extension)
            });
        }

        return items;
    }

    private async Task LoadSeriesEpisodesAndPlayFirstAsync(IptvListItem series)
    {
        if (!TryGetCredentialsForItem(series, out XtreamCredentials? credentials))
        {
            StatusTextBlock.Text = "Save this service account before loading series episodes.";
            return;
        }

        SetBusy(true);
        StatusTextBlock.Text = $"Loading episodes for {series.Name}...";

        try
        {
            IReadOnlyList<IptvListItem> episodes = await FetchSeriesEpisodesAsync(credentials!, series);
            if (episodes.Count == 0)
            {
                StatusTextBlock.Text = $"No playable episodes were returned for {series.Name}.";
                return;
            }

            foreach (IptvListItem episode in episodes)
            {
                if (!_results.Any(existing => SameLibraryItem(existing, episode)))
                {
                    _results.Add(episode);
                }
            }

            ResultsView.Refresh();
            UpdateCount();
            ResultsGrid.SelectedItem = episodes[0];
            ResultsGrid.ScrollIntoView(episodes[0]);
            PlayStream(episodes[0]);
        }
        catch (HttpRequestException ex)
        {
            StatusTextBlock.Text = $"Network error loading series episodes: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Series episode request timed out.";
        }
        catch (JsonException ex)
        {
            StatusTextBlock.Text = $"The provider returned invalid series JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error loading series episodes: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<IReadOnlyList<IptvListItem>> FetchSeriesEpisodesAsync(XtreamCredentials credentials, IptvListItem series)
    {
        Uri requestUri = BuildPlayerApiUri(credentials, "get_series_info", ("series_id", series.ItemId));
        using HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();

        using Stream jsonStream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(jsonStream);

        if (!document.RootElement.TryGetProperty("episodes", out JsonElement episodesElement))
        {
            return [];
        }

        List<IptvListItem> episodes = [];
        foreach (JsonElement episodeElement in EnumerateEpisodeObjects(episodesElement))
        {
            string episodeId = GetFirstString(episodeElement, "id", "stream_id");
            if (string.IsNullOrWhiteSpace(episodeId))
            {
                continue;
            }

            string extension = GetFirstString(episodeElement, "container_extension");
            string episodeNumber = GetFirstString(episodeElement, "episode_num");
            string episodeTitle = GetFirstString(episodeElement, "title", "name");
            string displayName = string.IsNullOrWhiteSpace(episodeNumber)
                ? $"{series.Name} - {episodeTitle}"
                : $"{series.Name} - Episode {episodeNumber}: {episodeTitle}";

            episodes.Add(new IptvListItem
            {
                ServiceName = credentials.ServiceName,
                EndpointAction = "get_series_episodes",
                EndpointLabel = "Series Episode",
                Name = displayName.TrimEnd(' ', ':'),
                CategoryId = series.CategoryId,
                EpgChannelId = series.EpgChannelId,
                ItemId = episodeId,
                ContainerExtension = extension,
                ArtworkUrl = GetFirstString(episodeElement, "movie_image", "cover", "cover_big", "stream_icon"),
                PlaybackUrl = BuildPlaybackUrl(credentials, "series", episodeId, extension)
            });
        }

        return episodes;
    }

    private async Task<List<EpgProgram>> FetchEpgAsync(XtreamCredentials credentials)
    {
        Uri requestUri = BuildXmltvUri(credentials);
        using HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();

        await using Stream xmlStream = await response.Content.ReadAsStreamAsync();
        XDocument document = await XDocument.LoadAsync(xmlStream, LoadOptions.None, CancellationToken.None);
        DateTimeOffset cutoff = DateTimeOffset.Now.AddHours(-4);

        return document.Descendants("programme")
                       .Select(element => new EpgProgram
                       {
                           ChannelId = (string?)element.Attribute("channel") ?? string.Empty,
                           StartUtc = ParseXmltvTime((string?)element.Attribute("start")),
                           StopUtc = ParseXmltvTime((string?)element.Attribute("stop")),
                           Title = element.Elements("title").FirstOrDefault()?.Value.Trim() ?? string.Empty,
                           Description = element.Elements("desc").FirstOrDefault()?.Value.Trim() ?? string.Empty
                       })
                       .Where(program => program.StopUtc == DateTimeOffset.MinValue || program.StopUtc >= cutoff)
                       .OrderBy(program => program.StartUtc)
                       .Take(10000)
                       .ToList();
    }

    private void LoadServices()
    {
        List<SavedService> savedServices = ReadList<SavedService>(ServicesPath);

        if (!string.IsNullOrWhiteSpace(DefaultServiceName) &&
            !savedServices.Any(service => service.Name.Equals(DefaultServiceName, StringComparison.OrdinalIgnoreCase)))
        {
            savedServices.Insert(0, new SavedService(DefaultServiceName, DefaultServerUrl, DefaultUsername, DefaultPassword));
        }

        _services.Clear();
        foreach (SavedService service in savedServices.OrderBy(service => service.Name.Equals(DefaultServiceName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                                      .ThenBy(service => service.Name))
        {
            _services.Add(service);
        }

        SaveJson(ServicesPath, _services.ToList());
    }

    private void ApplySelectedServiceToForm()
    {
        if (ServicesComboBox.SelectedItem is not SavedService service)
        {
            PasswordInput.Password = DefaultPassword;
            return;
        }

        AccountNameTextBox.Text = service.Name;
        ServerUrlTextBox.Text = service.ServerUrl;
        UsernameTextBox.Text = service.Username;
        PasswordInput.Password = service.Password;
    }

    private void LoadLocalFileIntoResults(string path, string label, string emptyMessage)
    {
        ShowResultsPanel();
        _results.Clear();
        foreach (IptvListItem item in ReadList(path))
        {
            _results.Add(item);
        }

        ResultsView.Refresh();
        PopulateCategoryFilters();
        LoadCachedGuideIntoMemory();
        ApplyEpgToResults();
        ResultsView.Refresh();
        UpdateCount();
        UpdateSelectedPlayableState();
        StatusTextBlock.Text = _results.Count == 0 ? emptyMessage : $"{label}: loaded {_results.Count:N0} item(s).";
    }

    private void LoadAllLocalCachesIntoResults(string serviceName)
    {
        ShowResultsPanel();
        _browseTypeFilter = "all";
        _categoryFilter = "all";
        _results.Clear();

        string[] cachePaths =
        [
            GetCachePath(serviceName, "live"),
            GetCachePath(serviceName, "vod"),
            GetCachePath(serviceName, "series")
        ];

        bool loadedSplitCache = false;
        foreach (string cachePath in cachePaths.Where(File.Exists))
        {
            loadedSplitCache = true;
            foreach (IptvListItem item in ReadList(cachePath))
            {
                if (!_results.Any(existing => SameLibraryItem(existing, item)))
                {
                    _results.Add(item);
                }
            }
        }

        if (!loadedSplitCache)
        {
            foreach (IptvListItem item in ReadList(GetCachePath(serviceName)))
            {
                _results.Add(item);
            }
        }

        ResultsView.Refresh();
        PopulateCategoryFilters();
        LoadCachedGuideIntoMemory();
        ApplyEpgToResults();
        ResultsView.Refresh();
        UpdateCount();
        UpdateSelectedPlayableState();
        StatusTextBlock.Text = _results.Count == 0
            ? "No local cache yet. Refresh Live, Movies, or Series from server once to create smaller local cache files."
            : $"Local cache for {serviceName}: loaded {_results.Count:N0} item(s).";
    }

    private async Task LoadBrowseModeFromCacheAsync(string mode)
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        string cachePath = GetCachePath(credentials!.ServiceName, mode);
        if (!File.Exists(cachePath))
        {
            cachePath = GetCachePath(credentials.ServiceName);
        }

        if (!File.Exists(cachePath))
        {
            StatusTextBlock.Text = $"No {GetBrowseModeLabel(mode)} cache yet. Use the matching refresh button first.";
            return;
        }

        LoadLocalFileIntoResults(cachePath, $"{GetBrowseModeLabel(mode)} cache for {credentials.ServiceName}", $"No {GetBrowseModeLabel(mode)} cache yet.");
        StatusTextBlock.Text = $"Showing {GetBrowseModeLabel(mode)} from local cache.";
        await Task.CompletedTask;
    }

    private async Task BrowseCategoryAsync(IptvListItem category)
    {
        string mode = category.EndpointAction switch
        {
            "get_live_categories" => "live",
            "get_vod_categories" => "vod",
            "get_series_categories" => "series",
            _ => "all"
        };

        if (mode == "all")
        {
            return;
        }

        _browseTypeFilter = mode;
        _categoryFilter = category.ItemId;
        await LoadBrowseModeFromCacheAsync(mode);
        CategoryFilterComboBox.SelectedValue = category.ItemId;
        ResultsView.Refresh();
        UpdateCount();
        StatusTextBlock.Text = $"Showing {GetBrowseModeLabel(mode)} in {category.Name}.";
    }

    private async Task LoadGuideAsync(bool forceRefresh)
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        XtreamCredentials activeCredentials = credentials!;
        string epgPath = GetEpgPath(activeCredentials.ServiceName);
        SetBusy(true);

        try
        {
            List<EpgProgram> programs = !forceRefresh && File.Exists(epgPath)
                ? ReadList<EpgProgram>(epgPath)
                : await FetchEpgAsync(activeCredentials);

            if (forceRefresh || !File.Exists(epgPath))
            {
                SaveJson(epgPath, programs);
            }

            _epgPrograms.Clear();
            foreach (EpgProgram program in programs.OrderBy(program => program.StartUtc).Take(10000))
            {
                _epgPrograms.Add(program);
            }

            ApplyEpgToResults();
            ShowGuidePanel();
            EpgView.Refresh();
            UpdateCount();
            StatusTextBlock.Text = _epgPrograms.Count == 0
                ? "No guide programs were found."
                : $"Guide loaded with {_epgPrograms.Count:N0} program(s).";
        }
        catch (HttpRequestException ex)
        {
            StatusTextBlock.Text = $"Network error loading guide: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Guide request timed out.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Guide load failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadHomeLists()
    {
        string filter = GetHomeTypeFilter();
        _favoritesHome.Clear();
        foreach (IptvListItem item in ReadList(FavoritesPath).Where(item => MatchesTypeFilter(item, filter)).Take(250))
        {
            _favoritesHome.Add(item);
        }

        _historyHome.Clear();
        foreach (IptvListItem item in ReadList(HistoryPath).Where(item => MatchesTypeFilter(item, filter)).Take(250))
        {
            _historyHome.Add(item);
        }

        UpdateCount();
        UpdateSelectedPlayableState();
    }

    private void LoadCachedGuideIntoMemory()
    {
        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return;
        }

        string epgPath = GetEpgPath(credentials!.ServiceName);
        if (!File.Exists(epgPath))
        {
            return;
        }

        List<EpgProgram> programs = ReadList<EpgProgram>(epgPath);
        if (programs.Count == 0)
        {
            return;
        }

        _epgPrograms.Clear();
        foreach (EpgProgram program in programs.OrderBy(program => program.StartUtc).Take(10000))
        {
            _epgPrograms.Add(program);
        }
    }

    private void ShowHomePanel()
    {
        _isHomeView = true;
        _isGuideView = false;
        HomePanel.Visibility = Visibility.Visible;
        ResultsGrid.Visibility = Visibility.Collapsed;
        GuideGrid.Visibility = Visibility.Collapsed;
        SearchTextBox.Visibility = Visibility.Collapsed;
        BrowseFilterPanel.Visibility = Visibility.Collapsed;
        HomeFilterPanel.Visibility = Visibility.Visible;
        _results.Clear();
        ResultsView.Refresh();
        UpdateCount();
    }

    private void ShowResultsPanel()
    {
        _isHomeView = false;
        _isGuideView = false;
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsGrid.Visibility = Visibility.Visible;
        GuideGrid.Visibility = Visibility.Collapsed;
        SearchTextBox.Visibility = Visibility.Visible;
        BrowseFilterPanel.Visibility = Visibility.Visible;
        HomeFilterPanel.Visibility = Visibility.Collapsed;
        FavoritesGrid.SelectedItem = null;
        HistoryGrid.SelectedItem = null;
    }

    private void ShowGuidePanel()
    {
        _isHomeView = false;
        _isGuideView = true;
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsGrid.Visibility = Visibility.Collapsed;
        GuideGrid.Visibility = Visibility.Visible;
        SearchTextBox.Visibility = Visibility.Visible;
        BrowseFilterPanel.Visibility = Visibility.Visible;
        HomeFilterPanel.Visibility = Visibility.Collapsed;
        FavoritesGrid.SelectedItem = null;
        HistoryGrid.SelectedItem = null;
        ResultsGrid.SelectedItem = null;
        ResetCategoryFilters();
    }

    private void AddToHistory(IptvListItem item)
    {
        List<IptvListItem> history = ReadList(HistoryPath);
        history.RemoveAll(historyItem => SameLibraryItem(historyItem, item));
        history.Insert(0, item with
        {
            LocalList = "History",
            SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        SaveJson(HistoryPath, history.Take(HistoryLimit).ToList());
        LoadHomeLists();
    }

    private bool TryCreateCredentials(out XtreamCredentials? credentials)
    {
        credentials = null;

        string serviceName = AccountNameTextBox.Text.Trim();
        string serverUrl = ServerUrlTextBox.Text.Trim().TrimEnd('/');
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordInput.Password.Trim();

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            StatusTextBlock.Text = "Enter an account name.";
            return false;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            StatusTextBlock.Text = "Enter a valid server URL, for example http://example.com:8080.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            StatusTextBlock.Text = "Enter both username and password.";
            return false;
        }

        credentials = new XtreamCredentials(serviceName, serverUrl, username, password);
        return true;
    }

    private bool TryGetCredentialsForItem(IptvListItem item, out XtreamCredentials? credentials)
    {
        SavedService? service = _services.FirstOrDefault(saved => saved.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (service is not null)
        {
            credentials = new XtreamCredentials(service.Name, service.ServerUrl, service.Username, service.Password);
            return true;
        }

        return TryCreateCredentials(out credentials);
    }

    private bool FilterResult(object item)
    {
        if (item is not IptvListItem result)
        {
            return false;
        }

        if (!MatchesBrowseFilter(result, _browseTypeFilter))
        {
            return false;
        }

        if (_categoryFilter != "all" && !result.CategoryId.Equals(_categoryFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string search = SearchTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               Contains(result.ServiceName, search) ||
               Contains(result.LocalList, search) ||
               Contains(result.EndpointLabel, search) ||
               Contains(result.EndpointAction, search) ||
               Contains(result.Name, search) ||
               Contains(result.CategoryId, search) ||
               Contains(result.EpgChannelId, search) ||
               Contains(result.CurrentProgram, search) ||
               Contains(result.NextProgram, search) ||
               Contains(result.ItemId, search) ||
               Contains(result.ContainerExtension, search) ||
               Contains(result.SavedAt, search) ||
               Contains(result.ArtworkUrl, search) ||
               Contains(result.PlaybackUrl, search);
    }

    private bool FilterEpgProgram(object item)
    {
        if (item is not EpgProgram program)
        {
            return false;
        }

        string search = SearchTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               Contains(program.ChannelId, search) ||
               Contains(program.Title, search) ||
               Contains(program.Description, search) ||
               Contains(program.StartDisplay, search) ||
               Contains(program.StopDisplay, search);
    }

    private static bool MatchesBrowseFilter(IptvListItem item, string filter)
    {
        return filter switch
        {
            "live" => item.EndpointAction is "get_live_streams" or "get_live_categories",
            "vod" => item.EndpointAction is "get_vod_streams" or "get_vod_categories",
            "series" => item.EndpointAction is "get_series" or "get_series_categories" or "get_series_episodes",
            _ => true
        };
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        SaveServiceButton.IsEnabled = !isBusy;
        LoadCacheButton.IsEnabled = !isBusy;
        RefreshCacheButton.IsEnabled = !isBusy;
        RefreshLiveButton.IsEnabled = !isBusy;
        RefreshMoviesButton.IsEnabled = !isBusy;
        RefreshSeriesButton.IsEnabled = !isBusy;
        RefreshGuideButton.IsEnabled = !isBusy;
        ShowFavoritesButton.IsEnabled = !isBusy;
        ShowHistoryButton.IsEnabled = !isBusy;
        ShowContinueWatchingButton.IsEnabled = !isBusy;
        RemoveFavoriteButton.IsEnabled = !isBusy && GetSelectedItem() is not null && IsInFavorites(GetSelectedItem()!);
        ClearHistoryButton.IsEnabled = !isBusy;
        ExportDataButton.IsEnabled = !isBusy;
        ImportDataButton.IsEnabled = !isBusy;
        ServicesComboBox.IsEnabled = !isBusy;
        ServerUrlTextBox.IsEnabled = !isBusy;
        UsernameTextBox.IsEnabled = !isBusy;
        PasswordInput.IsEnabled = !isBusy;
        AccountNameTextBox.IsEnabled = !isBusy;
        CategoryFilterComboBox.IsEnabled = !isBusy;
        SearchTextBox.IsEnabled = !isBusy;
        UpdateSelectedPlayableState();
    }

    private void UpdateCount()
    {
        if (ResultsView is null)
        {
            return;
        }

        if (_isGuideView)
        {
            int guideVisible = EpgView.Cast<object>().Count();
            ResultCountTextBlock.Text = $"{guideVisible:N0} of {_epgPrograms.Count:N0} guide item(s)";
            return;
        }

        int visible = ResultsView.Cast<object>().Count();
        ResultCountTextBlock.Text = $"{visible:N0} of {_results.Count:N0} item(s)";
    }

    private void UpdateSelectedPlayableState()
    {
        IptvListItem? item = GetSelectedItem();
        PlayButton.IsEnabled = item is not null &&
                               (!string.IsNullOrWhiteSpace(item.PlaybackUrl) || item.EndpointAction == "get_series") &&
                               !_isBusy;
        AddFavoriteButton.IsEnabled = item is not null && CanFavorite(item) && !_isBusy;
        RemoveFavoriteButton.IsEnabled = item is not null && IsInFavorites(item) && !_isBusy;
        PauseButton.IsEnabled = _currentItem is not null && !_isBusy;
        RewindButton.IsEnabled = _currentItem is not null && !_isBusy;
    }

    private void StopPlayback()
    {
        SaveContinueWatchingProgress();

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }

        StopTimeshiftBuffer();
        _resumeTimer.Stop();
        _currentItem = null;
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        PauseButton.Content = PauseIcon;
        OverlayPauseButton.Content = PauseIcon;
        RewindButton.IsEnabled = false;
        ResetPlaybackPosition();
        PlayerPlaceholderTextBlock.Visibility = Visibility.Visible;
        NowPlayingTextBlock.Text = "Nothing playing.";
        StatusTextBlock.Text = "Playback stopped.";
        ReturnToDefaultVideoLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        VideoPlayerView.MediaPlayer = null;
        SaveContinueWatchingProgress();
        _resumeTimer.Stop();
        _playbackTimer.Stop();
        _mediaPlayer.Stop();
        StopTimeshiftBuffer();
        _mediaPlayer.EndReached -= MediaPlayer_EndReached;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _httpClient.Dispose();

        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private static Uri BuildPlayerApiUri(XtreamCredentials credentials, string action)
    {
        return BuildPlayerApiUri(credentials, action, []);
    }

    private static Uri BuildPlayerApiUri(XtreamCredentials credentials, string action, params (string Key, string Value)[] extraQuery)
    {
        string query = $"username={Uri.EscapeDataString(credentials.Username)}&password={Uri.EscapeDataString(credentials.Password)}&action={Uri.EscapeDataString(action)}";
        foreach ((string key, string value) in extraQuery)
        {
            query += $"&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        }

        return new Uri($"{credentials.ServerUrl}/player_api.php?{query}");
    }

    private static Uri BuildXmltvUri(XtreamCredentials credentials)
    {
        string query = $"username={Uri.EscapeDataString(credentials.Username)}&password={Uri.EscapeDataString(credentials.Password)}";
        return new Uri($"{credentials.ServerUrl}/xmltv.php?{query}");
    }

    private static string BuildPlaybackUrl(XtreamCredentials credentials, string? streamKind, string itemId, string extension)
    {
        if (string.IsNullOrWhiteSpace(streamKind) || string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        string defaultExtension = streamKind switch
        {
            "movie" or "series" => "mp4",
            _ => "ts"
        };
        string safeExtension = string.IsNullOrWhiteSpace(extension) ? defaultExtension : extension.TrimStart('.');
        return $"{credentials.ServerUrl}/{streamKind}/{Uri.EscapeDataString(credentials.Username)}/{Uri.EscapeDataString(credentials.Password)}/{Uri.EscapeDataString(itemId)}.{safeExtension}";
    }

    private static string GetCachePath(string serviceName)
    {
        string safeName = string.Concat(serviceName.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return Path.Combine(AppDataDirectory, $"cache_{safeName}.json");
    }

    private static string GetCachePath(string serviceName, string mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == "all")
        {
            return GetCachePath(serviceName);
        }

        string safeName = string.Concat(serviceName.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        string safeMode = string.Concat(mode.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return Path.Combine(AppDataDirectory, $"cache_{safeName}_{safeMode}.json");
    }

    private static string GetEpgPath(string serviceName)
    {
        string safeName = string.Concat(serviceName.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return Path.Combine(AppDataDirectory, $"epg_{safeName}.json");
    }

    private static List<IptvListItem> ReadList(string path)
    {
        return ReadList<IptvListItem>(path);
    }

    private static List<T> ReadList<T>(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool SameLibraryItem(IptvListItem left, IptvListItem right)
    {
        return left.ServiceName.Equals(right.ServiceName, StringComparison.OrdinalIgnoreCase) &&
               left.EndpointAction.Equals(right.EndpointAction, StringComparison.OrdinalIgnoreCase) &&
               left.ItemId.Equals(right.ItemId, StringComparison.OrdinalIgnoreCase) &&
               left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCategoryRow(IptvListItem item)
    {
        return item.EndpointAction is "get_live_categories" or "get_vod_categories" or "get_series_categories";
    }

    private static bool IsLiveChannel(IptvListItem item)
    {
        return item.EndpointAction == "get_live_streams";
    }

    private static bool CanFavorite(IptvListItem item)
    {
        return item.EndpointAction is "get_live_streams" or "get_vod_streams" or "get_series" or "get_series_episodes";
    }

    private static bool IsInFavorites(IptvListItem item)
    {
        return ReadList(FavoritesPath).Any(favorite => SameLibraryItem(favorite, item));
    }

    private string GetHomeTypeFilter()
    {
        return HomeTypeFilterComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : "all";
    }

    private static bool MatchesTypeFilter(IptvListItem item, string filter)
    {
        return filter switch
        {
            "live" => item.EndpointAction == "get_live_streams",
            "vod" => item.EndpointAction == "get_vod_streams",
            "series" => item.EndpointAction is "get_series" or "get_series_episodes",
            _ => true
        };
    }

    private void PopulateCategoryFilters()
    {
        string categoryAction = _browseTypeFilter switch
        {
            "live" => "get_live_categories",
            "vod" => "get_vod_categories",
            "series" => "get_series_categories",
            _ => string.Empty
        };

        ResetCategoryFilters();
        if (string.IsNullOrWhiteSpace(categoryAction))
        {
            return;
        }

        foreach (CategoryFilterItem category in _results
                     .Where(item => item.EndpointAction == categoryAction && !string.IsNullOrWhiteSpace(item.ItemId))
                     .OrderBy(item => item.Name)
                     .Select(item => new CategoryFilterItem(item.ItemId, item.Name))
                     .DistinctBy(item => item.Id))
        {
            _categoryFilters.Add(category);
        }

        CategoryFilterComboBox.SelectedValue = "all";
    }

    private void ApplyEpgToResults()
    {
        if (_epgPrograms.Count == 0 || _results.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        Dictionary<string, List<EpgProgram>> programsByChannel = _epgPrograms
            .Where(program => !string.IsNullOrWhiteSpace(program.ChannelId))
            .GroupBy(program => program.ChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(program => program.StartUtc).ToList(), StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < _results.Count; index++)
        {
            IptvListItem item = _results[index];
            if (!IsLiveChannel(item) || string.IsNullOrWhiteSpace(item.EpgChannelId))
            {
                continue;
            }

            if (!programsByChannel.TryGetValue(item.EpgChannelId, out List<EpgProgram>? programs))
            {
                continue;
            }

            EpgProgram? current = programs.FirstOrDefault(program => program.StartUtc <= now && program.StopUtc > now);
            EpgProgram? next = programs.FirstOrDefault(program => program.StartUtc > now);

            _results[index] = item with
            {
                CurrentProgram = current?.Title ?? string.Empty,
                NextProgram = next?.Title ?? string.Empty
            };
        }
    }

    private void ResetCategoryFilters()
    {
        _categoryFilters.Clear();
        _categoryFilters.Add(new CategoryFilterItem("all", "All Categories"));
        CategoryFilterComboBox.SelectedValue = "all";
    }

    private static string GetBrowseModeLabel(string mode)
    {
        return mode switch
        {
            "live" => "Live channels",
            "vod" => "Movies",
            "series" => "Series",
            _ => "All items"
        };
    }

    private static IEnumerable<JsonElement> EnumerateEpisodeObjects(JsonElement episodesElement)
    {
        if (episodesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in episodesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }

            yield break;
        }

        if (episodesElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty season in episodesElement.EnumerateObject())
        {
            if (season.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in season.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }
    }

    private async void PlayStream(IptvListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PlaybackUrl) ||
            !Uri.TryCreate(item.PlaybackUrl, UriKind.Absolute, out Uri? playbackUri))
        {
            StatusTextBlock.Text = "This row does not have a playable stream URL.";
            return;
        }

        if (_currentItem is not null)
        {
            SaveContinueWatchingProgress();
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }

        StopTimeshiftBuffer();
        Uri activePlaybackUri = playbackUri;
        if (item.EndpointAction == "get_live_streams")
        {
            activePlaybackUri = await TryStartTimeshiftBufferAsync(item, playbackUri);
        }

        using Media media = new(_libVlc, activePlaybackUri);
        _mediaPlayer.Play(media);
        _currentItem = item;
        AddToHistory(item);
        _resumeTimer.Start();
        _playbackTimer.Start();

        StopButton.IsEnabled = true;
        PauseButton.IsEnabled = true;
        PauseButton.Content = PauseIcon;
        OverlayPauseButton.Content = PauseIcon;
        RewindButton.IsEnabled = true;
        PlayerPlaceholderTextBlock.Visibility = Visibility.Collapsed;
        NowPlayingTextBlock.Text = $"Playing: {item.Name}";
        StatusTextBlock.Text = item.PositionMs > MinimumResumeMs
            ? $"Playing {item.Name}. Resuming from {FormatDuration(item.PositionMs)}."
            : _playingTimeshiftBuffer
                ? $"Playing {item.Name} with a {LiveBufferMinutes}-minute live buffer."
            : $"Playing {item.Name}.";
    }

    private void ResumeTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentItem is null || itemHasNoResume(_currentItem))
        {
            _resumeTimer.Stop();
            return;
        }

        if (_mediaPlayer.Length <= 0)
        {
            return;
        }

        long resumeMs = _currentItem.PositionMs;
        _currentItem = _currentItem with
        {
            PositionMs = 0,
            LengthMs = _mediaPlayer.Length
        };
        _mediaPlayer.Time = Math.Min(resumeMs, Math.Max(0, _mediaPlayer.Length - 5000));
        _resumeTimer.Stop();

        static bool itemHasNoResume(IptvListItem item)
        {
            return item.PositionMs <= MinimumResumeMs;
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentItem is null)
        {
            ResetPlaybackPosition();
            return;
        }

        long length = Math.Max(0, _mediaPlayer.Length);
        long position = Math.Max(0, _mediaPlayer.Time);
        bool canSeek = _mediaPlayer.IsSeekable && length > 0;

        PlaybackPositionSlider.IsEnabled = canSeek;
        PlaybackPositionSlider.Maximum = canSeek ? length : 100;
        PlaybackLengthTextBlock.Text = length > 0 ? FormatDuration(length) : "Live";

        if (!_isSeekingPlayback)
        {
            PlaybackPositionSlider.Value = canSeek ? Math.Min(position, length) : 0;
            PlaybackPositionTextBlock.Text = length > 0 ? FormatDuration(position) : "0:00";
        }
    }

    private void ResetPlaybackPosition()
    {
        _playbackTimer.Stop();
        _isSeekingPlayback = false;
        PlaybackPositionSlider.IsEnabled = false;
        PlaybackPositionSlider.Value = 0;
        PlaybackPositionSlider.Maximum = 100;
        PlaybackPositionTextBlock.Text = "0:00";
        PlaybackLengthTextBlock.Text = "0:00";
    }

    private async Task<Uri> TryStartTimeshiftBufferAsync(IptvListItem item, Uri sourceUri)
    {
        string? ffmpegPath = FindFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            _playingTimeshiftBuffer = false;
            StatusTextBlock.Text = "Playing live directly. Install ffmpeg or place ffmpeg.exe next to the app to enable the 15-minute live buffer.";
            return sourceUri;
        }

        StopTimeshiftBuffer();
        string bufferRoot = Path.Combine(Path.GetTempPath(), "IptvViewer", "timeshift");
        _timeshiftDirectory = Path.Combine(bufferRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_timeshiftDirectory);

        string playlistPath = Path.Combine(_timeshiftDirectory, "live.m3u8");
        string segmentPattern = Path.Combine(_timeshiftDirectory, "segment_%05d.ts");
        int segmentCount = Math.Max(1, LiveBufferMinutes * 60 / LiveBufferSegmentSeconds);

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourceUri.ToString());
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("hls");
        startInfo.ArgumentList.Add("-hls_time");
        startInfo.ArgumentList.Add(LiveBufferSegmentSeconds.ToString());
        startInfo.ArgumentList.Add("-hls_list_size");
        startInfo.ArgumentList.Add(segmentCount.ToString());
        startInfo.ArgumentList.Add("-hls_flags");
        startInfo.ArgumentList.Add("delete_segments+append_list+omit_endlist");
        startInfo.ArgumentList.Add("-hls_segment_filename");
        startInfo.ArgumentList.Add(segmentPattern);
        startInfo.ArgumentList.Add(playlistPath);

        _timeshiftProcess = Process.Start(startInfo);
        if (_timeshiftProcess is null)
        {
            StopTimeshiftBuffer();
            return sourceUri;
        }

        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (File.Exists(playlistPath) && new FileInfo(playlistPath).Length > 0)
            {
                _playingTimeshiftBuffer = true;
                return new Uri(playlistPath);
            }

            if (_timeshiftProcess.HasExited)
            {
                break;
            }

            await Task.Delay(250);
        }

        StopTimeshiftBuffer();
        StatusTextBlock.Text = $"Could not start live buffer for {item.Name}; playing direct stream.";
        return sourceUri;
    }

    private static string? FindFfmpegPath()
    {
        string appFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(appFfmpeg))
        {
            return appFfmpeg;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void StopTimeshiftBuffer()
    {
        _playingTimeshiftBuffer = false;

        if (_timeshiftProcess is not null)
        {
            try
            {
                if (!_timeshiftProcess.HasExited)
                {
                    _timeshiftProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup for a helper process that may already be gone.
            }
            finally
            {
                _timeshiftProcess.Dispose();
                _timeshiftProcess = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(_timeshiftDirectory) && Directory.Exists(_timeshiftDirectory))
        {
            try
            {
                Directory.Delete(_timeshiftDirectory, recursive: true);
            }
            catch
            {
                // Temp files can be locked briefly while VLC releases the playlist.
            }
            finally
            {
                _timeshiftDirectory = null;
            }
        }
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_currentItem is not null)
            {
                RemoveFromContinueWatching(_currentItem);
                AddToHistory(_currentItem with
                {
                    LocalList = "Watched",
                    PositionMs = 0,
                    LengthMs = _mediaPlayer.Length
                });
            }

            _resumeTimer.Stop();
            _playbackTimer.Stop();
            _currentItem = null;
            StopTimeshiftBuffer();
            StopButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            PauseButton.Content = PauseIcon;
            OverlayPauseButton.Content = PauseIcon;
            RewindButton.IsEnabled = false;
            ResetPlaybackPosition();
            PlayerPlaceholderTextBlock.Visibility = Visibility.Visible;
            NowPlayingTextBlock.Text = "Nothing playing.";
            StatusTextBlock.Text = "Finished video. Removed it from Continue Watching.";
            ReturnToDefaultVideoLayout();
        });
    }

    private void SaveContinueWatchingProgress()
    {
        if (_currentItem is null)
        {
            return;
        }

        long positionMs = Math.Max(_mediaPlayer.Time, _currentItem.PositionMs);
        long lengthMs = Math.Max(_mediaPlayer.Length, _currentItem.LengthMs);

        if (positionMs <= MinimumResumeMs)
        {
            RemoveFromContinueWatching(_currentItem);
            return;
        }

        if (lengthMs > 0 && positionMs >= lengthMs * WatchedThreshold)
        {
            RemoveFromContinueWatching(_currentItem);
            AddToHistory(_currentItem with
            {
                LocalList = "Watched",
                PositionMs = 0,
                LengthMs = lengthMs
            });
            return;
        }

        List<IptvListItem> continueWatching = ReadList(ContinueWatchingPath);
        continueWatching.RemoveAll(item => SameLibraryItem(item, _currentItem));
        continueWatching.Insert(0, _currentItem with
        {
            LocalList = "Continue",
            SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PositionMs = positionMs,
            LengthMs = lengthMs
        });

        SaveJson(ContinueWatchingPath, continueWatching.Take(ContinueWatchingLimit).ToList());
    }

    private static void RemoveFromContinueWatching(IptvListItem item)
    {
        List<IptvListItem> continueWatching = ReadList(ContinueWatchingPath);
        continueWatching.RemoveAll(saved => SameLibraryItem(saved, item));
        SaveJson(ContinueWatchingPath, continueWatching.Take(ContinueWatchingLimit).ToList());
    }

    private static string FormatDuration(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    private static DateTimeOffset ParseXmltvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.MinValue;
        }

        string normalized = value.Trim();
        int spaceIndex = normalized.IndexOf(' ');
        if (spaceIndex > 0 && normalized.Length >= spaceIndex + 6)
        {
            string offset = normalized[(spaceIndex + 1)..];
            if (offset.Length == 5 && (offset[0] == '+' || offset[0] == '-'))
            {
                normalized = $"{normalized[..spaceIndex]} {offset[..3]}:{offset[3..]}";
            }
        }

        string[] formats = ["yyyyMMddHHmmss zzz", "yyyyMMddHHmmss", "yyyyMMddHHmm zzz", "yyyyMMddHHmm"];
        return DateTimeOffset.TryParseExact(
            normalized,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.MinValue;
    }

    private static bool Contains(string value, string search)
    {
        return value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private IptvListItem? GetSelectedItem()
    {
        return ResultsGrid.SelectedItem as IptvListItem ??
               FavoritesGrid.SelectedItem as IptvListItem ??
               HistoryGrid.SelectedItem as IptvListItem;
    }

    private void UpdateSelectedArtwork(IptvListItem? item)
    {
        SelectedDetailsTextBlock.Text = item is null
            ? string.Empty
            : BuildSelectedDetails(item);

        if (item is null || string.IsNullOrWhiteSpace(item.ArtworkUrl) ||
            !Uri.TryCreate(item.ArtworkUrl, UriKind.Absolute, out Uri? artworkUri))
        {
            SelectedArtworkImage.Source = null;
            return;
        }

        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = artworkUri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            SelectedArtworkImage.Source = image;
        }
        catch
        {
            SelectedArtworkImage.Source = null;
        }
    }

    private string BuildSelectedDetails(IptvListItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CurrentProgram))
        {
            return string.IsNullOrWhiteSpace(item.NextProgram)
                ? $"Now: {item.CurrentProgram}"
                : $"Now: {item.CurrentProgram} | Next: {item.NextProgram}";
        }

        if (IsCategoryRow(item))
        {
            return "Double-click to browse this category.";
        }

        return item.EndpointLabel;
    }

    private IptvListItem? FindLiveChannelForGuide(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        IptvListItem? currentResult = _results.FirstOrDefault(item =>
            IsLiveChannel(item) && item.EpgChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase));
        if (currentResult is not null)
        {
            return currentResult;
        }

        if (!TryCreateCredentials(out XtreamCredentials? credentials))
        {
            return null;
        }

        IptvListItem? liveCacheMatch = ReadList(GetCachePath(credentials!.ServiceName, "live")).FirstOrDefault(item =>
            IsLiveChannel(item) && item.EpgChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase));

        return liveCacheMatch ?? ReadList(GetCachePath(credentials.ServiceName)).FirstOrDefault(item =>
            IsLiveChannel(item) && item.EpgChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Number ||
                value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return string.Empty;
    }

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;

        public NativeRect Monitor;

        public NativeRect WorkArea;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }
}

public sealed record SavedService(string Name, string ServerUrl, string Username, string Password);

public sealed record XtreamCredentials(string ServiceName, string ServerUrl, string Username, string Password);

public sealed record EndpointDefinition(string Action, string Label, string? StreamKind);

public sealed record CachedList(string FileName, List<IptvListItem> Items);

public sealed record CategoryFilterItem(string Id, string Name);

public sealed record IptvExportPackage
{
    public List<SavedService> Services { get; init; } = [];

    public List<IptvListItem> Favorites { get; init; } = [];

    public List<IptvListItem> ContinueWatching { get; init; } = [];

    public List<IptvListItem> History { get; init; } = [];

    public List<CachedList> Caches { get; init; } = [];
}

public sealed record EpgProgram
{
    public string ChannelId { get; init; } = string.Empty;

    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset StopUtc { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string StartDisplay => StartUtc == DateTimeOffset.MinValue ? string.Empty : StartUtc.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");

    public string StopDisplay => StopUtc == DateTimeOffset.MinValue ? string.Empty : StopUtc.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");
}

public sealed record IptvListItem
{
    public string ServiceName { get; init; } = string.Empty;

    public string LocalList { get; init; } = string.Empty;

    public string SavedAt { get; init; } = string.Empty;

    public string EndpointAction { get; init; } = string.Empty;

    public string EndpointLabel { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string CategoryId { get; init; } = string.Empty;

    public string EpgChannelId { get; init; } = string.Empty;

    public string CurrentProgram { get; init; } = string.Empty;

    public string NextProgram { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string ContainerExtension { get; init; } = string.Empty;

    public string ArtworkUrl { get; init; } = string.Empty;

    public string PlaybackUrl { get; init; } = string.Empty;

    public long PositionMs { get; init; }

    public long LengthMs { get; init; }
}
