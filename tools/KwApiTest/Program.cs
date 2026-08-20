using CatClawMusic.Plugins.LxSource;

// 实测酷我 API：搜索 + 榜单 + 封面（验证 C# 解析逻辑与真实响应匹配）
Console.WriteLine("=== 酷我 API 实测 ===");

// 1. 搜索
var songs = await LxKuwoApi.SearchAsync("周杰伦", 1, 5);
Console.WriteLine($"[搜索] 返回 {songs?.Count ?? -1} 首");
if (songs is { Count: > 0 })
{
    var s0 = songs[0];
    Console.WriteLine($"  第一首: {s0.Title} / {s0.Artist} / {s0.Album} / {s0.DurationMs / 1000}s / id={s0.Id}");
}

// 2. 榜单列表
var boards = LxKuwoApi.GetBoards();
Console.WriteLine($"[榜单] 共 {boards.Count} 个，前 5: {string.Join(", ", boards.Take(5).Select(b => b.Name))}");

// 3. 榜单歌曲（热歌榜 id=16）
var boardSongs = await LxKuwoApi.GetBoardSongsAsync("16", 1, 5);
Console.WriteLine($"[榜单歌曲] 返回 {boardSongs?.Count ?? -1} 首");
if (boardSongs is { Count: > 0 })
{
    var b0 = boardSongs[0];
    Console.WriteLine($"  第一首: {b0.Title} / {b0.Artist} / {b0.Album} / {b0.DurationMs / 1000}s / cover={(b0.CoverUrl ?? "")[..Math.Min(60, (b0.CoverUrl ?? "").Length)]}");
}

// 4. 封面（用第一首榜单歌曲 id）
if (boardSongs is { Count: > 0 })
{
    var songId = boardSongs[0].Id.Split(':')[1];
    var pic = await LxKuwoApi.GetPicAsync(songId);
    Console.WriteLine($"[封面] id={songId} → {(string.IsNullOrEmpty(pic) ? "null" : pic[..Math.Min(80, pic.Length)])}");
}

Console.WriteLine("=== 完成 ===");