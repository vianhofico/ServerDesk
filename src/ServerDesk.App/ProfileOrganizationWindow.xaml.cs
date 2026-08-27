using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class ProfileOrganizationWindow : Window
{
    private readonly IServerProfileOrganizationService _organizationService;
    private readonly Guid? _initialServerId;

    public ProfileOrganizationWindow(
        IServerProfileOrganizationService organizationService,
        Guid? initialServerId = null)
    {
        InitializeComponent();
        _organizationService = organizationService;
        _initialServerId = initialServerId;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshAsync(_initialServerId).ConfigureAwait(true);
    }

    private async void ApplyFilterOnClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync((ProfilesListBox.SelectedItem as OrganizedServerProfile)?.Profile.Id)
            .ConfigureAwait(true);
    }

    private async void ClearFilterOnClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        GroupFilterTextBox.Text = string.Empty;
        TagFilterTextBox.Text = string.Empty;
        EnvironmentFilterTextBox.Text = string.Empty;
        FavoritesOnlyCheckBox.IsChecked = false;
        await RefreshAsync((ProfilesListBox.SelectedItem as OrganizedServerProfile)?.Profile.Id)
            .ConfigureAwait(true);
    }

    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is not OrganizedServerProfile selected)
        {
            SelectedProfileNameText.Text = "Select a server";
            SelectedProfileEndpointText.Text = string.Empty;
            GroupNameTextBox.Text = string.Empty;
            TagsTextBox.Text = string.Empty;
            FavoriteCheckBox.IsChecked = false;
            SetEditorEnabled(false);
            return;
        }

        SelectedProfileNameText.Text = selected.Profile.Name;
        SelectedProfileEndpointText.Text = $"{selected.Profile.Username}@{selected.Profile.Host}:{selected.Profile.Port} · {selected.Profile.Environment ?? "Unlabeled"}";
        GroupNameTextBox.Text = selected.Organization.GroupName ?? string.Empty;
        TagsTextBox.Text = string.Join(", ", selected.Organization.Tags);
        FavoriteCheckBox.IsChecked = selected.Organization.IsFavorite;
        EditorStatusText.Text = string.Empty;
        SetEditorEnabled(true);
    }

    private async void SaveOrganizationOnClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is not OrganizedServerProfile selected)
        {
            return;
        }

        SaveOrganizationButton.IsEnabled = false;
        EditorStatusText.Text = "Saving…";
        try
        {
            await _organizationService.SaveAsync(
                    selected.Profile.Id,
                    GroupNameTextBox.Text,
                    TagsTextBox.Text,
                    FavoriteCheckBox.IsChecked == true)
                .ConfigureAwait(true);
            EditorStatusText.Text = "Saved.";
            await RefreshAsync(selected.Profile.Id).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            EditorStatusText.Text = exception.Message;
        }
        finally
        {
            SaveOrganizationButton.IsEnabled = ProfilesListBox.SelectedItem is not null;
        }
    }

    private async Task RefreshAsync(Guid? preferredServerId)
    {
        EditorStatusText.Text = string.Empty;
        var filter = new ServerProfileSearchFilter(
            SearchTextBox.Text,
            GroupFilterTextBox.Text,
            TagFilterTextBox.Text,
            EnvironmentFilterTextBox.Text,
            FavoritesOnlyCheckBox.IsChecked == true);

        try
        {
            var results = await _organizationService.SearchAsync(filter).ConfigureAwait(true);
            ProfilesListBox.ItemsSource = results;
            ResultCountText.Text = results.Count == 1 ? "1 server" : $"{results.Count} servers";
            ProfilesListBox.SelectedItem = preferredServerId is null
                ? results.FirstOrDefault()
                : results.FirstOrDefault(item => item.Profile.Id == preferredServerId.Value) ?? results.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ProfilesListBox.ItemsSource = Array.Empty<OrganizedServerProfile>();
            ResultCountText.Text = "Could not load servers";
            EditorStatusText.Text = exception.Message;
            SetEditorEnabled(false);
        }
    }

    private void SetEditorEnabled(bool enabled)
    {
        GroupNameTextBox.IsEnabled = enabled;
        TagsTextBox.IsEnabled = enabled;
        FavoriteCheckBox.IsEnabled = enabled;
        SaveOrganizationButton.IsEnabled = enabled;
    }
}
