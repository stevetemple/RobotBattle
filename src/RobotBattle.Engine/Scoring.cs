namespace RobotBattle.Engine;

/// <summary>Turns round placings into per-round scores and leaderboard rating changes.</summary>
public static class Scoring
{
    public const double StartRating = 1000;
    public const double K = 32;

    /// <summary>1 if place a beats place b (lower is better), 0.5 for a tie, else 0.</summary>
    private static double Pair(int a, int b) => a < b ? 1 : a == b ? 0.5 : 0;

    /// <summary>
    /// Each robot's round score in 0..1: the average of its pairwise results against every other robot.
    /// With two robots that is 1 for a win, 0.5 for a draw, 0 for a loss. Scores in a round always sum to N/2.
    /// </summary>
    public static double[] RoundScores(IReadOnlyList<int> places)
    {
        int n = places.Count;
        var scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) if (j != i) scores[i] += Pair(places[i], places[j]);
            scores[i] /= n - 1;
        }
        return scores;
    }

    /// <summary>
    /// Multiplayer Elo: every pair of robots is an Elo game, and each robot's change is the sum of its pair results scaled by 1/(N-1)
    /// so that a round moves ratings by roughly the same amount whatever the number of robots. Changes always sum to zero.
    /// </summary>
    public static double[] EloDeltas(IReadOnlyList<double> ratings, IReadOnlyList<int> places)
    {
        int n = ratings.Count;
        var delta = new double[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                double expected = 1 / (1 + Math.Pow(10, (ratings[j] - ratings[i]) / 400));
                delta[i] += K / (n - 1) * (Pair(places[i], places[j]) - expected);
            }
        return delta;
    }
}
