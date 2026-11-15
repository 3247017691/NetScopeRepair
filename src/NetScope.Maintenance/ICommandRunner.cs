namespace NetScope.Maintenance;

/// <summary>Injectable seam for fixed local commands; tests never spawn a real process.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunPowerShellAsync(string script, CancellationToken cancellationToken);
    Task<CommandResult> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken);
    Task<CommandResult> RunJsonAsync(string script, CancellationToken cancellationToken);
}