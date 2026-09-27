using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>
/// Reads the Kaggle CSV (one row per match, ~90 "Home …" / "Away …" columns).
///
/// Data quality notes, handled here:
/// - Stadium names lost their non-ASCII characters ("Fuball Arena Mnchen") → fixed via the context file.
/// - Several stats appear twice ("Total shots" / "Total shots.", "Expected goals(xG)" / "Expected goals (xG)");
///   the first occurrence is used.
/// - "Accurate passes" is repeated from other rows (e.g. Hungary: 438 passes, 643 accurate) → ignored.
/// - There is no date, stage or group column → derived from row order and the context file.
/// </summary>
public static class MatchCsvReader
{
    public static IReadOnlyList<EuroMatch> Read(string csvPath, TournamentContext context)
    {
        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));

        csv.Read();
        csv.ReadHeader();
        var columns = IndexColumns(csv.HeaderRecord ?? throw new InvalidDataException("CSV has no header row."));

        var matches = new List<EuroMatch>();
        while (csv.Read())
        {
            var row = new Row(csv, columns);
            var matchNumber = matches.Count + 1;
            var rawStadium = row.Text("stadium");
            var stadium = context.Stadiums.GetValueOrDefault(rawStadium) ?? new StadiumInfo(rawStadium, "Unknown");
            var stage = context.StageOf(matchNumber);
            var home = ReadTeam(row, "Home", row.Text("home_team"), row.Int("home_goals"));
            var away = ReadTeam(row, "Away", row.Text("away_team"), row.Int("away_goals"));

            matches.Add(new EuroMatch
            {
                MatchNumber = matchNumber,
                Stage = stage,
                Group = stage == "Group stage" ? context.GroupOf(home.Team) : null,
                Stadium = stadium.Name,
                City = stadium.City,
                Attendance = row.Int("attendance"),
                Home = home,
                Away = away,
                Knockout = context.KnockoutDetails.GetValueOrDefault(matchNumber),
            });
        }

        return matches;
    }

    private static TeamMatchStats ReadTeam(Row row, string side, string team, int goals) => new()
    {
        Team = team,
        Goals = goals,
        ExpectedGoals = row.Double($"{side} Expected goals(xG)"),
        XgOpenPlay = row.Double($"{side} xG open play"),
        XgSetPlay = row.Double($"{side} xG set play"),
        NonPenaltyXg = row.Double($"{side} Non-penalty xG"),
        XgOnTarget = row.Double($"{side} xG on target (xGOT)"),
        TotalShots = row.Int($"{side} Total shots"),
        ShotsOnTarget = row.Int($"{side} Shots on target"),
        ShotsOffTarget = row.Int($"{side} Shots off target"),
        BlockedShots = row.Int($"{side} Blocked shots"),
        HitWoodwork = row.Int($"{side} Hit woodwork"),
        ShotsInsideBox = row.Int($"{side} Shots inside box"),
        ShotsOutsideBox = row.Int($"{side} Shots outside box"),
        BigChances = row.Int($"{side} Big chances"),
        BigChancesMissed = row.Int($"{side} Big chances missed"),
        Passes = row.Int($"{side} Passes"),
        PassesOwnHalf = row.Int($"{side} Own half"),
        PassesOppositionHalf = row.Int($"{side} Opposition half"),
        AccurateLongBalls = row.Rate($"{side} Accurate long balls"),
        AccurateCrosses = row.Rate($"{side} Accurate crosses"),
        Throws = row.Int($"{side} Throws"),
        TouchesInOppositionBox = row.Int($"{side} Touches in opposition box"),
        Offsides = row.Int($"{side} Offsides"),
        Corners = row.Int($"{side} Corners"),
        FoulsCommitted = row.Int($"{side} Fouls committed"),
        YellowCards = row.Int($"{side} Yellow cards"),
        RedCards = row.Int($"{side} Red cards"),
        TacklesWon = row.Rate($"{side} Tackles won"),
        Interceptions = row.Int($"{side} Interceptions"),
        Blocks = row.Int($"{side} Blocks"),
        Clearances = row.Int($"{side} Clearances"),
        KeeperSaves = row.Int($"{side} Keeper saves"),
        DuelsWon = row.Int($"{side} Duels won"),
        GroundDuelsWon = row.Rate($"{side} Ground duels won"),
        AerialDuelsWon = row.Rate($"{side} Aerial duels won"),
        SuccessfulDribbles = row.Rate($"{side} Successful dribbles"),
    };

    /// <summary>Maps header name → first column index (the header contains duplicates).</summary>
    private static Dictionary<string, int> IndexColumns(string[] header)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            columns.TryAdd(header[i].Trim(), i);
        }

        return columns;
    }

    private readonly struct Row(CsvReader csv, Dictionary<string, int> columns)
    {
        public string Text(string column) =>
            csv.GetField(columns.TryGetValue(column, out var index) ? index : throw new KeyNotFoundException($"CSV column '{column}' not found."))!.Trim();

        public int Int(string column) => int.Parse(Text(column), NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

        public double Double(string column) => double.Parse(Text(column), NumberStyles.Float, CultureInfo.InvariantCulture);

        public CountWithRate Rate(string column) => CountWithRate.Parse(Text(column));
    }
}
