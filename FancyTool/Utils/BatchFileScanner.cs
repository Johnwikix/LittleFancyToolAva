namespace FancyToolAva.Utils;

/// <summary>
/// 批量添加文件的统一扫描器：在后台线程递归枚举（大目录可达分钟级，不能占 UI 线程），
/// 单个不可访问子目录不致命；返回去重后的 (文件, 来源根目录) 列表，供 UI 线程一次性批量入列表。
/// </summary>
public static class BatchFileScanner
{
    /// <param name="roots">拖入/选中的路径，目录递归展开，文件直收（null 根表示单独添加）。</param>
    /// <param name="accept">扩展名过滤谓词。</param>
    /// <param name="existing">已有文件路径集合（会被写入本次发现，用于跨次去重）。</param>
    public static List<(string File, string? Root)> Scan(
        IEnumerable<string> roots, Func<string, bool> accept, HashSet<string> existing)
    {
        var result = new List<(string, string?)>();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
        };
        foreach (string path in roots)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", opts))
                {
                    if (accept(file) && existing.Add(file))
                        result.Add((file, path));
                }
            }
            else if (File.Exists(path) && accept(path) && existing.Add(path))
            {
                result.Add((path, null));
            }
        }
        return result;
    }
}
