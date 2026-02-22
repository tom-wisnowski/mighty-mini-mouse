using System.Diagnostics;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Actions;

public class LaunchProcessAction : IAction
{
    private readonly string _path;
    private readonly string? _arguments;

    public LaunchProcessAction(string path, string? arguments = null)
    {
        _path = path;
        _arguments = arguments;
    }

    public Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_path)
            {
                Arguments = _arguments ?? "",
                UseShellExecute = true
            };
            Process.Start(psi);
            Logger.Instance.Info($"Launched process: {_path} {_arguments}");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to launch process: {_path}", ex);
        }
        return Task.CompletedTask;
    }
}
