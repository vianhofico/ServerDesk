namespace ServerDesk.Application.Sessions;

public sealed record InteractiveAuthenticationPrompt(
    int Id,
    string Request,
    bool IsSecret);

public sealed record InteractiveAuthenticationChallenge(
    string Username,
    string Instruction,
    IReadOnlyList<InteractiveAuthenticationPrompt> Prompts);

public interface IInteractiveAuthenticationPrompt
{
    ValueTask<IReadOnlyList<string>?> PromptAsync(
        InteractiveAuthenticationChallenge challenge,
        CancellationToken cancellationToken = default);
}
