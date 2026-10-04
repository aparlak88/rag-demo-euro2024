using System.Globalization;
using System.Text;
using Euro2024Rag.Web.Rag;
using static System.FormattableString;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>
/// Turns structured match rows into natural-language documents ("chunks") for retrieval.
///
/// Chunking strategy: a vector search returns the top-K most similar chunks, so it can answer
/// "what happened in Spain vs England?" from a single match chunk, but it cannot add up 51 rows
/// to answer "which team scored the most goals?". Aggregations are therefore pre-computed here
/// and stored as their own chunks (team summaries, group tables, venues, leaderboards, overview).
/// </summary>
public static class KnowledgeDocumentBuilder
{
    private const string Tournament = "UEFA Euro 2024";

    public static IReadOnlyList<KnowledgeChunk> Build(IReadOnlyList<EuroMatch> matches, TournamentContext context)
    {
        var teams = matches
            .SelectMany(m => new[] { m.Home.Team, m.Away.Team })
            .Distinct()
            .Order()
            .Select(team => new TeamTotals(team, context.GroupOf(team), matches))
            .ToList();

        var chunks = new List<KnowledgeChunk>();
        chunks.AddRange(matches.Select(MatchChunk));
        chunks.AddRange(teams.Select(TeamChunk));
        chunks.AddRange(context.Groups.OrderBy(g => g.Key).Select(g => GroupChunk(g.Key, matches, teams)));
        chunks.AddRange(matches.GroupBy(m => m.Stadium).OrderBy(g => g.Key).Select(VenueChunk));
        chunks.AddRange(Leaderboards(teams));
        chunks.Add(OverviewChunk(matches, teams));
        chunks.Add(RecordsChunk(matches));
        return chunks;
    }

    private static KnowledgeChunk MatchChunk(EuroMatch m)
    {
        var stageLabel = m.Group is null ? m.Stage : $"{m.Stage}, Group {m.Group}";
        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} match report — Match {m.MatchNumber} of 51: {m.Home.Team} vs {m.Away.Team} ({stageLabel}).");
        sb.AppendLine($"Final score: {m.Scoreline}. {ResultSentence(m)}");
        sb.AppendLine(Invariant($"Venue: {m.Stadium}, {m.City}. Attendance: {m.Attendance:N0}."));
        sb.AppendLine($"Match statistics ({m.Home.Team} | {m.Away.Team}):");

        void Stat(string label, Func<TeamMatchStats, object> value) =>
            sb.AppendLine(Invariant($"- {label}: {value(m.Home)} | {value(m.Away)}"));

        Stat("Goals", s => s.Goals);
        Stat("Expected goals (xG)", s => s.ExpectedGoals.ToString("0.00", CultureInfo.InvariantCulture));
        Stat("xG open play / set play", s => Invariant($"{s.XgOpenPlay:0.00} / {s.XgSetPlay:0.00}"));
        Stat("Non-penalty xG", s => s.NonPenaltyXg.ToString("0.00", CultureInfo.InvariantCulture));
        Stat("xG on target (xGOT)", s => s.XgOnTarget.ToString("0.00", CultureInfo.InvariantCulture));
        Stat("Total shots", s => s.TotalShots);
        Stat("Shots on target / off target / blocked", s => $"{s.ShotsOnTarget} / {s.ShotsOffTarget} / {s.BlockedShots}");
        Stat("Shots inside / outside the box", s => $"{s.ShotsInsideBox} / {s.ShotsOutsideBox}");
        Stat("Hit woodwork", s => s.HitWoodwork);
        Stat("Big chances (missed)", s => $"{s.BigChances} ({s.BigChancesMissed})");
        Stat("Passes (own half / opposition half)", s => $"{s.Passes} ({s.PassesOwnHalf} / {s.PassesOppositionHalf})");
        Stat("Accurate long balls", s => s.AccurateLongBalls);
        Stat("Accurate crosses", s => s.AccurateCrosses);
        Stat("Touches in opposition box", s => s.TouchesInOppositionBox);
        Stat("Corners", s => s.Corners);
        Stat("Offsides", s => s.Offsides);
        Stat("Throw-ins", s => s.Throws);
        Stat("Fouls committed", s => s.FoulsCommitted);
        Stat("Yellow cards", s => s.YellowCards);
        Stat("Red cards", s => s.RedCards);
        Stat("Tackles won", s => s.TacklesWon);
        Stat("Interceptions", s => s.Interceptions);
        Stat("Blocks", s => s.Blocks);
        Stat("Clearances", s => s.Clearances);
        Stat("Goalkeeper saves", s => s.KeeperSaves);
        Stat("Duels won", s => s.DuelsWon);
        Stat("Ground duels won", s => s.GroundDuelsWon);
        Stat("Aerial duels won", s => s.AerialDuelsWon);
        Stat("Successful dribbles", s => s.SuccessfulDribbles);

        return Chunk($"match-{m.MatchNumber}", "match", $"Match {m.MatchNumber}: {m.Scoreline} ({m.Stage})", sb,
            m.Home.Team, m.Away.Team);
    }

    private static string ResultSentence(EuroMatch m)
    {
        var extraTime = m.Knockout?.ExtraTime == true ? " The match went to extra time (the score includes extra-time goals)." : "";
        if (m.Knockout?.PenaltyWinner is { } penaltyWinner)
        {
            return $"The match finished level after extra time; {penaltyWinner} won the penalty shootout {m.Knockout.PenaltyScore} and advanced to the {NextStage(m.Stage)}.";
        }

        if (m.Winner is null)
        {
            return "The match ended in a draw.";
        }

        return m.Stage switch
        {
            "Group stage" => $"{m.Winner} won.",
            "Final" => $"{m.Winner} won the final and became {Tournament} champions; {m.Loser} finished as runners-up.{extraTime}",
            _ => $"{m.Winner} won and advanced to the {NextStage(m.Stage)}; {m.Loser} were eliminated.{extraTime}",
        };
    }

    private static string NextStage(string stage) => stage switch
    {
        "Round of 16" => "Quarter-finals",
        "Quarter-final" => "Semi-finals",
        "Semi-final" => "Final",
        _ => "next round",
    };

    private static KnowledgeChunk TeamChunk(TeamTotals t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} team summary — {t.Team}{(t.Group is null ? "" : $" (Group {t.Group})")}.");
        sb.AppendLine($"Tournament result: {t.Finish}.");
        sb.AppendLine($"Record: played {t.Played}, won {t.Wins}, drew {t.Draws}, lost {t.Losses} (penalty shootouts count as draws).");
        sb.AppendLine(Invariant($"Goals: scored {t.GoalsFor}, conceded {t.GoalsAgainst}, goal difference {t.GoalDifference:+0;-0;0}, clean sheets {t.CleanSheets}."));
        sb.AppendLine(Invariant($"Expected goals: xG {t.ExpectedGoals:0.00}, xG against {t.ExpectedGoalsAgainst:0.00}, goals minus xG {t.GoalsFor - t.ExpectedGoals:+0.00;-0.00;0.00}."));
        sb.AppendLine(Invariant($"Attack: {t.Shots} shots ({t.ShotsOnTarget} on target), {t.BigChances} big chances, {t.Corners} corners, {t.Offsides} offsides."));
        sb.AppendLine(Invariant($"Passing: {t.Passes} passes ({(double)t.Passes / t.Played:0} per match)."));
        sb.AppendLine($"Discipline: {t.Fouls} fouls, {t.YellowCards} yellow cards, {t.RedCards} red cards. Goalkeeper saves: {t.KeeperSaves}.");
        sb.AppendLine(ExtraTimeSummary(t));
        sb.AppendLine("Matches:");
        foreach (var m in t.Matches)
        {
            sb.AppendLine($"- Match {m.MatchNumber} ({m.Stage}): {m.Scoreline} — {t.ResultDescription(m)}, at {m.Stadium} ({m.City}).");
        }

        return Chunk($"team-{Slug(t.Team)}", "team", $"Team summary: {t.Team}", sb, t.Team);
    }

    private static string ExtraTimeSummary(TeamTotals t)
    {
        var extraTime = t.ExtraTimeMatches;
        if (extraTime.Count == 0)
        {
            return "Extra time and penalties: none — every match was decided in normal time (90 minutes).";
        }

        var details = string.Join("; ", extraTime.Select(m => $"Match {m.MatchNumber} ({m.Stage}) vs {m.OpponentOf(t.Team).Team}: {t.ResultDescription(m)}"));
        return $"Extra time and penalties: {extraTime.Count} match(es) went to extra time, {t.PenaltyShootouts.Count} decided by a penalty shootout — {details}. All other matches were decided in normal time.";
    }

    private static KnowledgeChunk GroupChunk(string group, IReadOnlyList<EuroMatch> matches, IReadOnlyList<TeamTotals> teams)
    {
        var groupMatches = matches.Where(m => m.Group == group).ToList();
        var advanced = matches.Where(m => m.Stage == "Round of 16").SelectMany(m => new[] { m.Home.Team, m.Away.Team }).ToHashSet();

        var table = teams
            .Where(t => t.Group == group)
            .Select(t => new GroupRow(t.Team, groupMatches.Where(m => m.Involves(t.Team)).ToList()))
            .OrderByDescending(r => r.Points)
            .ThenByDescending(r => r.GoalDifference)
            .ThenByDescending(r => r.GoalsFor)
            .ThenBy(r => r.CardPoints)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} Group {group} final standings (group stage table).");
        sb.AppendLine("Position | Team | Played | Won | Drawn | Lost | Goals for | Goals against | Goal difference | Points | Advanced to Round of 16");
        for (var i = 0; i < table.Count; i++)
        {
            var r = table[i];
            sb.AppendLine(Invariant($"{i + 1} | {r.Team} | {r.Played} | {r.Won} | {r.Drawn} | {r.Lost} | {r.GoalsFor} | {r.GoalsAgainst} | {r.GoalDifference:+0;-0;0} | {r.Points} | {(advanced.Contains(r.Team) ? "yes" : "no")}"));
        }

        sb.AppendLine("Note: order uses points, goal difference, goals scored and fewer cards; UEFA's official tie-breakers also include head-to-head results.");
        sb.AppendLine("Group matches:");
        foreach (var m in groupMatches)
        {
            sb.AppendLine($"- Match {m.MatchNumber}: {m.Scoreline} ({m.City})");
        }

        return Chunk($"group-{group.ToLowerInvariant()}", "group", $"Group {group} standings", sb, table.Select(r => r.Team).ToArray());
    }

    private sealed record GroupRow(string Team, IReadOnlyList<EuroMatch> Matches)
    {
        public int Played => Matches.Count;
        public int Won => Matches.Count(m => m.StatsFor(Team).Goals > m.OpponentOf(Team).Goals);
        public int Drawn => Matches.Count(m => m.StatsFor(Team).Goals == m.OpponentOf(Team).Goals);
        public int Lost => Played - Won - Drawn;
        public int GoalsFor => Matches.Sum(m => m.StatsFor(Team).Goals);
        public int GoalsAgainst => Matches.Sum(m => m.OpponentOf(Team).Goals);
        public int GoalDifference => GoalsFor - GoalsAgainst;
        public int Points => Won * 3 + Drawn;
        public int CardPoints => Matches.Sum(m => m.StatsFor(Team).YellowCards + 3 * m.StatsFor(Team).RedCards);
    }

    private static KnowledgeChunk VenueChunk(IGrouping<string, EuroMatch> venue)
    {
        var list = venue.OrderBy(m => m.MatchNumber).ToList();
        var city = list[0].City;
        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} venue — {venue.Key} in {city}.");
        sb.AppendLine(Invariant($"Hosted {list.Count} matches with a total attendance of {list.Sum(m => m.Attendance):N0} (average {list.Average(m => m.Attendance):N0}, highest {list.Max(m => m.Attendance):N0})."));
        sb.AppendLine($"Goals scored at this stadium: {list.Sum(m => m.Home.Goals + m.Away.Goals)}.");
        sb.AppendLine("Matches played here:");
        foreach (var m in list)
        {
            sb.AppendLine(Invariant($"- Match {m.MatchNumber} ({m.Stage}): {m.Scoreline}, attendance {m.Attendance:N0}"));
        }

        return Chunk($"venue-{Slug(city)}", "venue", $"Venue: {venue.Key} ({city})", sb,
            list.SelectMany(m => new[] { m.Home.Team, m.Away.Team }).Distinct().ToArray());
    }

    private static IEnumerable<KnowledgeChunk> Leaderboards(IReadOnlyList<TeamTotals> teams)
    {
        yield return Leaderboard("goals-scored", "most goals scored (attack)", teams, t => t.GoalsFor, descending: true);
        yield return Leaderboard("goals-conceded", "fewest goals conceded (defence)", teams, t => t.GoalsAgainst, descending: false);
        yield return Leaderboard("expected-goals", "highest expected goals (xG) created", teams, t => t.ExpectedGoals, descending: true);
        yield return Leaderboard("xg-overperformance", "goals minus xG (finishing over/under-performance)", teams, t => t.GoalsFor - t.ExpectedGoals, descending: true);
        yield return Leaderboard("shots", "most shots", teams, t => t.Shots, descending: true);
        yield return Leaderboard("shots-on-target", "most shots on target", teams, t => t.ShotsOnTarget, descending: true);
        yield return Leaderboard("passes", "most passes (possession style)", teams, t => t.Passes, descending: true);
        yield return Leaderboard("clean-sheets", "most clean sheets", teams, t => t.CleanSheets, descending: true);
        yield return Leaderboard("yellow-cards", "most yellow cards (discipline)", teams, t => t.YellowCards, descending: true);
        yield return Leaderboard("red-cards", "most red cards", teams, t => t.RedCards, descending: true);
        yield return Leaderboard("fouls", "most fouls committed", teams, t => t.Fouls, descending: true);
        yield return Leaderboard("keeper-saves", "most goalkeeper saves", teams, t => t.KeeperSaves, descending: true);
        yield return Leaderboard("wins", "most wins", teams, t => t.Wins, descending: true);
    }

    private static KnowledgeChunk Leaderboard(
        string id, string metric, IReadOnlyList<TeamTotals> teams, Func<TeamTotals, double> value, bool descending)
    {
        var ranked = (descending ? teams.OrderByDescending(value) : teams.OrderBy(value)).ThenBy(t => t.Team).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} team leaderboard — {metric}. Ranking of all 24 teams across the whole tournament.");
        sb.AppendLine("Rank | Team | Total | Matches played | Per match");
        for (var i = 0; i < ranked.Count; i++)
        {
            var t = ranked[i];
            var total = value(t);
            sb.AppendLine(Invariant($"{i + 1} | {t.Team} | {total:0.##} | {t.Played} | {total / t.Played:0.00}"));
        }

        return Chunk($"leaderboard-{id}", "leaderboard", $"Leaderboard: {metric}", sb);
    }

    private static KnowledgeChunk OverviewChunk(IReadOnlyList<EuroMatch> matches, IReadOnlyList<TeamTotals> teams)
    {
        var final = matches.Single(m => m.Stage == "Final");
        var semiFinalists = matches.Where(m => m.Stage == "Semi-final").SelectMany(m => new[] { m.Home.Team, m.Away.Team });
        var quarterFinalists = matches.Where(m => m.Stage == "Quarter-final").SelectMany(m => new[] { m.Home.Team, m.Away.Team });
        var goals = matches.Sum(m => m.Home.Goals + m.Away.Goals);

        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} tournament overview (hosted in Germany).");
        sb.AppendLine($"Champions: {final.Winner}. Runners-up: {final.Loser}. Final: {final.Scoreline} at {final.Stadium}, {final.City}.");
        sb.AppendLine($"Semi-finalists: {string.Join(", ", semiFinalists)}.");
        sb.AppendLine($"Quarter-finalists: {string.Join(", ", quarterFinalists)}.");
        sb.AppendLine($"Format: 24 teams in 6 groups (A–F), then Round of 16, quarter-finals, semi-finals and the final; {matches.Count} matches in total.");
        foreach (var stage in matches.GroupBy(m => m.Stage))
        {
            sb.AppendLine(Invariant($"- {stage.Key}: {stage.Count()} matches, {stage.Sum(m => m.Home.Goals + m.Away.Goals)} goals"));
        }

        sb.AppendLine(Invariant($"Goals: {goals} in total, {(double)goals / matches.Count:0.00} per match. Draws: {matches.Count(m => m.Home.Goals == m.Away.Goals)}."));
        sb.AppendLine(Invariant($"Attendance: {matches.Sum(m => m.Attendance):N0} in total, {matches.Average(m => m.Attendance):N0} per match."));
        sb.AppendLine(Invariant($"Cards: {matches.Sum(m => m.Home.YellowCards + m.Away.YellowCards)} yellow, {matches.Sum(m => m.Home.RedCards + m.Away.RedCards)} red."));
        sb.AppendLine($"Best attack: {teams.MaxBy(t => t.GoalsFor)!.Team} ({teams.Max(t => t.GoalsFor)} goals).");
        sb.AppendLine("Data source: Kaggle dataset 'Euro 2024 Matches' (team-level statistics per match; no player names, goal scorers or match dates).");

        return Chunk("tournament-overview", "tournament", "Tournament overview", sb, final.Home.Team, final.Away.Team);
    }

    private static KnowledgeChunk RecordsChunk(IReadOnlyList<EuroMatch> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Tournament} match records and extremes.");

        void Top(string title, IEnumerable<EuroMatch> ordered, Func<EuroMatch, string> detail)
        {
            sb.AppendLine($"{title}:");
            foreach (var m in ordered.Take(5))
            {
                sb.AppendLine($"- Match {m.MatchNumber} ({m.Stage}): {m.Scoreline} — {detail(m)}");
            }
        }

        Top("Highest-scoring matches", matches.OrderByDescending(m => m.Home.Goals + m.Away.Goals), m => $"{m.Home.Goals + m.Away.Goals} goals");
        Top("Biggest winning margins", matches.OrderByDescending(m => Math.Abs(m.Home.Goals - m.Away.Goals)), m => $"margin {Math.Abs(m.Home.Goals - m.Away.Goals)}");
        Top("Highest attendance", matches.OrderByDescending(m => m.Attendance), m => Invariant($"{m.Attendance:N0} at {m.Stadium}"));
        Top("Lowest attendance", matches.OrderBy(m => m.Attendance), m => Invariant($"{m.Attendance:N0} at {m.Stadium}"));
        Top("Most shots in a match (both teams)", matches.OrderByDescending(m => m.Home.TotalShots + m.Away.TotalShots), m => $"{m.Home.TotalShots + m.Away.TotalShots} shots");
        Top("Highest combined xG", matches.OrderByDescending(m => m.Home.ExpectedGoals + m.Away.ExpectedGoals), m => Invariant($"xG {m.Home.ExpectedGoals + m.Away.ExpectedGoals:0.00}"));
        Top("Most cards in a match", matches.OrderByDescending(m => m.Home.YellowCards + m.Away.YellowCards + m.Home.RedCards + m.Away.RedCards),
            m => $"{m.Home.YellowCards + m.Away.YellowCards} yellow, {m.Home.RedCards + m.Away.RedCards} red");
        Top("Goalless draws (0–0)", matches.Where(m => m.Home.Goals + m.Away.Goals == 0), _ => "0 goals");

        return Chunk("tournament-records", "tournament", "Match records and extremes", sb);
    }

    private static KnowledgeChunk Chunk(string id, string kind, string title, StringBuilder content, params string[] teams) => new()
    {
        Key = KnowledgeChunk.CreateKey(id),
        Id = id,
        Kind = kind,
        Title = title,
        Content = content.ToString().TrimEnd(),
        Teams = [.. teams],
    };

    private static string Slug(string value) =>
        new(value.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ')
            .Select(c => c == ' ' ? '-' : c)
            .ToArray());
}
