using CommunityToolkit.Mvvm.ComponentModel;

// TODO: 待完善 — Modbus 专用模型，后续可完善后重新启用
namespace LittleFancyToolAva.Models
{
    public partial class PollTableRow : ObservableObject
    {
        public string Address
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string ValueDec
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string ValueHex
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string LastUpdate
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;
    }
}
