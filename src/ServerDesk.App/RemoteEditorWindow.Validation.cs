using ServerDesk.Application.RemoteEditing;

namespace ServerDesk.App;

public partial class RemoteEditorWindow
{
    public void ConfigureValidation(RemoteEditValidationSpec validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ValidatorExecutableBox.Text = validation.Executable;
        ValidatorArgumentsBox.Text = string.Join(Environment.NewLine, validation.Arguments);
    }
}
