using System.Collections.Generic;

namespace DramaMerger;

/// <summary>界面语言。切换后调用 MainForm.ApplyLanguage() 生效。</summary>
public enum AppLang { Zh, En }

/// <summary>
/// 中英双语文案表。静态字符串 + 动态消息用带参数的方法；切换语言时整体替换。
/// </summary>
internal static class L
{
    public static AppLang Current { get; set; } = AppLang.Zh;

    public static bool IsZh => Current == AppLang.Zh;

    // ---- 窗口与控件 ----
    public static string AppTitle => IsZh ? "短剧合并工具" : "Drama Merger";
    public static string SrcListLabel => IsZh
        ? "剧集文件夹（勾选参与合并的，可同时勾选旧剧集和新下载的文件夹）："
        : "Episode folders (check the ones to merge; old episodes and newly downloaded ones can be checked together):";
    public static string AddFolder => IsZh ? "添加文件夹…" : "Add folder…";
    public static string RemoveSelected => IsZh ? "移除选中" : "Remove selected";
    public static string ClearList => IsZh ? "清空" : "Clear";
    public static string OutputFolder => IsZh ? "输出文件夹：" : "Output folder:";
    public static string DramaName => IsZh ? "剧　名：" : "Drama name:";
    public static string NameHint => IsZh ? "留空自动识别" : "Auto-detected if empty";
    public static string BatchMode => IsZh ? "批量模式（每个子文件夹 = 一部剧）" : "Batch mode (each subfolder = one drama)";
    public static string ScanMerge => IsZh ? "扫描并合并" : "Scan && Merge";
    public static string Cancel => IsZh ? "取消" : "Cancel";
    public static string DefaultStatus => IsZh
        ? "添加剧集文件夹（勾选）后点击「扫描并合并」"
        : "Add episode folders (checked), then click \"Scan && Merge\".";
    public static string Browse => IsZh ? "浏览…" : "Browse…";

    // ---- 对话框 ----
    public static string DlgPickFolder => IsZh ? "选择一个剧集文件夹（可多次添加）" : "Pick an episode folder (add as many as you like)";
    public static string DlgAlreadyInList => IsZh ? "该文件夹已在列表中。" : "That folder is already in the list.";
    public static string DlgWarnTitle => IsZh ? "提示" : "Warning";
    public static string DlgInfoTitle => IsZh ? "信息" : "Info";
    public static string DlgErrorTitle => IsZh ? "错误" : "Error";
    public static string DlgConfirmTitle => IsZh ? "确认合并" : "Confirm merge";

    // ---- 动态消息（带参数） ----
    public static string MsgNoFolders => IsZh ? "请先添加并勾选至少一个剧集文件夹" : "Add and check at least one episode folder first.";
    public static string MsgFolderMissing(string f) => IsZh ? $"文件夹不存在，请移除后重新添加：\n{f}" : $"Folder does not exist. Remove it and re-add:\n{f}";
    public static string MsgNoOutput => IsZh ? "请选择输出文件夹" : "Please choose an output folder.";
    public static string MsgMkDirFailed(string e) => IsZh ? $"创建输出文件夹失败：{e}" : $"Failed to create the output folder: {e}";
    public static string MsgNoFfmpeg => IsZh
        ? "未找到 ffmpeg。\n请安装 ffmpeg 并将其所在目录加入 PATH 环境变量。"
        : "ffmpeg not found.\nInstall ffmpeg and add its folder to the PATH environment variable.";
    public static string MsgScanning => IsZh ? "正在扫描剧集…" : "Scanning episodes…";
    public static string LogBatchFound(int roots, int dramas) => IsZh
        ? $"批量扫描：在 {roots} 个根文件夹下发现 {dramas} 部剧"
        : $"Batch scan: found {dramas} drama(s) under {roots} root folder(s).";
    public static string LogSingleFound(int folders, string name) => IsZh
        ? $"已勾选 {folders} 个剧集文件夹，识别出剧集：《{name}》"
        : $"{folders} folder(s) checked; drama detected: \"{name}\"";
    public static string LogPlanLine(Merger.DramaPlan dp) => dp.Plan.Kind switch
    {
        PlanKind.FullMerge => IsZh
            ? $"  待合并：{dp.DramaName}（全新，{dp.Plan.EpisodesToMerge.Count} 集）→ {dp.Plan.OutputName}"
            : $"  To merge: {dp.DramaName} (full, {dp.Plan.EpisodesToMerge.Count} ep) -> {dp.Plan.OutputName}",
        PlanKind.IncrementalMerge => IsZh
            ? $"  待合并：{dp.DramaName}（追加第 {dp.Plan.OldMaxEpisode + 1}-{dp.Plan.NewMaxEpisode} 集）→ {dp.Plan.OutputName}"
            : $"  To merge: {dp.DramaName} (append ep {dp.Plan.OldMaxEpisode + 1}-{dp.Plan.NewMaxEpisode}) -> {dp.Plan.OutputName}",
        PlanKind.UpToDate => IsZh
            ? $"  已是最新：{dp.DramaName}（已合并 {dp.Manifest!.MergedCount} 集）"
            : $"  Up to date: {dp.DramaName} ({dp.Manifest!.MergedCount} ep already merged)",
        PlanKind.MissingEpisodes => IsZh
            ? $"  缺集：{dp.DramaName}（缺第 {dp.Plan.MissingFrom}-{dp.Plan.MissingTo} 集）"
            : $"  Missing episodes: {dp.DramaName} (missing ep {dp.Plan.MissingFrom}-{dp.Plan.MissingTo})",
        _ => IsZh ? $"  无剧集：{dp.DramaName}" : $"  No episodes: {dp.DramaName}",
    };
    public static string MsgNothingToMerge(bool anyUpToDate) => IsZh
        ? anyUpToDate ? "所有剧集都已是最新，没有需要合并的内容。" : "没有可合并的剧集（详情见日志）。"
        : anyUpToDate ? "Everything is up to date; nothing to merge." : "Nothing to merge (see the log for details).";
    public static string MsgConfirm(long count, string size, string items) => IsZh
        ? $"将合并 {count} 部剧，数据量约 {size}：\n\n{items}\n\n是否继续？"
        : $"Merge {count} drama(s), about {size} in total:\n\n{items}\n\nContinue?";
    public static string StatusMerging(int i, int n, string name) => IsZh
        ? $"({i}/{n}) 正在合并：{name}…"
        : $"({i}/{n}) Merging: {name}…";
    public static string StatusProbingStart => IsZh
        ? "正在读取各集时长（用于进度估算）…"
        : "Reading episode durations (for progress estimation)…";
    public static string StatusProbing(int i, int n, string file) => IsZh
        ? $"正在读取时长（{i}/{n}）：{file}"
        : $"Reading durations ({i}/{n}): {file}";
    public static string StatusMergingFile(int d, int n, string drama, int fi, int fc, string file, string done, string total) => IsZh
        ? $"({d}/{n})《{drama}》正在拼接 {fi}/{fc}：{file} · 已处理 {done} / 共 {total}"
        : $"({d}/{n}) \"{drama}\" concatenating {fi}/{fc}: {file} · {done} / {total}";
    public static string StatusMergingFileNoTime(int d, int n, string drama, int fi, int fc, string file) => IsZh
        ? $"({d}/{n})《{drama}》正在拼接 {fi}/{fc}：{file}"
        : $"({d}/{n}) \"{drama}\" concatenating {fi}/{fc}: {file}";
    public static string LogFail(string name, string err) => IsZh ? $"✗ {name} 合并失败：{err}" : $"✗ {name} merge failed: {err}";
    public static string MsgDone(int ok, int failed, int total, bool batch, string dst, string firstOutput) => IsZh
        ? batch
            ? $"批量合并完成！成功 {ok} 部。\n\n输出目录：{dst}"
            : $"合并完成！\n{firstOutput}"
        : batch
            ? $"Batch merge finished! {ok} drama(s) merged.\n\nOutput folder: {dst}"
            : $"Merge finished!\n{firstOutput}";
    public static string MsgPartialFail(int ok, int failed) => IsZh
        ? $"成功 {ok} 部，失败 {failed} 部，失败原因见日志。"
        : $"{ok} succeeded, {failed} failed. See the log for failure details.";
    public static string StatusDone(int ok, int failed, int total) => IsZh
        ? failed > 0 ? $"完成：成功 {ok} 部，失败 {failed} 部" : $"完成：成功 {ok} 部"
        : failed > 0 ? $"Done: {ok} succeeded, {failed} failed" : $"Done: {ok} succeeded";
    public static string StatusCanceled => IsZh ? "已取消" : "Canceled";
    public static string LogCanceled => IsZh
        ? "合并已取消（可能留下未完成的临时文件，已自动清理）。"
        : "Merge canceled (temporary files may remain; they are cleaned up automatically).";
    public static string MsgError(string e) => IsZh ? "出错：" + e : "Error: " + e;
    public static string StatusError => IsZh ? "出错" : "Error";

    public static string LogStartMerge(string name, int files, string output) => IsZh
        ? $"《{name}》合并 {files} 个文件 → {output}"
        : $"\"{name}\": merging {files} file(s) -> {output}";
    public static string LogDeletedOld(string name, string oldFile) => IsZh
        ? $"《{name}》已删除旧合并文件：{oldFile}"
        : $"\"{name}\": removed old merged file {oldFile}";
    public static string LogManifestSaved(string name, int count) => IsZh
        ? $"《{name}》完成（已合并到第 {count} 集，清单已更新）"
        : $"\"{name}\" done ({count} episode(s) merged, manifest updated)";
    public static string MsgFfmpegFailed(string err) => IsZh ? "ffmpeg 合并失败：\n" + err : "ffmpeg merge failed:\n" + err;
    public static string MsgFfmpegExit(string output) => IsZh
        ? $"ffmpeg 退出码异常（合并 {output}）"
        : $"ffmpeg exited with an error (merging {output})";
}
