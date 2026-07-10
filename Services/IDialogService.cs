using System.Threading.Tasks;

namespace LittleFancyToolAva.Services
{
    public interface IDialogService
    {
        Task<TResult?> ShowDialogAsync<TResult>(object viewModel) where TResult : class;
    }
}
