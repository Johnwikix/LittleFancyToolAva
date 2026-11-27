using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 会话 ComboBox 的下拉项：<see cref="Session"/> 非空表示一个会话；为空表示末尾的"新建"项，
    /// 例如串口的"添加端口 COMx"、TCP / UDP 的"新建会话"。
    /// </summary>
    public sealed class SessionChoiceItem : ObservableObject
    {
        private readonly string _addLabelKey;

        private SessionChoiceItem(TerminalSessionViewModelBase? session, string addLabelKey, string? addPayload)
        {
            Session = session;
            _addLabelKey = addLabelKey;
            AddPayload = addPayload;
            if (session is not null)
            {
                session.PropertyChanged += OnSessionPropertyChanged;
            }
        }

        public static SessionChoiceItem ForSession(TerminalSessionViewModelBase session) => new(session, string.Empty, null);

        public static SessionChoiceItem ForAdd(string labelKey, string? payload = null) => new(null, labelKey, payload);

        public override string ToString() => DisplayName;

        public TerminalSessionViewModelBase? Session { get; }

        /// <summary>新建项携带的默认值：串口为端口名，没有可枚举来源时为空。</summary>
        public string? AddPayload { get; }

        public bool HasSession => Session is not null;

        public bool IsAddItem => Session is null;

        public ConnectionStatus State => Session?.ConnectionStatus ?? ConnectionStatus.Idle;

        public string DisplayName => Session?.DisplayName ?? LocalizationRegistry.Get(_addLabelKey, AddPayload);

        public string StateText => Session?.StateText ?? string.Empty;

        public bool HasUnread => Session?.HasUnread ?? false;

        public string UnreadText => Session?.UnreadText ?? string.Empty;

        /// <summary>语言切换后重发全部展示属性。</summary>
        public void Refresh() => RaiseAll();

        /// <summary>下拉项被移出后不再跟踪会话。</summary>
        public void Detach()
        {
            if (Session is not null)
            {
                Session.PropertyChanged -= OnSessionPropertyChanged;
            }
        }

        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadText));
        }
    }
}
