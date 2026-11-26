using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Lang.Avalonia;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 串口 ComboBox 下拉项：<see cref="Session"/> 非空表示已添加会话；为空表示扫描到但未添加的端口。
    /// </summary>
    public sealed class SerialChoiceItem : ObservableObject
    {
        public SerialSessionViewModel? Session { get; }

        public string PortName { get; }

        private SerialChoiceItem(SerialSessionViewModel? session, string portName)
        {
            Session = session;
            PortName = portName;
            if (session is not null)
            {
                session.PropertyChanged += OnSessionPropertyChanged;
            }
        }

        public static SerialChoiceItem ForSession(SerialSessionViewModel session) => new(session, session.SelectedPort);

        public static SerialChoiceItem ForPort(string portName) => new(null, portName);

        public override string ToString() => DisplayName;

        public bool HasSession => Session is not null;

        public bool IsAddPort => Session is null;

        public ConnectionStatus State => Session?.ConnectionStatus ?? ConnectionStatus.Idle;

        public string DisplayName => Session?.DisplayName ?? LocalizationRegistry.Get("Serial.Menu_AddPort", PortName);

        public string Summary => Session?.ParameterSummary ?? string.Empty;

        public string RxTxSummary => Session?.RxTxSummary ?? string.Empty;

        public string StateText => Session?.StateText ?? string.Empty;

        public bool HasUnread => Session?.HasUnread ?? false;

        public string UnreadText => Session?.UnreadText ?? string.Empty;

        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(RxTxSummary));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadText));
        }
    }
}