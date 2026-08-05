using PakStudio.App.Commands;
using PakStudio.App.Services;
using PakStudio.Core.Documents;

namespace PakStudio.App.ViewModels;

public enum SettingsTab
{
    General,
    Archive,
    Preview,
    Explorer,
}

public sealed record SettingsOption<TValue>(string Label, TValue Value);

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PakScapeSettings _settings = PakScapeSettings.Current;
    private SettingsTab _selectedTab = SettingsTab.General;
    private bool _isAssociated;

    public SettingsViewModel()
    {
        _isAssociated = ShellIntegrationService.IsAssociated;
        AssociateCommand = new RelayCommand(Associate, () => !IsAssociated);
        ManageAssociationsCommand = new RelayCommand(ShellIntegrationService.OpenDefaultAppsSettings);
    }

    public RelayCommand AssociateCommand { get; }

    public RelayCommand ManageAssociationsCommand { get; }

    public SettingsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(SelectedTabTitle));
                OnPropertyChanged(nameof(IsGeneralSelected));
                OnPropertyChanged(nameof(IsArchiveSelected));
                OnPropertyChanged(nameof(IsPreviewSelected));
                OnPropertyChanged(nameof(IsExplorerSelected));
            }
        }
    }

    public string SelectedTabTitle => SelectedTab.ToString();

    /* The tab strip binds these two-way; the radio group only ever sets one to true. */
    public bool IsGeneralSelected
    {
        get => SelectedTab == SettingsTab.General;
        set => SelectTab(value, SettingsTab.General);
    }

    public bool IsArchiveSelected
    {
        get => SelectedTab == SettingsTab.Archive;
        set => SelectTab(value, SettingsTab.Archive);
    }

    public bool IsPreviewSelected
    {
        get => SelectedTab == SettingsTab.Preview;
        set => SelectTab(value, SettingsTab.Preview);
    }

    public bool IsExplorerSelected
    {
        get => SelectedTab == SettingsTab.Explorer;
        set => SelectTab(value, SettingsTab.Explorer);
    }

    private void SelectTab(bool isSelected, SettingsTab tab)
    {
        if (isSelected)
        {
            SelectedTab = tab;
        }
    }

    public IReadOnlyList<SettingsOption<AppearancePreference>> AppearanceOptions { get; } =
    [
        new("Automatic", AppearancePreference.Automatic),
        new("Light", AppearancePreference.Light),
        new("Dark", AppearancePreference.Dark),
    ];

    public IReadOnlyList<SettingsOption<ArchiveViewMode>> DefaultViewOptions { get; } =
    [
        new("Details", ArchiveViewMode.Details),
        new("List", ArchiveViewMode.List),
        new("Large Icons", ArchiveViewMode.LargeIcons),
        new("Small Icons", ArchiveViewMode.SmallIcons),
    ];

    public IReadOnlyList<SettingsOption<DefaultSortPreference>> DefaultSortOptions { get; } =
    [
        new("Name", DefaultSortPreference.Name),
        new("Type", DefaultSortPreference.Type),
        new("Size", DefaultSortPreference.Size),
    ];

    public IReadOnlyList<SettingsOption<bool>> SortOrderOptions { get; } =
    [
        new("Ascending", true),
        new("Descending", false),
    ];

    public AppearancePreference Appearance
    {
        get => _settings.Appearance;
        set
        {
            if (_settings.Appearance == value)
            {
                return;
            }

            _settings.Appearance = value;
            OnPropertyChanged();
        }
    }

    public ArchiveViewMode DefaultView
    {
        get => _settings.DefaultView;
        set
        {
            if (_settings.DefaultView == value)
            {
                return;
            }

            _settings.DefaultView = value;
            OnPropertyChanged();
        }
    }

    public DefaultSortPreference DefaultSort
    {
        get => _settings.DefaultSort;
        set
        {
            if (_settings.DefaultSort == value)
            {
                return;
            }

            _settings.DefaultSort = value;
            OnPropertyChanged();
        }
    }

    public bool DefaultSortAscending
    {
        get => _settings.DefaultSortAscending;
        set
        {
            if (_settings.DefaultSortAscending == value)
            {
                return;
            }

            _settings.DefaultSortAscending = value;
            OnPropertyChanged();
        }
    }

    public double TextSize
    {
        get => _settings.TextSize;
        set
        {
            if (Math.Abs(_settings.TextSize - value) < 0.01)
            {
                return;
            }

            _settings.TextSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TextSizeCaption));
        }
    }

    public string TextSizeCaption => Math.Abs(TextSize - PakScapeSettings.DefaultTextSize) < 0.01
        ? "Default"
        : $"{TextSize:0} pt";

    public bool ConfirmDeletion
    {
        get => _settings.ConfirmDeletion;
        set
        {
            if (_settings.ConfirmDeletion == value)
            {
                return;
            }

            _settings.ConfirmDeletion = value;
            OnPropertyChanged();
        }
    }

    public bool ConfirmOverwrite
    {
        get => _settings.ConfirmOverwrite;
        set
        {
            if (_settings.ConfirmOverwrite == value)
            {
                return;
            }

            _settings.ConfirmOverwrite = value;
            OnPropertyChanged();
        }
    }

    public bool BackupBeforeSave
    {
        get => _settings.BackupBeforeSave;
        set
        {
            if (_settings.BackupBeforeSave == value)
            {
                return;
            }

            _settings.BackupBeforeSave = value;
            OnPropertyChanged();
        }
    }

    public bool QuickPreviewOnSelection
    {
        get => _settings.QuickPreviewOnSelection;
        set
        {
            if (_settings.QuickPreviewOnSelection == value)
            {
                return;
            }

            _settings.QuickPreviewOnSelection = value;
            OnPropertyChanged();
        }
    }

    public bool AnimateModels
    {
        get => _settings.AnimateModels;
        set
        {
            if (_settings.AnimateModels == value)
            {
                return;
            }

            _settings.AnimateModels = value;
            OnPropertyChanged();
        }
    }

    public bool ShowBspMarkers
    {
        get => _settings.ShowBspMarkers;
        set
        {
            if (_settings.ShowBspMarkers == value)
            {
                return;
            }

            _settings.ShowBspMarkers = value;
            OnPropertyChanged();
        }
    }

    public bool ExplorerActionsEnabled
    {
        get => _settings.ExplorerActionsEnabled;
        set
        {
            if (_settings.ExplorerActionsEnabled == value)
            {
                return;
            }

            _settings.ExplorerActionsEnabled = value;
            ShellIntegrationService.UpdateExplorerActions(value);
            OnPropertyChanged();
        }
    }

    public bool IsAssociated
    {
        get => _isAssociated;
        private set
        {
            if (SetProperty(ref _isAssociated, value))
            {
                OnPropertyChanged(nameof(AssociateButtonText));
                AssociateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string AssociateButtonText => IsAssociated ? "Associated" : "Associate with PakScape";

    private void Associate()
    {
        ShellIntegrationService.Associate();
        IsAssociated = ShellIntegrationService.IsAssociated;
    }
}
