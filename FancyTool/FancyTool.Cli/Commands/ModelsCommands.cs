using System.Text.Json;
using System.Text.Json.Nodes;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.Cli.Commands;

internal static class ModelsCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["models list"] = ListAsync;
        map["models install"] = InstallAsync;
        map["models dir"] = DirAsync;
    }

    private static Task<int> ListAsync(CliContext ctx)
    {
        var image = Enum.GetValues<SuperResolutionModel>().Select(model =>
        {
            // VOSR 是九图链路，没有单一下载条目；以整链是否齐备作为安装状态。
            if (model == SuperResolutionModel.Vosr1_4B)
                return new
                {
                    name = ToImageName(model), model = model.ToString(), file = "vosr_*.onnx (9 graphs)",
                    path = AppPaths.ModelsDirectory,
                    installed = ModelManifest.Vosr.All(e => File.Exists(Path.Combine(AppPaths.ModelsDirectory, e.FileName)))
                };
            ModelManifestEntry entry = ModelManifest.GetImageModel(model);
            string path = Path.Combine(AppPaths.ModelsDirectory, entry.FileName);
            return new { name = ToImageName(model), model = model.ToString(), file = entry.FileName, path, installed = File.Exists(path) };
        }).ToList();

        var videoSr = VideoSrModelCatalog.Models.Select(definition =>
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, "VideoSr", definition.FullPrecision.FileName);
            return new { name = ToVideoSrName(definition.Model), model = definition.Model.ToString(), file = path, path, installed = File.Exists(path) };
        }).ToList();

        var interpolation = Enum.GetValues<VideoInterpolationModel>().Select(model =>
        {
            string fileName = model == VideoInterpolationModel.Rife425Lite ? "rife_v4.25_lite_v2.onnx" : "rife_v4.25_v2.onnx";
            string path = Path.Combine(AppPaths.ModelsDirectory, "Interpolation", fileName);
            return new { name = model == VideoInterpolationModel.Rife425Lite ? "rife425-lite" : "rife425", model = model.ToString(), file = fileName, path, installed = File.Exists(path) };
        }).ToList();

        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                modelDirectory = AppPaths.ModelsDirectory,
                image,
                videoSuperResolution = videoSr,
                interpolation,
            });
        }
        else
        {
            ctx.WriteResult(
                (ctx.Zh ? "模型目录: " : "Model directory: ") + AppPaths.ModelsDirectory + "\n\n" +
                Format("image", image.Select(i => (i.name, i.installed))) +
                Format("video-sr", videoSr.Select(i => (i.name, i.installed))) +
                Format("interpolation", interpolation.Select(i => (i.name, i.installed))));
        }
        return Task.FromResult(0);
    }

    private static string Format(string title, IEnumerable<(string Name, bool Installed)> items) =>
        title + ":\n" + string.Join("\n", items.Select(i => $"  {(i.Installed ? "[x]" : "[ ]")} {i.Name}")) + "\n";

    private static async Task<int> InstallAsync(CliContext ctx)
    {
        if (ctx.Args.Get("--dir") is { } directory)
            AppPaths.InitializeModelsDirectory(directory);
        IProgress<ModelDownloadProgress> progress = new Progress<ModelDownloadProgress>(p =>
        {
            if (ctx.Quiet) return;
            double? fraction = p.TotalBytes is > 0 ? (double)p.BytesDownloaded / p.TotalBytes.Value : null;
            ctx.Progress(fraction ?? 0);
            if (p.Stage == ModelDownloadStage.Failed) ctx.Error(p.Message);
        });
        var installed = new List<string>();

        if (ctx.Args.Get("--image") is { } imageName)
        {
            var models = new List<SuperResolutionModel>();
            if (imageName.Equals("all", StringComparison.OrdinalIgnoreCase))
                models.AddRange(Enum.GetValues<SuperResolutionModel>());
            else
                models.Add(ParseImageModel(imageName));
            var downloader = ctx.Get<ModelDownloadService>();
            foreach (SuperResolutionModel model in models.Distinct())
            {
                if (model == SuperResolutionModel.Vosr1_4B)
                {
                    await downloader.EnsureModelsAsync(ModelManifest.Vosr, AppPaths.ModelsDirectory, progress, ctx.CancellationToken).ConfigureAwait(false);
                    installed.AddRange(ModelManifest.Vosr.Select(e => e.FileName));
                    continue;
                }
                ModelManifestEntry entry = ModelManifest.GetImageModel(model);
                await downloader.EnsureModelsAsync([entry], AppPaths.ModelsDirectory, progress, ctx.CancellationToken).ConfigureAwait(false);
                installed.Add(entry.FileName);
            }
        }

        if (ctx.Args.Get("--video-sr") is { } videoName)
        {
            var service = ctx.Get<IVideoSuperResolutionService>();
            service.DownloadProgressChanged += p => progress.Report(p);
            var models = videoName.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? VideoSrModelCatalog.Models.Select(m => m.Model).ToList()
                : [ParseVideoSrModel(videoName)];
            foreach (VideoSrModel model in models)
            {
                await service.EnsureModelAsync(model, ctx.CancellationToken).ConfigureAwait(false);
                installed.Add(VideoSrModelCatalog.Get(model).FullPrecision.FileName);
            }
        }

        if (ctx.Args.Get("--interpolation") is { } interpolationName)
        {
            var service = ctx.Get<IVideoInterpolationService>();
            service.DownloadProgressChanged += p => progress.Report(p);
            var models = interpolationName.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? Enum.GetValues<VideoInterpolationModel>()
                : [interpolationName.ToLowerInvariant() == "rife425" ? VideoInterpolationModel.Rife425 : VideoInterpolationModel.Rife425Lite];
            foreach (VideoInterpolationModel model in models)
            {
                await service.EnsureModelAsync(model, ctx.CancellationToken).ConfigureAwait(false);
                installed.Add(model.ToString());
            }
        }

        ctx.ProgressDone();
        if (installed.Count == 0)
            throw new CliUsageException(ctx.Zh
                ? "指定 --image、--video-sr 或 --interpolation。"
                : "Specify --image, --video-sr, or --interpolation.");
        if (ctx.Json) ctx.WriteJson(new { modelDirectory = AppPaths.ModelsDirectory, installed });
        else ctx.WriteResult((ctx.Zh ? "已安装: " : "Installed: ") + string.Join(", ", installed));
        return 0;
    }

    private static async Task<int> DirAsync(CliContext ctx)
    {
        if (ctx.Args.Get("--set") is { } directory)
        {
            PersistModelDirectory(directory);
            AppPaths.InitializeModelsDirectory(directory);
        }
        else if (ctx.Args.GetBool("--reset"))
        {
            PersistModelDirectory(string.Empty);
            AppPaths.InitializeModelsDirectory(null);
        }
        if (ctx.Json) ctx.WriteJson(new { modelDirectory = AppPaths.ModelsDirectory });
        else ctx.WriteResult(AppPaths.ModelsDirectory);
        await Task.CompletedTask;
        return 0;
    }

    /// <summary>只修改 preferences.json 的模型目录字段，保留主题/语言等用户设置。</summary>
    private static void PersistModelDirectory(string directory)
    {
        string path = Path.Combine(AppPaths.DataDirectory, "preferences.json");
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { root = new JsonObject(); }
        }
        else
        {
            root = new JsonObject();
        }
        if (root["Preferences"] is not JsonObject preferences)
        {
            preferences = new JsonObject();
            root["Preferences"] = preferences;
        }
        preferences["ModelStorageDirectory"] = directory;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private static SuperResolutionModel ParseImageModel(string value) => value.ToLowerInvariant() switch
    {
        "realesrgan-x4plus" or "realesrgan-x4plus-fp16" => SuperResolutionModel.RealEsrganX4Plus,
        "realesrgan-x4plus-anime" => SuperResolutionModel.RealEsrganX4PlusAnime,
        "realesr-general-x4v3" or "realesrgan-general-x4v3" => SuperResolutionModel.RealEsrganGeneralX4V3,
        "clearreality" or "span-clearreality" or "4x-clearrealityv1" => SuperResolutionModel.SpanClearRealityV1,
        "vosr" or "vosr-1.4b" or "vosr14b" => SuperResolutionModel.Vosr1_4B,
        _ => throw new CliUsageException($"Unknown image model: {value}"),
    };

    private static VideoSrModel ParseVideoSrModel(string value) => value.ToLowerInvariant() switch
    {
        "anime4k-cnn-s" => VideoSrModel.Anime4KCnnS,
        "anime4k-cnn-m" => VideoSrModel.Anime4KCnnM,
        "fsrcnnx-s" => VideoSrModel.FsrcnnxS,
        "fsrcnnx-m" => VideoSrModel.FsrcnnxM,
        "ravu-lite-r2" => VideoSrModel.RavuLiteR2,
        "ravu-lite-r3" => VideoSrModel.RavuLiteR3,
        _ => throw new CliUsageException($"Unknown video SR model: {value}"),
    };

    private static string ToImageName(SuperResolutionModel model) => model switch
    {
        SuperResolutionModel.RealEsrganX4Plus => "realesrgan-x4plus",
        SuperResolutionModel.RealEsrganX4PlusAnime => "realesrgan-x4plus-anime",
        SuperResolutionModel.RealEsrganGeneralX4V3 => "realesr-general-x4v3",
        SuperResolutionModel.SpanClearRealityV1 => "clearreality",
        SuperResolutionModel.Vosr1_4B => "vosr-1.4b",
        _ => model.ToString(),
    };

    private static string ToVideoSrName(VideoSrModel model) => model switch
    {
        VideoSrModel.Anime4KCnnS => "anime4k-cnn-s",
        VideoSrModel.Anime4KCnnM => "anime4k-cnn-m",
        VideoSrModel.FsrcnnxS => "fsrcnnx-s",
        VideoSrModel.FsrcnnxM => "fsrcnnx-m",
        VideoSrModel.RavuLiteR2 => "ravu-lite-r2",
        VideoSrModel.RavuLiteR3 => "ravu-lite-r3",
        _ => model.ToString(),
    };
}