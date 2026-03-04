using System.Diagnostics;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Actions;

public class PowerShellAction : IAction
{
    private readonly string _scriptPath;
    private readonly string? _arguments;

    public PowerShellAction(string scriptPath, string? arguments = null)
    {
        _scriptPath = scriptPath;
        _arguments = arguments;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{_scriptPath}\"";
            if (!string.IsNullOrWhiteSpace(_arguments))
                args += $" {_arguments}";

            var psi = new ProcessStartInfo("pwsh")
            {
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Logger.Instance.Error($"Failed to start PowerShell for script: {_scriptPath}");
                return;
            }

            // Read output asynchronously with a timeout
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                Logger.Instance.Debug($"PowerShell script completed: {_scriptPath} (exit: 0)");
                if (!string.IsNullOrWhiteSpace(output))
                    Logger.Instance.Debug($"PowerShell output: {output.Trim()}");
            }
            else
            {
                Logger.Instance.Debug($"PowerShell script exited with code {process.ExitCode}: {_scriptPath}");
                if (!string.IsNullOrWhiteSpace(error))
                    Logger.Instance.Error($"PowerShell error: {error.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to execute PowerShell script: {_scriptPath}", ex);
        }
    }
}
