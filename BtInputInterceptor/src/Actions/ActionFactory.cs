namespace BtInputInterceptor.Actions;

public static class ActionFactory
{
    public static IAction Create(ActionConfig config) => config.Type.ToLowerInvariant() switch
    {
        "launch" => new LaunchProcessAction(
            config.Path ?? throw new ArgumentException("Launch action requires 'path'"),
            config.Arguments),

        "keystroke" => new SendKeystrokeAction(
            config.Keystroke ?? throw new ArgumentException("Keystroke action requires 'keystroke'")),

        "webhook" => new WebhookAction(
            config.Url ?? throw new ArgumentException("Webhook action requires 'url'"),
            config.HttpMethod ?? "POST",
            config.Body),

        "powershell" => new PowerShellAction(
            config.Path ?? throw new ArgumentException("PowerShell action requires 'path'"),
            config.Arguments),

        "notification" => new NotificationAction(
            config.Message ?? "Gesture triggered"),

        _ => throw new ArgumentException($"Unknown action type: {config.Type}")
    };
}
