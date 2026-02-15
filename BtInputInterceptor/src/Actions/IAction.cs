namespace BtInputInterceptor.Actions;

public interface IAction
{
    Task ExecuteAsync(CancellationToken ct = default);
}
