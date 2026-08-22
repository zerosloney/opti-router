namespace OptiRouter.Components.Services;

public enum ToastType
{
    Success,
    Info,
    Warning,
    Error
}

public class ToastItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ToastType Type { get; set; } = ToastType.Info;
    public string Message { get; set; } = "";
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int DurationMs { get; set; } = 3500;
}

public class ToastService
{
    private readonly List<ToastItem> _toasts = new();
    public IReadOnlyList<ToastItem> Toasts => _toasts;

    public event Action? OnChange;

    public void Show(ToastType type, string message, string? title = null, int durationMs = 3500)
    {
        var item = new ToastItem
        {
            Type = type,
            Message = message,
            Title = title,
            DurationMs = durationMs
        };

        lock (_toasts)
        {
            _toasts.Add(item);
            if (_toasts.Count > 6)
            {
                _toasts.RemoveAt(0);
            }
        }

        NotifyStateChanged();

        if (durationMs > 0)
        {
            _ = AutoDismissAsync(item.Id, durationMs);
        }
    }

    public void ShowSuccess(string message, string? title = null, int durationMs = 3500)
        => Show(ToastType.Success, message, title, durationMs);

    public void ShowInfo(string message, string? title = null, int durationMs = 3500)
        => Show(ToastType.Info, message, title, durationMs);

    public void ShowWarning(string message, string? title = null, int durationMs = 4500)
        => Show(ToastType.Warning, message, title, durationMs);

    public void ShowError(string message, string? title = null, int durationMs = 6000)
        => Show(ToastType.Error, message, title, durationMs);

    public void Dismiss(string id)
    {
        lock (_toasts)
        {
            var idx = _toasts.FindIndex(t => t.Id == id);
            if (idx >= 0)
            {
                _toasts.RemoveAt(idx);
            }
        }
        NotifyStateChanged();
    }

    private async Task AutoDismissAsync(string id, int delayMs)
    {
        await Task.Delay(delayMs);
        Dismiss(id);
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
