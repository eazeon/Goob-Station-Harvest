using System.Text.Json;
using Content.Shared._Harvest.Arcade;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Harvest.Arcade;

/// <summary>
/// High scores of one Harvest arcade game, separate for each of its modes: one list for the current round
/// and one kept between rounds in its own file of the server's data folder.
/// </summary>
public sealed class ArcadeLeaderboards<TMode> where TMode : struct, Enum
{
    public const int MaxEntries = 10;

    private readonly IWritableDirProvider _data;
    private readonly ISawmill _sawmill;
    private readonly Dictionary<TMode, Board> _boards = new();

    public ArcadeLeaderboards(IWritableDirProvider data, ISawmill sawmill, Func<TMode, ResPath> savePath)
    {
        _data = data;
        _sawmill = sawmill;
        foreach (var mode in Enum.GetValues<TMode>())
        {
            var board = new Board(savePath(mode));
            _boards[mode] = board;
            Load(board);
        }
    }

    public List<ArcadeScoreEntry> Round(TMode mode) => _boards[mode].Round;

    public List<ArcadeScoreEntry> AllTime(TMode mode) => _boards[mode].AllTime;

    public void ClearRound()
    {
        foreach (var board in _boards.Values)
            board.Round.Clear();
    }

    /// <summary>
    /// Adds a score to both lists of its mode.
    /// </summary>
    /// <returns>The 1-based placement in each list, null when the score did not make it.</returns>
    public (int? Round, int? AllTime) Register(TMode mode, string name, int score, int level)
    {
        if (score <= 0)
            return (null, null);

        var board = _boards[mode];
        var entry = new ArcadeScoreEntry { Name = name, Score = score, Level = level };
        var round = Insert(board.Round, entry);
        var allTime = Insert(board.AllTime, entry);
        if (allTime != null)
            Save(board);

        return (round, allTime);
    }

    private static int? Insert(List<ArcadeScoreEntry> list, ArcadeScoreEntry entry)
    {
        var index = list.FindIndex(e => e.Score < entry.Score);
        if (index < 0)
            index = list.Count;
        if (index >= MaxEntries)
            return null;

        list.Insert(index, entry);
        if (list.Count > MaxEntries)
            list.RemoveAt(list.Count - 1);
        return index + 1;
    }

    private void Load(Board board)
    {
        board.AllTime.Clear();
        try
        {
            if (!_data.Exists(board.Path))
                return;

            using var reader = _data.OpenText(board.Path);
            var entries = JsonSerializer.Deserialize<List<ArcadeScoreEntry>>(reader.ReadToEnd());
            if (entries == null)
                return;

            entries.Sort((a, b) => b.Score.CompareTo(a.Score));
            board.AllTime.AddRange(entries.GetRange(0, Math.Min(MaxEntries, entries.Count)));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Could not read {board.Path}: {e}");
        }
    }

    private void Save(Board board)
    {
        try
        {
            using var writer = _data.OpenWriteText(board.Path);
            writer.Write(JsonSerializer.Serialize(board.AllTime));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Could not write {board.Path}: {e}");
        }
    }

    private sealed class Board(ResPath path)
    {
        public readonly ResPath Path = path;
        public readonly List<ArcadeScoreEntry> Round = new();
        public readonly List<ArcadeScoreEntry> AllTime = new();
    }
}
