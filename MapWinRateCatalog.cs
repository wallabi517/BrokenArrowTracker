namespace BrokenArrowSquadTracker;

internal static class MapWinRateCatalog
{
    // BATrace ranked report, 2026-10-19..2026-10-26. Win rate is from RU's
    // perspective in RU-vs-US matches; US is the complementary result.
    public const string SourceLabel = "BATrace 랭크 통계 · 2026-10-19~10-26";

    private sealed record Entry(MapWinRate Stats, string[] Aliases);

    private static readonly Entry[] Entries =
    [
        new(new("Baltiisk Peninsula", .3846153846, 39), ["Baltiisk Peninsula", "Baltiysk Peninsula"]),
        new(new("Bay of Winds", .4117647059, 17), ["Bay of Winds", "Whistling Bay"]),
        new(new("Black Coast", .3461538462, 26), ["Black Coast"]),
        new(new("Chernyakhovsky District", null, 0), ["Chernyakhovsky District", "Chernyakhovsk District"]),
        new(new("Riga Airport", .5384615385, 26), ["Riga Airport", "Airport"]),
        new(new("Chkalovsk Airbase", .6521739130, 23), ["Chkalovsk Airbase", "Chkalovsk Air Base"]),
        new(new("Coal Mountain", .6666666667, 18), ["Coal Mountain", "Coal Hill"]),
        new(new("Cold Harbor", null, 0), ["Cold Harbor"]),
        new(new("Daugavpils", null, 0), ["Daugavpils"]),
        new(new("Dead Center", .5185185185, 27), ["Dead Center", "Death Center"]),
        new(new("Highway of Death", null, 0), ["Highway of Death"]),
        new(new("Ignalina Powerplant", null, 0), ["Ignalina Powerplant", "Ignalina Power Plant"]),
        new(new("Jelgavas Pils", .5357142857, 28), ["Jelgavas Pils", "Jelgava"]),
        new(new("Kadaga Military Base", null, 0), ["Kadaga Military Base"]),
        new(new("Kaliningrad", .5714285714, 21), ["Kaliningrad"]),
        new(new("Klaipeda Rift", .3913043478, 23), ["Klaipeda Rift", "Klaipeda"]),
        new(new("Meandering River", null, 0), ["Meandering River"]),
        new(new("Oil Rush", .5384615385, 13), ["Oil Rush"]),
        new(new("Ruda Forest", .6190476190, 21), ["Ruda Forest"]),
        new(new("Snow Castle", null, 0), ["Snow Castle"]),
        new(new("Suwalki Gap", .3809523810, 21), ["Suwalki Gap", "Suwalki Corridor", "Suwalki"]),
        new(new("The Dam", .5333333333, 15), ["The Dam", "Kaunas Dam"])
    ];

    private static readonly IReadOnlyDictionary<int, string> GameMapIds = new Dictionary<int, string>
    {
        [3] = "Baltiisk Peninsula", [4] = "Black Coast", [5] = "Riga Airport",
        [7] = "The Dam", [9] = "Chkalovsk Airbase", [11] = "Dead Center",
        [12] = "Oil Rush", [13] = "Suwalki Gap", [14] = "Jelgavas Pils",
        [16] = "Klaipeda Rift", [17] = "Ruda Forest", [20] = "Bay of Winds",
        [23] = "Kaliningrad", [25] = "Coal Mountain"
    };

    public static IReadOnlyList<string> FindInText(string value) => Entries
        .Where(entry => entry.Aliases.Any(alias => ContainsMapName(value, alias)))
        .Select(entry => entry.Stats.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static MapWinRate? Find(string name) => Entries.FirstOrDefault(entry =>
        entry.Stats.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        entry.Aliases.Any(alias => name.Equals(alias, StringComparison.OrdinalIgnoreCase)))?.Stats;

    public static string? FindByGameId(int id) => GameMapIds.GetValueOrDefault(id);

    private static bool ContainsMapName(string value, string alias)
    {
        var index = value.IndexOf(alias, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var end = index + alias.Length;
            var after = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (before && after) return true;
            index = value.IndexOf(alias, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
