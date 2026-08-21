using System.Threading.Tasks;

namespace FancyToolAva.Services
{
    public interface IDialogService
    {
        Task<TResult?> ShowDialogAsync<TResult>(object viewModel) where TResult : class;
    }
}
