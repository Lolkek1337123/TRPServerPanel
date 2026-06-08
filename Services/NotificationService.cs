using System;
using System.Collections.Generic;

namespace TRPServerPanel.Services
{
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error,
        Security
    }

    public class Notification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class NotificationService
    {
        public event Action<Notification>? OnNotificationReceived;
        private readonly List<Notification> _history = new();

        public void Send(string title, string message, NotificationType type = NotificationType.Info)
        {
            var notification = new Notification
            {
                Title = title,
                Message = message,
                Type = type
            };

            lock (_history)
            {
                _history.Insert(0, notification);
                if (_history.Count > 50) _history.RemoveAt(50);
            }

            OnNotificationReceived?.Invoke(notification);
        }

        public void ShowNotification(string title, string message, string type = "info")
        {
            var nType = type.ToLower() switch
            {
                "success" => NotificationType.Success,
                "error" => NotificationType.Error,
                "warning" => NotificationType.Warning,
                "security" => NotificationType.Security,
                _ => NotificationType.Info
            };
            Send(title, message, nType);
        }

        public void Success(string title, string message) => Send(title, message, NotificationType.Success);
        public void Error(string title, string message) => Send(title, message, NotificationType.Error);
        public void Info(string title, string message) => Send(title, message, NotificationType.Info);
        public void Warning(string title, string message) => Send(title, message, NotificationType.Warning);

        public List<Notification> GetHistory()
        {
            lock (_history)
            {
                return new List<Notification>(_history);
            }
        }
    }
}
