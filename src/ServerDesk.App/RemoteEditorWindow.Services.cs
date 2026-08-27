using ServerDesk.Application.RemoteEditing;

namespace ServerDesk.App;

public partial class RemoteEditorWindow
{
    internal static IRemoteFileEditorService? EditorService { get; set; }
}
