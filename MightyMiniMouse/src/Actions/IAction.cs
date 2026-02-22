namespace MightyMiniMouse.Actions;

public interface IAction
{
    Task ExecuteAsync(CancellationToken ct = default);
}
