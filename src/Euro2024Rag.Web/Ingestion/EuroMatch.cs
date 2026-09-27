using System.Globalization;
using System.Text.RegularExpressions;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>A value like "19(68%)": 19 successful out of an attempt count, 68% success rate.</summary>
public readonly partial record struct CountWithRate(int Count, int Percent)
{
    public static CountWithRate Parse(string raw)
    {
        var match = Pattern().Match(raw);
        if (!match.Success)
        {
            throw new FormatException($"Expected a value like '19(68%)' but got '{raw}'.");
        }

        return new CountWithRate(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    public override string ToString() => $"{Count} ({Percent}%)";

    [GeneratedRegex(@"^\s*(\d+)\s*\(\s*(\d+)%\s*\)\s*$")]
    private static partial Regex Pattern();
}

/// <summary>One team's statistics in one match.</summary>
public sealed record TeamMatchStats
{
    public required string Team { get; init; }
    public required int Goals { get; init; }
    public required double ExpectedGoals { get; init; }
    public required double XgOpenPlay { get; init; }
    public required double XgSetPlay { get; init; }
    public required double NonPenaltyXg { get; init; }
    public required double XgOnTarget { get; init; }
    public required int TotalShots { get; init; }
    public required int ShotsOnTarget { get; init; }
    public required int ShotsOffTarget { get; init; }
    public required int BlockedShots { get; init; }
    public required int HitWoodwork { get; init; }
    public required int ShotsInsideBox { get; init; }
    public required int ShotsOutsideBox { get; init; }
    public required int BigChances { get; init; }
    public required int BigChancesMissed { get; init; }
    public required int Passes { get; init; }
    public required int PassesOwnHalf { get; init; }
    public required int PassesOppositionHalf { get; init; }
    public required CountWithRate AccurateLongBalls { get; init; }
    public required CountWithRate AccurateCrosses { get; init; }
    public required int Throws { get; init; }
    public required int TouchesInOppositionBox { get; init; }
    public required int Offsides { get; init; }
    public required int Corners { get; init; }
    public required int FoulsCommitted { get; init; }
    public required int YellowCards { get; init; }
    public required int RedCards { get; init; }
    public required CountWithRate TacklesWon { get; init; }
    public required int Interceptions { get; init; }
    public required int Blocks { get; init; }
    public required int Clearances { get; init; }
    public required int KeeperSaves { get; init; }
    public required int DuelsWon { get; init; }
    public required CountWithRate GroundDuelsWon { get; init; }
    public required CountWithRate AerialDuelsWon { get; init; }
    public required CountWithRate SuccessfulDribbles { get; init; }
}

public sealed record KnockoutDetail(bool ExtraTime, string? PenaltyWinner = null, string? PenaltyScore = null);

public sealed record EuroMatch
{
    /// <summary>1-based, chronological (row order of the CSV).</summary>
    public required int MatchNumber { get; init; }
    public required string Stage { get; init; }
    public string? Group { get; init; }
    public required string Stadium { get; init; }
    public required string City { get; init; }
    public required int Attendance { get; init; }
    public required TeamMatchStats Home { get; init; }
    public required TeamMatchStats Away { get; init; }
    public KnockoutDetail? Knockout { get; init; }

    public bool IsGroupStage => Group is not null;

    /// <summary>The team that won or advanced; null for a group-stage draw.</summary>
    public string? Winner =>
        Home.Goals > Away.Goals ? Home.Team
        : Away.Goals > Home.Goals ? Away.Team
        : Knockout?.PenaltyWinner;

    public string? Loser => Winner is null ? null : Winner == Home.Team ? Away.Team : Home.Team;

    public bool Involves(string team) => Home.Team == team || Away.Team == team;

    public TeamMatchStats StatsFor(string team) => Home.Team == team ? Home : Away;

    public TeamMatchStats OpponentOf(string team) => Home.Team == team ? Away : Home;

    public string Scoreline => $"{Home.Team} {Home.Goals}–{Away.Goals} {Away.Team}";
}
