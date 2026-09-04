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
            SetStateResource(
                ServerDesk.Application.RemoteFiles.RemoteExplorerUiState.Error,
                "Loc.Explorer.Editor.SelectText");
            return;
        }

        if (EditorService is not { } editorService)
        {
            SetStateResource(
                ServerDesk.Application.RemoteFiles.RemoteExplorerUiState.Error,
                "Loc.Explorer.Editor.Unavailable");
            return;
        }

        var window = new RemoteEditorWindow(editorService, _profile, row.Path)
        {
            Owner = this,
        };
        window.Show();
    }
}
