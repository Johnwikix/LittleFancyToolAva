using System.Threading.Tasks;
using System.Threading;
namespace FancyToolAva.Algorithms
{
    public interface IFileEncryption
    {
        public Task EncryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, CancellationToken cancellationToken = default);
        public Task DecryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, CancellationToken cancellationToken = default);
    }
}


