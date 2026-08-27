using System.Windows;
using ServerDesk.Application.RemoteEditing;

namespace ServerDesk.App;

public partial class RemoteExplorerWindow
{
    internal IRemoteFileEditorService? EditorService { get; set; }

    private void EditOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { IsDownloadable: true } row)
        {
            SetState(ServerDesk.Application.RemoteFiles.RemoteExplorerUiState.Error, "Select a text file to edit.");
            return;
        }

        if (EditorService is not { } editorService)
        {
            SetState(ServerDesk.Application.RemoteFiles.RemoteExplorerUiState.Error, "Remote editor service is unavailable.");
            return;
        }

        var window = new RemoteEditorWindow(editorService, _profile, row.Path)
        {
            Owner = this,
        };
        window.Show();
    }
}
