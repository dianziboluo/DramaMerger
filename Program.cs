using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DramaMerger;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
            return SelfTest.Run() ? 0 : 1;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

// ---------------- 数据模型 ----------------

/// <summary>一集：集数 + 对应的视频文件。</summary>
public sealed record Episode(int Number, string FilePath);

/// <summary>合并清单：记录已合并了哪些集，用于下次增量更新。</summary>
public sealed class MergeManifest
{
    public string DramaName { get; set; } = "";
    public List<string> MergedFiles { get; set; } = new();   // 按合并顺序记录的源文件名（相对源目录）
    public List<string> MergedSizes { get; set; } = new();   // 与 MergedFiles 对应的文件字节数
    public string OutputFile { get; set; } = "";             // 当前合并输出的文件名（相对输出目录）
    public int FirstEpisode { get; set; } = 1;               // 已合并的首集集数（输出命名 第X-XX集 用）

    [JsonIgnore]
    public int MergedCount => MergedFiles.Count;
}

public enum PlanKind { FullMerge, IncrementalMerge, UpToDate, NothingToMerge, MissingEpisodes }

public sealed class MergePlan
{
    public PlanKind Kind { get; set; }
    public List<Episode> EpisodesToMerge { get; set; } = new();
    public string OutputName { get; set; } = "";
    public int NewMaxEpisode { get; set; }
    public int OldMaxEpisode { get; set; }
    public int MissingFrom { get; set; }
    public int MissingTo { get; set; }
    public long TotalBytes { get; set; }   // 本次要写入的数据量（估算）
}

// ---------------- 核心逻辑（可独立测试，不依赖 UI） ----------------

public static class Merger
{
    public static readonly string[] VideoExts = { ".mp4", ".mkv", ".mov", ".avi", ".ts", ".flv", ".webm", ".m4v" };

    /// <summary>
    /// 扫描一个文件夹的剧集：识别文件名中的“第NN集”，按集数排序。
    /// 同一集数有多个视频文件时报错（提示用户清理）。
    /// </summary>
    public static List<Episode> ScanEpisodes(string folder) => CollectEpisodes(new[] { folder });

    /// <summary>
    /// 跨多个文件夹合并扫描剧集（按集数排序）。
    /// 同一集数出现在多个文件夹时视为冲突并报错，提示用户只保留一个。
    /// </summary>
    public static List<Episode> CollectEpisodes(IReadOnlyList<string> folders)
    {
        var map = new SortedDictionary<int, string>();
        foreach (var folder in folders)
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                if (!VideoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                int num = ParseEpisodeNumber(Path.GetFileNameWithoutExtension(f));
                if (num <= 0) continue; // 不带集数编号的视频（SP、预告等）不参与
                if (map.TryGetValue(num, out var prev))
                    throw new InvalidOperationException(
                        $"第 {num} 集在多个文件夹中都有视频文件，请只保留一个：\n{prev}\n{f}");
                map[num] = f;
            }
        }
        return map.Select(kv => new Episode(kv.Key, kv.Value)).ToList();
    }

    /// <summary>从文件名解析集数：匹配“第NN集”，找不到返回 0。</summary>
    public static int ParseEpisodeNumber(string fileNameNoExt)
        => Regex.Match(fileNameNoExt, @"第(\d{1,4})集") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>从首个剧集文件名推断剧名：去掉“第NN集”及其后缀。</summary>
    public static string GuessDramaName(string episodeFileNameNoExt)
        => Regex.Replace(episodeFileNameNoExt, @"第\d{1,4}集.*$", "").Trim();

    /// <summary>输出命名模板：“{剧名} 第{首集}-{末集}集.mp4”。</summary>
    public static string BuildOutputName(string dramaName, int firstEpisode, int lastEpisode)
        => $"{dramaName} 第{firstEpisode}-{lastEpisode}集.mp4";

    public static string ManifestPath(string outputDir, string dramaName)
        => Path.Combine(outputDir, $".merge-manifest-{SanitizeName(dramaName)}.json");

    public static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public static MergeManifest? LoadManifest(string outputDir, string dramaName)
    {
        string path = ManifestPath(outputDir, dramaName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MergeManifest>(File.ReadAllText(path)); }
        catch { return null; } // 清单损坏视作不存在，走全新合并
    }

    public static void SaveManifest(string outputDir, MergeManifest manifest)
        => File.WriteAllText(
            ManifestPath(outputDir, manifest.DramaName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    public sealed record DramaPlan(string DramaName, string SourceFolder, MergePlan Plan, MergeManifest? Manifest);

    /// <summary>对（可能多个）剧集文件夹制定计划。dramaName 为 null 时从文件名推断剧名。</summary>
    public static DramaPlan PlanDrama(IReadOnlyList<string> srcFolders, string outputDir, string? dramaName = null)
    {
        var episodes = CollectEpisodes(srcFolders);
        string name = dramaName ?? (episodes.Count > 0
            ? GuessDramaName(Path.GetFileNameWithoutExtension(episodes[0].FilePath))
            : Path.GetFileName(srcFolders[0]));
        var manifest = LoadManifest(outputDir, name);
        var plan = Plan(episodes, manifest, srcFolders, outputDir, name);
        return new DramaPlan(name, string.Join("  +  ", srcFolders), plan, manifest);
    }

    /// <summary>批量模式：列出 root 的子文件夹中包含剧集的（仅一层）。</summary>
    public static List<string> ListDramaFolders(string root)
        => Directory.GetDirectories(root)
            .Where(d => ScanEpisodes(d).Count > 0)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>合并成功后更新清单（全新合并重建列表，增量合并追加）。</summary>
    public static void UpdateManifestAfterMerge(MergeManifest manifest, MergePlan plan)
    {
        if (plan.Kind == PlanKind.FullMerge || manifest.MergedFiles.Count == 0)
        {
            manifest.MergedFiles = plan.EpisodesToMerge.Select(e => Path.GetFileName(e.FilePath)).ToList();
            manifest.MergedSizes = plan.EpisodesToMerge.Select(e => new FileInfo(e.FilePath).Length.ToString()).ToList();
            manifest.FirstEpisode = plan.EpisodesToMerge.Count > 0 ? plan.EpisodesToMerge[0].Number : 1;
        }
        else
        {
            foreach (var e in plan.EpisodesToMerge)
            {
                manifest.MergedFiles.Add(Path.GetFileName(e.FilePath));
                manifest.MergedSizes.Add(new FileInfo(e.FilePath).Length.ToString());
            }
        }
        manifest.OutputFile = plan.OutputName;
    }

    /// <summary>全新合并计划：要求集数从第 1 集开始连续。</summary>
    static MergePlan PlanFullMerge(List<Episode> episodes, string dramaName)
    {
        int first = episodes[0].Number;
        if (first != 1)
            return new MergePlan { Kind = PlanKind.MissingEpisodes, MissingFrom = 1, MissingTo = first - 1 };
        return new MergePlan
        {
            Kind = PlanKind.FullMerge,
            EpisodesToMerge = episodes,
            OutputName = BuildOutputName(dramaName, episodes[0].Number, episodes[^1].Number),
            NewMaxEpisode = episodes[^1].Number,
            TotalBytes = episodes.Sum(e => new FileInfo(e.FilePath).Length),
        };
    }

    /// <summary>
    /// 对比扫描结果和清单，决定行动方案。
    /// </summary>
    public static MergePlan Plan(List<Episode> episodes, MergeManifest? manifest,
        IReadOnlyList<string> srcFolders, string outputDir, string dramaName)
    {
        if (episodes.Count == 0)
            return new MergePlan { Kind = PlanKind.NothingToMerge };

        if (manifest == null || manifest.MergedFiles.Count == 0)
            return PlanFullMerge(episodes, dramaName);

        // 已合并的源文件通常已被删除，增量只按集数判断：清单记录合并到第几集，
        // 比它大的集数按顺序追加到现有合并文件末尾，源文件在不在不参与判断。
        // mergedMax 的计算假设清单从 FirstEpisode 起连续（程序自己写出的清单均满足）。
        int firstEpisode = manifest.FirstEpisode > 0 ? manifest.FirstEpisode : 1;
        int mergedMax = firstEpisode - 1 + manifest.MergedFiles.Count;

        // 追加靠把新集拼到旧合并产物末尾，产物不在了就只能退回全新合并（要求从第 1 集连续）
        if (manifest.OutputFile.Length == 0 || !File.Exists(Path.Combine(outputDir, manifest.OutputFile)))
            return PlanFullMerge(episodes, dramaName);

        var newOnes = episodes.Where(e => e.Number > mergedMax).ToList();

        if (newOnes.Count == 0)
            return new MergePlan { Kind = PlanKind.UpToDate, OutputName = manifest.OutputFile };

        // 新集数必须恰好从 mergedMax+1 开始连续
        if (newOnes[0].Number != mergedMax + 1)
            return new MergePlan
            {
                Kind = PlanKind.MissingEpisodes,
                MissingFrom = mergedMax + 1,
                MissingTo = newOnes[0].Number - 1,
            };

        return new MergePlan
        {
            Kind = PlanKind.IncrementalMerge,
            EpisodesToMerge = newOnes,
            OutputName = BuildOutputName(dramaName, firstEpisode, newOnes[^1].Number),
            NewMaxEpisode = newOnes[^1].Number,
            OldMaxEpisode = mergedMax,
            TotalBytes = new FileInfo(Path.Combine(outputDir, manifest.OutputFile)).Length
                         + newOnes.Sum(e => new FileInfo(e.FilePath).Length),
        };
    }

    /// <summary>用 ffprobe 读取视频时长（秒），失败返回 null。</summary>
    public static double? ProbeDuration(string ffprobeExe, string videoFile)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobeExe,
                Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{videoFile}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            return double.TryParse(output, out var d) && d > 0 ? d : null;
        }
        catch { return null; }
    }

    /// <summary>生成 ffmpeg concat demuxer 所需的清单文件（UTF-8 无 BOM）。</summary>
    public static string WriteConcatList(string outputDir, IReadOnlyList<string> absolutePaths)
    {
        string path = Path.Combine(outputDir, $".concat-{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();
        foreach (var p in absolutePaths)
            sb.AppendLine($"file '{p.Replace("'", @"'\''")}'");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 调 ffmpeg 无损拼接（-c copy）。
    /// 全新合并：orderedPaths = 各集文件；追加合并：orderedPaths = [旧合并文件, 新集...]，
    /// 输出先写临时文件，成功后替换 outputFile。
    /// progress：0~1；输入总时长未知时传 null 表示不定进度。
    /// </summary>
    public static async Task<(bool Ok, string Error)> MergeAsync(
        IReadOnlyList<string> orderedPaths, string outputFile, string ffmpegExe,
        Action<double?>? progress = null, CancellationToken ct = default)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile))!;
        Directory.CreateDirectory(outDir); // 输出目录不存在时自动创建
        string concatList = WriteConcatList(outDir, orderedPaths);
        string tmpOut = Path.Combine(outDir, $".merging-{Guid.NewGuid():N}.mp4");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = $"-hide_banner -loglevel error -nostdin -progress pipe:1 -nostats " +
                            $"-f concat -safe 0 -i \"{concatList}\" -c copy -movflags +faststart -y \"{tmpOut}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var errTask = proc.StandardError.ReadToEndAsync();
            var readProgress = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        // -progress 输出形如 out_time_us=1234567 / out_time_ms=1234567（均为微秒）
                        if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                        {
                            if (double.TryParse(line.AsSpan(line.IndexOf('=') + 1), out var us))
                                progress?.Invoke(us / 1_000_000.0);
                        }
                    }
                }
                catch { /* 进程被杀时流关闭，忽略 */ }
            });

            try
            {
                while (!proc.WaitForExit(200))
                    ct.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                throw;
            }

            await readProgress; // 等进度解析线程自然结束
            string err = (await errTask).Trim();
            if (proc.ExitCode != 0)
                return (false, err.Length > 0 ? err : $"ffmpeg 退出码 {proc.ExitCode}");

            if (File.Exists(outputFile)) File.Delete(outputFile);
            File.Move(tmpOut, outputFile);
            return (true, "");
        }
        finally
        {
            try { File.Delete(concatList); } catch { }
            try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
        }
    }
}

// ---------------- 自检 ----------------

internal static class SelfTest
{
    public static bool Run()
    {
        string? ffmpeg = FindInPath("ffmpeg.exe");
        if (ffmpeg == null)
        {
            Console.Error.WriteLine("selftest requires ffmpeg in PATH");
            return false;
        }
        string? ffprobe = FindInPath("ffprobe.exe");

        string tmp = Path.Combine(Path.GetTempPath(), "DramaMergerTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            string src = Path.Combine(tmp, "src");
            string dst = Path.Combine(tmp, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);

            // 用 ffmpeg 生成 3 个 2 秒小视频模拟剧集，外加 nfo/jpg 干扰文件
            for (int i = 1; i <= 3; i++)
            {
                string f = Path.Combine(src, $"测试剧 第{i:00}集.mp4");
                FfmpegGen(ffmpeg, f, 2, 440 + i * 110);
            }
            File.WriteAllText(Path.Combine(src, "测试剧 第01集.nfo"), "x");
            File.WriteAllText(Path.Combine(src, "poster.jpg"), "x");

            // ---- 集数解析与命名 ----
            Check(Merger.ParseEpisodeNumber("短剧A 第07集") == 7, "第07集 → 7");
            Check(Merger.ParseEpisodeNumber("剧 第123集") == 123, "第123集 → 123");
            Check(Merger.ParseEpisodeNumber("SP 特别篇") == 0, "无集数 → 0");
            Check(Merger.GuessDramaName("短剧A 第01集") == "短剧A", "剧名推断");
            Check(Merger.BuildOutputName("短剧A", 1, 10) == "短剧A 第1-10集.mp4", "命名模板 1-10");
            Check(Merger.BuildOutputName("某剧", 5, 12) == "某剧 第5-12集.mp4", "命名模板 5-12");

            // ---- 扫描 ----
            var eps = Merger.ScanEpisodes(src);
            Check(eps.Count == 3 && eps[0].Number == 1 && eps[2].Number == 3, $"扫描应得 3 集，实际 {eps.Count}");

            // ---- 全新合并计划 + 执行 ----
            var plan = Merger.Plan(eps, null, new[] { src }, dst, "测试剧");
            Check(plan.Kind == PlanKind.FullMerge && plan.EpisodesToMerge.Count == 3, "全新合并计划");
            Check(plan.OutputName == "测试剧 第1-3集.mp4", $"输出名 {plan.OutputName}");
            Check(plan.TotalBytes > 0, "应估算数据量");

            string outFile = Path.Combine(dst, plan.OutputName);
            var r1 = Merger.MergeAsync(plan.EpisodesToMerge.Select(e => e.FilePath).ToList(), outFile, ffmpeg).GetAwaiter().GetResult();
            Check(r1.Ok, "ffmpeg 合并应成功: " + r1.Error);
            Check(new FileInfo(outFile).Length > 10000, "合并产物应有实际内容");

            double? dur = ffprobe == null ? null : Merger.ProbeDuration(ffprobe, outFile);
            Check(dur == null || Math.Abs(dur.Value - 6.0) < 1.0, $"合并后时长应约 6 秒，实际 {dur}");

            // ---- 清单 ----
            var manifest = new MergeManifest
            {
                DramaName = "测试剧",
                MergedFiles = plan.EpisodesToMerge.Select(e => Path.GetFileName(e.FilePath)).ToList(),
                MergedSizes = plan.EpisodesToMerge.Select(e => new FileInfo(e.FilePath).Length.ToString()).ToList(),
                OutputFile = plan.OutputName,
                FirstEpisode = plan.EpisodesToMerge[0].Number,
            };
            Merger.SaveManifest(dst, manifest);
            var loaded = Merger.LoadManifest(dst, "测试剧");
            Check(loaded != null && loaded.MergedCount == 3, "清单保存/加载");

            // ---- 无新集：UpToDate ----
            Check(Merger.Plan(Merger.ScanEpisodes(src), loaded, new[] { src }, dst, "测试剧").Kind == PlanKind.UpToDate, "无新集应 UpToDate");

            // ---- 加入第4集：增量计划 + 执行（旧产物 + 新集）----
            string ep4 = Path.Combine(src, "测试剧 第04集.mp4");
            FfmpegGen(ffmpeg, ep4, 2, 1000);
            var eps4 = Merger.ScanEpisodes(src);
            var plan3 = Merger.Plan(eps4, loaded, new[] { src }, dst, "测试剧");
            Check(plan3.Kind == PlanKind.IncrementalMerge && plan3.EpisodesToMerge.Count == 1 && plan3.NewMaxEpisode == 4, "增量计划应识别第4集");
            Check(plan3.OutputName == "测试剧 第1-4集.mp4", $"增量输出名 {plan3.OutputName}");

            var paths = new List<string> { Path.Combine(dst, loaded!.OutputFile) };
            paths.AddRange(plan3.EpisodesToMerge.Select(e => e.FilePath));
            string oldOut = Path.Combine(dst, loaded.OutputFile);
            outFile = Path.Combine(dst, plan3.OutputName); // 增量合并后输出名变为 第1-4集
            var r2 = Merger.MergeAsync(paths, outFile, ffmpeg).GetAwaiter().GetResult();
            Check(r2.Ok, "增量合并应成功: " + r2.Error);
            File.Delete(oldOut); // 模拟 GUI 在增量合并成功后删除旧产物
            Check(Path.GetFileName(outFile) == "测试剧 第1-4集.mp4", "文件名应保持正确");
            Check(!File.Exists(oldOut), "旧合并文件应被删除");
            Check(Merger.ParseEpisodeNumber(Path.GetFileNameWithoutExtension(outFile)) == 0, "合并文件名不应再被识别为单集");

            dur = ffprobe == null ? null : Merger.ProbeDuration(ffprobe, outFile);
            Check(dur == null || Math.Abs(dur.Value - 8.0) < 1.0, $"追加后时长应约 8 秒，实际 {dur}");

            manifest.MergedFiles.Add(Path.GetFileName(ep4));
            manifest.MergedSizes.Add(new FileInfo(ep4).Length.ToString());
            manifest.OutputFile = plan3.OutputName;
            Merger.SaveManifest(dst, manifest);
            Check(Merger.Plan(Merger.ScanEpisodes(src), Merger.LoadManifest(dst, "测试剧"), new[] { src }, dst, "测试剧").Kind == PlanKind.UpToDate, "更新清单后应 UpToDate");

            // ---- 缺集检测：跳过第5集直接放第6集（用更新后的清单）----
            string ep6 = Path.Combine(src, "测试剧 第06集.mp4");
            FfmpegGen(ffmpeg, ep6, 1, 1200);
            loaded = Merger.LoadManifest(dst, "测试剧");
            var plan4 = Merger.Plan(Merger.ScanEpisodes(src), loaded, new[] { src }, dst, "测试剧");
            Check(plan4.Kind == PlanKind.MissingEpisodes && plan4.MissingFrom == 5 && plan4.MissingTo == 5,
                $"应检测到缺第5集，实际 Kind={plan4.Kind} From={plan4.MissingFrom} To={plan4.MissingTo} Eps=[{string.Join(',', Merger.ScanEpisodes(src).Select(e => e.Number))}] Merged={loaded!.MergedCount}");

            // ---- 旧合并产物丢失：无法追加，退回全新合并（源仍从第 1 集连续）----
            File.Delete(Path.Combine(dst, loaded!.OutputFile));
            Check(Merger.Plan(Merger.ScanEpisodes(src), loaded, new[] { src }, dst, "测试剧").Kind == PlanKind.FullMerge, "合并产物丢失应退回全新合并");

            // ---- 批量模式：两部剧各一个文件夹，一次合并到统一输出目录 ----
            string batchRoot = Path.Combine(tmp, "batch");
            string outRoot = Path.Combine(tmp, "batch_out");
            string dramaA = Path.Combine(batchRoot, "剧A 第一部");
            string dramaB = Path.Combine(batchRoot, "剧B 番外");
            Directory.CreateDirectory(dramaA);
            Directory.CreateDirectory(dramaB);
            for (int i = 1; i <= 2; i++)
                FfmpegGen(ffmpeg, Path.Combine(dramaA, $"剧A 第一部 第{i:00}集.mp4"), 1, 1300 + i * 60);
            for (int i = 1; i <= 3; i++)
                FfmpegGen(ffmpeg, Path.Combine(dramaB, $"剧B 番外 第{i:00}集.mp4"), 1, 1500 + i * 60);

            var folders = Merger.ListDramaFolders(batchRoot);
            Check(folders.Count == 2, $"批量应发现 2 个剧集文件夹，实际 {folders.Count}");

            var planA = Merger.PlanDrama(new[] { folders[0] }, outRoot);
            Check(planA.DramaName == "剧A 第一部", $"剧名推断：{planA.DramaName}");
            Check(planA.Plan.Kind == PlanKind.FullMerge && planA.Plan.OutputName == "剧A 第一部 第1-2集.mp4", $"剧A 计划 {planA.Plan.OutputName}");
            var planB = Merger.PlanDrama(new[] { folders[1] }, outRoot);
            Check(planB.DramaName == "剧B 番外" && planB.Plan.OutputName == "剧B 番外 第1-3集.mp4", $"剧B 计划 {planB.Plan.OutputName}");

            foreach (var dp in new[] { planA, planB })
            {
                string outPath = Path.Combine(outRoot, dp.Plan.OutputName);
                var r = Merger.MergeAsync(dp.Plan.EpisodesToMerge.Select(e => e.FilePath).ToList(), outPath, ffmpeg).GetAwaiter().GetResult();
                Check(r.Ok, $"{dp.DramaName} 批量合并应成功: {r.Error}");
                var mf = new MergeManifest { DramaName = dp.DramaName };
                Merger.UpdateManifestAfterMerge(mf, dp.Plan);
                Merger.SaveManifest(outRoot, mf);
            }
            Check(Directory.GetFiles(outRoot, "*.mp4").Length == 2, "批量输出应有 2 个合并文件");
            var mfB = Merger.LoadManifest(outRoot, "剧B 番外");
            Check(mfB != null && mfB.FirstEpisode == 1 && mfB.MergedCount == 3, "剧B 清单应记录首集=1、共3集");

            // 批量 + 增量：剧A 加入第3集，再跑一次应只追加剧A
            FfmpegGen(ffmpeg, Path.Combine(dramaA, "剧A 第一部 第03集.mp4"), 1, 1550);
            var planA2 = Merger.PlanDrama(new[] { dramaA }, outRoot);
            Check(planA2.Plan.Kind == PlanKind.IncrementalMerge && planA2.Plan.OutputName == "剧A 第一部 第1-3集.mp4",
                $"剧A 增量计划 {planA2.Plan.OutputName}");

            // ---- 多文件夹合并：旧剧文件夹 + 新下载文件夹 ----
            string oldFold = Path.Combine(tmp, "old_eps");
            string newFold = Path.Combine(tmp, "new_eps");
            Directory.CreateDirectory(oldFold);
            Directory.CreateDirectory(newFold);
            for (int i = 1; i <= 3; i++)
                FfmpegGen(ffmpeg, Path.Combine(oldFold, $"多夹剧 第{i:00}集.mp4"), 1, 1700 + i * 60);
            for (int i = 4; i <= 5; i++)
                FfmpegGen(ffmpeg, Path.Combine(newFold, $"多夹剧 第{i:00}集.mp4"), 1, 1900 + i * 60);

            string multiOut = Path.Combine(tmp, "multi_out");
            var multi = Merger.PlanDrama(new[] { oldFold, newFold }, multiOut);
            Check(multi.DramaName == "多夹剧", $"跨文件夹剧名推断：{multi.DramaName}");
            Check(multi.Plan.Kind == PlanKind.FullMerge && multi.Plan.EpisodesToMerge.Count == 5, "跨文件夹应合并扫描 5 集");
            Check(multi.Plan.OutputName == "多夹剧 第1-5集.mp4", $"跨文件夹输出名 {multi.Plan.OutputName}");
            Check(multi.Plan.EpisodesToMerge[0].FilePath.Contains("old_eps") &&
                  multi.Plan.EpisodesToMerge[4].FilePath.Contains("new_eps"), "集数顺序应跨越文件夹");

            string multiOutFile = Path.Combine(multiOut, multi.Plan.OutputName);
            var r3 = Merger.MergeAsync(multi.Plan.EpisodesToMerge.Select(e => e.FilePath).ToList(), multiOutFile, ffmpeg).GetAwaiter().GetResult();
            Check(r3.Ok, "跨文件夹合并应成功: " + r3.Error);
            var mfMulti = new MergeManifest { DramaName = "多夹剧" };
            Merger.UpdateManifestAfterMerge(mfMulti, multi.Plan);
            Merger.SaveManifest(multiOut, mfMulti);

            // 第二天新剧集下载到另一个新文件夹：勾选 [old_eps, new_eps, new_eps2] 追加第6集
            string newFold2 = Path.Combine(tmp, "new_eps2");
            Directory.CreateDirectory(newFold2);
            FfmpegGen(ffmpeg, Path.Combine(newFold2, "多夹剧 第06集.mp4"), 1, 2100);
            var multi2 = Merger.PlanDrama(new[] { oldFold, newFold, newFold2 }, multiOut);
            Check(multi2.Plan.Kind == PlanKind.IncrementalMerge && multi2.Plan.NewMaxEpisode == 6, "跨文件夹增量应识别第6集");
            Check(multi2.Plan.OutputName == "多夹剧 第1-6集.mp4", $"增量输出名 {multi2.Plan.OutputName}");

            // ---- 已合并的源文件被删除：不阻断增量，照常按集数追加 ----
            foreach (var f in Directory.GetFiles(oldFold)) File.Delete(f);
            foreach (var f in Directory.GetFiles(newFold)) File.Delete(f);
            var multi3 = Merger.PlanDrama(new[] { oldFold, newFold, newFold2 }, multiOut);
            Check(multi3.Plan.Kind == PlanKind.IncrementalMerge && multi3.Plan.NewMaxEpisode == 6,
                $"源文件被删后仍应追加第6集，实际 Kind={multi3.Plan.Kind}");
            Check(multi3.Plan.OldMaxEpisode == 5 && multi3.Plan.EpisodesToMerge.Count == 1, "源文件被删后增量范围应为第6集");

            Console.WriteLine("selftest OK");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selftest FAILED: " + ex);
            return false;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    static void FfmpegGen(string ffmpeg, string file, int seconds, int freq)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -f lavfi -i testsrc=duration={seconds}:size=320x240:rate=10 " +
                        $"-f lavfi -i sine=frequency={freq}:duration={seconds} -shortest -pix_fmt yuv420p -y \"{file}\"",
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(60000);
        Check(p.ExitCode == 0, $"生成测试视频失败: {file}");
    }

    static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static void Check(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }
}

// ---------------- GUI ----------------

public class MainForm : Form
{
    readonly TextBox _dstBox, _nameBox, _logBox;
    readonly CheckedListBox _srcList;
    readonly Button _btn, _cancelBtn;
    readonly CheckBox _batch;
    readonly ProgressBar _progress;
    readonly ComboBox _langBox;
    readonly List<(Label Label, Button Browse)> _pathRows = new();
    readonly Label _dstLabel;
    Label _srcLabel = null!, _nameLabel = null!, _nameHint = null!, _status = null!;
    Button _addBtn = null!, _removeBtn = null!, _clearBtn = null!;
    CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = L.AppTitle;
        MinimumSize = new System.Drawing.Size(700, 720);
        Size = new System.Drawing.Size(780, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 14),
            ColumnCount = 3,
            RowCount = 9,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));        // 0 语言选择
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 1 说明行
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 2 添加文件夹按钮行
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 40));        // 3 剧集文件夹列表
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));       // 4 输出文件夹
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));       // 5 剧名
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));       // 6 按钮
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));       // 7 进度条
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));       // 8 状态文字
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 60));        // 9 日志
        Controls.Add(table);

        // 语言选择行
        var langRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        langRow.Controls.Add(new Label { Text = "语言 / Language:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        _langBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Items = { "中文", "English" },
            SelectedIndex = L.Current == AppLang.Zh ? 0 : 1,
        };
        _langBox.SelectedIndexChanged += (_, _) =>
        {
            L.Current = _langBox.SelectedIndex == 1 ? AppLang.En : AppLang.Zh;
            ApplyLanguage();
        };
        langRow.Controls.Add(_langBox);
        table.Controls.Add(langRow, 0, 0);
        table.SetColumnSpan(langRow, 3);

        // 剧集文件夹列表（可勾选多个）
        _srcLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        table.Controls.Add(_srcLabel, 0, 1);
        table.SetColumnSpan(_srcLabel, 3);

        _srcList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        table.Controls.Add(_srcList, 0, 3);
        table.SetColumnSpan(_srcList, 3);

        var srcBtnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 6) };
        _addBtn = new Button { AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _addBtn.Click += (_, _) => AddSrcFolder();
        _removeBtn = new Button { AutoSize = true, Margin = new Padding(0) };
        _removeBtn.Click += (_, _) =>
        {
            var items = new List<string>();
            foreach (var o in _srcList.SelectedItems)
            {
                string? s = o?.ToString();
                if (s != null) items.Add(s);
            }
            foreach (var it in items) _srcList.Items.Remove(it);
        };
        _clearBtn = new Button { AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _clearBtn.Click += (_, _) => _srcList.Items.Clear();
        srcBtnRow.Controls.Add(_addBtn);
        srcBtnRow.Controls.Add(_removeBtn);
        srcBtnRow.Controls.Add(_clearBtn);
        table.Controls.Add(srcBtnRow, 0, 2);
        table.SetColumnSpan(srcBtnRow, 3);

        // 输出文件夹行
        _dstLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
        table.Controls.Add(_dstLabel, 0, 4);
        _dstBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 8, 4) };
        table.Controls.Add(_dstBox, 1, 4);
        var dstBrowse = new Button { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        dstBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { ShowNewFolderButton = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) _dstBox.Text = dlg.SelectedPath;
        };
        _pathRows.Add((_dstLabel, dstBrowse));
        table.Controls.Add(dstBrowse, 2, 4);

        // 剧名行
        _nameLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
        table.Controls.Add(_nameLabel, 0, 5);
        _nameBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 8, 4) };
        table.Controls.Add(_nameBox, 1, 5);
        _nameHint = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = System.Drawing.Color.Gray,
            Margin = new Padding(10, 0, 0, 0),
        };
        table.Controls.Add(_nameHint, 2, 5);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        _batch = new CheckBox { AutoSize = true, Margin = new Padding(0, 9, 16, 0) };
        _batch.CheckedChanged += (_, _) => { _nameBox.Enabled = !_batch.Checked; _srcList.Enabled = !_batch.Checked; };
        _btn = new Button { Height = 34, Width = 140, Margin = new Padding(0, 6, 12, 0) };
        _btn.Click += (_, _) => _ = RunAsync();
        _cancelBtn = new Button { Height = 34, Width = 90, Enabled = false, Margin = new Padding(0, 6, 0, 0) };
        _cancelBtn.Click += (_, _) => _cts?.Cancel();
        btnRow.Controls.Add(_batch);
        btnRow.Controls.Add(_btn);
        btnRow.Controls.Add(_cancelBtn);
        table.Controls.Add(btnRow, 0, 6);
        table.SetColumnSpan(btnRow, 3);

        _progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), Maximum = 100 };
        table.Controls.Add(_progress, 0, 7);
        table.SetColumnSpan(_progress, 3);

        _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 2, 0, 0) };
        table.Controls.Add(_status, 0, 8);
        table.SetColumnSpan(_status, 3);

        _logBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0),
            Font = new System.Drawing.Font("Consolas", 9F),
        };
        table.Controls.Add(_logBox, 0, 9);
        table.SetColumnSpan(_logBox, 3);

        ApplyLanguage();
    }

    /// <summary>语言切换时刷新所有界面文字。</summary>
    void ApplyLanguage()
    {
        Text = L.AppTitle;
        _srcLabel.Text = L.SrcListLabel;
        _addBtn.Text = L.AddFolder;
        _removeBtn.Text = L.RemoveSelected;
        _clearBtn.Text = L.ClearList;
        foreach (var (label, browse) in _pathRows)
        {
            label.Text = L.OutputFolder;
            browse.Text = L.Browse;
        }
        _nameLabel.Text = L.DramaName;
        _nameHint.Text = L.NameHint;
        _batch.Text = L.BatchMode;
        _btn.Text = L.ScanMerge;
        _cancelBtn.Text = L.Cancel;
        if (!_btn.Enabled) _status.Text = L.DefaultStatus; // 空闲时才重置默认状态文字
    }

    void AddSrcFolder()
    {
        using var dlg = new FolderBrowserDialog { ShowNewFolderButton = false, Description = L.DlgPickFolder };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string path = dlg.SelectedPath;
        if (_srcList.Items.Cast<string>().Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
        { Warn(L.DlgAlreadyInList); return; }
        _srcList.Items.Add(path, true); // 默认勾选
        if (_dstBox.Text.Length == 0) _dstBox.Text = path;
    }

    async Task RunAsync()
    {
        var folders = _srcList.CheckedItems.Cast<string>().ToList();
        string dst = _dstBox.Text.Trim().TrimEnd('"');
        string dramaName = _nameBox.Text.Trim();
        bool batch = _batch.Checked;

        if (folders.Count == 0) { Warn(L.MsgNoFolders); return; }
        foreach (var f in folders)
            if (!Directory.Exists(f)) { Warn(L.MsgFolderMissing(f)); return; }
        if (dst.Length == 0) { Warn(L.MsgNoOutput); return; }
        if (!Directory.Exists(dst)) { try { Directory.CreateDirectory(dst); } catch (Exception ex) { Error(L.MsgMkDirFailed(ex.Message)); return; } }

        string? ffmpeg = FindInPath("ffmpeg.exe");
        if (ffmpeg == null) { Error(L.MsgNoFfmpeg); return; }
        string? ffprobe = FindInPath("ffprobe.exe");

        _btn.Enabled = false;
        _cancelBtn.Enabled = true;
        _logBox.Clear();
        _progress.Value = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // 1. 扫描 + 计划
            SetStatus(L.MsgScanning);
            var plans = batch
                ? await Task.Run(() => folders
                        .SelectMany(root => Merger.ListDramaFolders(root))
                        .Select(f => Merger.PlanDrama(new[] { f }, dst)).ToList(), ct)
                : new List<Merger.DramaPlan> { await Task.Run(() => Merger.PlanDrama(folders, dst, dramaName.Length > 0 ? dramaName : null), ct) };

            if (!batch && dramaName.Length == 0) _nameBox.Text = plans[0].DramaName;

            Log(batch
                ? L.LogBatchFound(folders.Count, plans.Count)
                : L.LogSingleFound(folders.Count, plans[0].DramaName));
            foreach (var dp in plans)
                Log(L.LogPlanLine(dp));

            var actionable = plans.Where(p => p.Plan.Kind is PlanKind.FullMerge or PlanKind.IncrementalMerge).ToList();
            if (actionable.Count == 0)
            {
                Info(L.MsgNothingToMerge(plans.Any(p => p.Plan.Kind == PlanKind.UpToDate)));
                return;
            }

            // 2. 确认（列出将要产出的文件）
            long totalBytes = actionable.Sum(p => p.Plan.TotalBytes);
            string confirmMsg = L.MsgConfirm(actionable.Count, FmtSize(totalBytes),
                string.Join("\n", actionable.Select(p => $"• {p.Plan.OutputName}")));
            if (MessageBox.Show(this, confirmMsg, L.DlgConfirmTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            { Log(L.LogCanceled); return; }

            // 3. 逐部执行
            int done = 0, failed = 0;
            for (int i = 0; i < actionable.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dp = actionable[i];
                SetStatus(L.StatusMerging(i + 1, actionable.Count, dp.DramaName));
                var (ok, err) = await MergeOneDrama(dp, dst, ffmpeg, ffprobe, i, actionable.Count, ct);
                if (ok) done++; else { failed++; Log(L.LogFail(dp.DramaName, err)); }
            }

            _progress.Value = 100;
            SetStatus(L.StatusDone(done, failed, actionable.Count));
            if (failed == 0)
                Info(L.MsgDone(done, failed, actionable.Count, batch, dst, actionable[0].Plan.OutputName));
            else
                Warn(L.MsgPartialFail(done, failed));
        }
        catch (OperationCanceledException)
        {
            SetStatus(L.StatusCanceled);
            Log(L.LogCanceled);
        }
        catch (Exception ex)
        {
            Error(L.MsgError(ex.Message));
            SetStatus(L.StatusError);
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks; // 若处于 Marquee（未知时长）状态，恢复常规样式
            _btn.Enabled = true;
            _cancelBtn.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>合并一部剧：执行 ffmpeg、删除旧产物、更新清单。返回 (成功, 错误信息)。</summary>
    async Task<(bool Ok, string Error)> MergeOneDrama(
        Merger.DramaPlan dp, string dst, string ffmpeg, string? ffprobe,
        int dramaIndex, int dramaCount, CancellationToken ct)
    {
        var plan = dp.Plan;
        var manifest = dp.Manifest;
        string oldOutput = manifest?.OutputFile is { Length: > 0 } of ? Path.Combine(dst, of) : "";
        string outPath = Path.Combine(dst, plan.OutputName);

        var ordered = plan.Kind == PlanKind.IncrementalMerge
            ? new List<string> { oldOutput }.Concat(plan.EpisodesToMerge.Select(e => e.FilePath)).ToList()
            : plan.EpisodesToMerge.Select(e => e.FilePath).ToList();

        Log(L.LogStartMerge(dp.DramaName, ordered.Count, plan.OutputName));

        // 逐集读取时长：既用于总进度估算，也用于实时显示当前正在拼接哪个文件。
        // ffprobe 探测（尤其走 NAS）较慢，放后台线程并逐个刷新状态，避免界面无响应。
        var durations = new double[ordered.Count];
        SetStatus(L.StatusProbingStart);
        await Task.Run(() =>
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                durations[i] = ffprobe == null ? 0 : Merger.ProbeDuration(ffprobe, ordered[i]) ?? 0;
                int idx = i; // BeginInvoke 异步执行，必须捕获快照，否则闭包读到自增后的 i 会越界
                BeginInvoke(() =>
                {
                    if (ct.IsCancellationRequested) return; // 取消后不再覆盖“已取消”状态
                    SetStatus(L.StatusProbing(idx + 1, ordered.Count, Path.GetFileName(ordered[idx])));
                });
            }
        }, ct);
        double totalSeconds = durations.Sum();
        var cumEnd = new double[ordered.Count];
        for (int i = 0; i < ordered.Count; i++) cumEnd[i] = (i > 0 ? cumEnd[i - 1] : 0) + durations[i];
        bool knowDuration = totalSeconds > 0;
        BeginInvoke(() =>
        {
            _progress.Style = knowDuration ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 40;
        });

        var (ok, err) = await Merger.MergeAsync(ordered, outPath, ffmpeg, seconds =>
        {
            if (!seconds.HasValue) return;
            BeginInvoke(() =>
            {
                if (ct.IsCancellationRequested) return; // 取消后不再覆盖“已取消”状态
                if (!knowDuration) return; // 无时长信息时无法定位当前文件，靠跑马灯表示进行中，不刷新状态
                double t = Math.Max(0, seconds.Value);
                // 由已处理时长定位当前拼到的文件（concat 按顺序处理）
                int fi = Array.FindIndex(cumEnd, c => t < c);
                if (fi < 0) fi = ordered.Count - 1;
                _progress.Value = Math.Min(
                    (int)((dramaIndex + Math.Min(t, totalSeconds) / totalSeconds) / dramaCount * 100), 100);
                SetStatus(L.StatusMergingFile(dramaIndex + 1, dramaCount, dp.DramaName,
                    fi + 1, ordered.Count, Path.GetFileName(ordered[fi]), FmtTime(t), FmtTime(totalSeconds)));
            });
        }, ct);
        if (!ok) return (false, err.Length > 0 ? err : L.MsgFfmpegExit(plan.OutputName));

        // 增量合并成功后，删除旧名字的合并产物（避免新旧文件内容重复）
        if (plan.Kind == PlanKind.IncrementalMerge && oldOutput.Length > 0 &&
            !string.Equals(oldOutput, outPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldOutput))
        {
            File.Delete(oldOutput);
            Log(L.LogDeletedOld(dp.DramaName, Path.GetFileName(oldOutput)));
        }

        var newManifest = manifest ?? new MergeManifest { DramaName = dp.DramaName };
        Merger.UpdateManifestAfterMerge(newManifest, plan);
        await Task.Run(() => Merger.SaveManifest(dst, newManifest), ct);
        Log(L.LogManifestSaved(dp.DramaName, newManifest.MergedCount));
        return (true, "");
    }

    static string FmtSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F2} GB" : $"{bytes / 1048576.0:F1} MB";

    /// <summary>秒数格式化为 MM:SS / H:MM:SS。</summary>
    static string FmtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    void Log(string line) => _logBox.AppendText(line + Environment.NewLine);
    void SetStatus(string s) { if (_status != null) _status.Text = s; }
    void Warn(string msg) => MessageBox.Show(this, msg, L.DlgWarnTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    void Info(string msg) => MessageBox.Show(this, msg, L.DlgInfoTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    void Error(string msg) => MessageBox.Show(this, msg, L.DlgErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
