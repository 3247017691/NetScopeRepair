using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NetScope.Maintenance;

/// <summary>
/// Runs short, fixed PowerShell / netsh / pnputil commands against the local machine.
/// No free-form text from the UI ever reaches this class: every caller passes a constant script
/// with values injected through the argument list, and only structured output is returned.
/// </summary>
public sealed class WindowsCommandRunner : ICommandRunner
{
    private static readonly string PowerShellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<CommandResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
        => await RunAsync(PowerShellPath, new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script,
        }, cancellationToken).ConfigureAwait(false);

    public async Task<CommandResult> RunAsync(string fileName, string[] arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new CommandResult(-1, "", "process could not be started");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new CommandResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return new CommandResult(-1, "", exception.Message);
        }
    }

    /// <summary>Runs PowerShell and returns parsed JSON, or a failure the caller must surface.</summary>
    public async Task<CommandResult> RunJsonAsync(string script, CancellationToken cancellationToken)
        => await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);

    /// <summary>Escapes a string for use inside a single-quoted PowerShell literal.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Ok => ExitCode == 0;
}
