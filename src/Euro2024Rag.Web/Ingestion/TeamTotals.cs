namespace Euro2024Rag.Web.Ingestion;

/// <summary>Everything one team did across the tournament, derived from its matches.</summary>
public sealed class TeamTotals
{
    public TeamTotals(string team, string? group, IReadOnlyList<EuroMatch> allMatches)
    {
        Team = team;
        Group = group;
        Matches = allMatches.Where(m => m.Involves(team)).OrderBy(m => m.MatchNumber).ToList();
    }

    public string Team { get; }
    public string? Group { get; }
    public IReadOnlyList<EuroMatch> Matches { get; }

    public int Played => Matches.Count;

    /// <summary>Results after 90/120 minutes; a penalty shootout counts as a draw, like in official records.</summary>
    public int Wins => Matches.Count(m => Own(m).Goals > Opp(m).Goals);
    public int Draws => Matches.Count(m => Own(m).Goals == Opp(m).Goals);
    public int Losses => Matches.Count(m => Own(m).Goals < Opp(m).Goals);

    public int GoalsFor => Sum(s => s.Goals);
    public int GoalsAgainst => Matches.Sum(m => Opp(m).Goals);
    public int GoalDifference => GoalsFor - GoalsAgainst;
    public int CleanSheets => Matches.Count(m => Opp(m).Goals == 0);

    public double ExpectedGoals => Matches.Sum(m => Own(m).ExpectedGoals);
    public double ExpectedGoalsAgainst => Matches.Sum(m => Opp(m).ExpectedGoals);

    public int Shots => Sum(s => s.TotalShots);
    public int ShotsOnTarget => Sum(s => s.ShotsOnTarget);
    public int BigChances => Sum(s => s.BigChances);
    public int Passes => Sum(s => s.Passes);
    public int Corners => Sum(s => s.Corners);
    public int Fouls => Sum(s => s.FoulsCommitted);
    public int YellowCards => Sum(s => s.YellowCards);
    public int RedCards => Sum(s => s.RedCards);
    public int KeeperSaves => Sum(s => s.KeeperSaves);
    public int Offsides => Sum(s => s.Offsides);

    public string Finish
    {
        get
        {
            var last = Matches[^1];
            return last.Stage switch
            {
                "Final" => last.Winner == Team ? "Champions" : "Runners-up",
                "Group stage" => "Eliminated in the group stage",
                _ => $"Eliminated in the {last.Stage}",
            };
        }
    }

    /// <summary>Knockout matches that went to extra time (including those decided on penalties).</summary>
    public IReadOnlyList<EuroMatch> ExtraTimeMatches => Matches.Where(m => m.Knockout?.ExtraTime == true).ToList();

    public IReadOnlyList<EuroMatch> PenaltyShootouts => Matches.Where(m => m.Knockout?.PenaltyWinner is not null).ToList();

    /// <summary>E.g. "win", "win after extra time", "draw after extra time, lost 3-5 on penalties".</summary>
    public string ResultDescription(EuroMatch match)
    {
        var own = Own(match).Goals;
        var opp = Opp(match).Goals;
        var afterExtraTime = match.Knockout?.ExtraTime == true ? " after extra time" : "";

        if (own != opp)
        {
            return (own > opp ? "win" : "loss") + afterExtraTime;
        }

        if (match.Knockout is { PenaltyWinner: { } winner, PenaltyScore: var score })
        {
            return winner == Team
                ? $"draw{afterExtraTime}, won {score} on penalties"
                : $"draw{afterExtraTime}, lost {Reverse(score)} on penalties";
        }

        return "draw";
    }

    /// <summary>Penalty scores are stored from the winner's side ("5-3"); the loser's side reads "3-5".</summary>
    private static string? Reverse(string? score) =>
        score?.Split('-') is [var a, var b] ? $"{b}-{a}" : score;

    private TeamMatchStats Own(EuroMatch m) => m.StatsFor(Team);
    private TeamMatchStats Opp(EuroMatch m) => m.OpponentOf(Team);
    private int Sum(Func<TeamMatchStats, int> selector) => Matches.Sum(m => selector(Own(m)));
}
