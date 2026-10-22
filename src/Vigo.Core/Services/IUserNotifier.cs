namespace Vigo.Core.Services;

public enum NotificationSeverity { Info, Success, Warning, Error }

/// <summary>In-app toasts (bottom-right corner in the mockups).</summary>
public interface IUserNotifier
{
    void Notify(NotificationSeverity severity, string title, string? message = null);
}
