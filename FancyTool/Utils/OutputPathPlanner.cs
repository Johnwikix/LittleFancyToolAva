namespace FancyToolAva.Utils;

/// <summary>
/// 批量转码的输出路径规划：目录镜像 + 批内唯一命名。
/// 路径必须在并行执行前统一规划（串行完成 File.Exists 探测与同名去重），
/// 并行执行期各任务只写自己预分到的路径，杜绝并发同名/临时文件竞态。
/// </summary>
public static class OutputPathPlanner
{
    /// <summary>
    /// 指定输出目录时解析单个文件的输出目录：文件夹添加的文件按其相对根目录的路径
    /// 镜像重建子目录；单独添加（无根目录）或已不在根目录下的文件平铺到输出根目录。
    /// </summary>
    public static string ResolveOutputDir(string outputFolder, string filePath, string? rootFolder)
    {
        if (string.IsNullOrEmpty(rootFolder)) return outputFolder;
        string sourceDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        if (sourceDir.Length == 0) return outputFolder;
        string rel = Path.GetRelativePath(rootFolder, sourceDir);
        if (rel.Length == 0 || rel == "." || rel.StartsWith("..", StringComparison.Ordinal))
            return outputFolder;
        return Path.Combine(outputFolder, rel);
    }

    /// <summary>
    /// 规划一个批内唯一的输出路径：同名候选按 _1、_2 递增避让批内已占用名字与磁盘上
    /// 已存在的文件（与历史上逐文件执行时的 GetOutputPath 行为一致），并创建目标目录。
    /// </summary>
    /// <param name="taken">批内已占用文件名，键为目录（大小写不敏感）。</param>
    public static string PlanUniquePath(string folder, string stem, string ext, bool addSuffix,
        Dictionary<string, HashSet<string>> taken)
    {
        Directory.CreateDirectory(folder);
        if (!taken.TryGetValue(folder, out HashSet<string>? used))
        {
            used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            taken[folder] = used;
        }

        string baseName = addSuffix ? stem + "_output" : stem;
        string candidate = baseName + ext;
        int i = 0;
        while (used.Contains(candidate) || File.Exists(Path.Combine(folder, candidate)))
            candidate = $"{baseName}_{++i}{ext}";
        used.Add(candidate);
        return Path.Combine(folder, candidate);
    }
}
