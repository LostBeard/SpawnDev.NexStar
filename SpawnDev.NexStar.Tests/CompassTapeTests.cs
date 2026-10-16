using SpawnDev.NexStar.Sky;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>The heading tape: the compass points land where they should for any way of looking, including across north.</summary>
public class CompassTapeTests
{
    [TestMethod]
    public void TheCompassPoints_AreOnTheTape_AtTheirOwnBearings()
    {
        // looking south: south is in the middle, east and west are 90 degrees to either side
        var marks = CompassTape.Marks(180, 2.0, 200);
        var south = marks.Single(m => m.Label == "S");
        Check.Near(0, south.X, 1e-9, "south in the middle");
        Check.Near(-90.0, marks.Single(m => m.Label == "SE").X, 1e-9, "south-east 45 degrees to the left");
        Check.Near(90.0, marks.Single(m => m.Label == "SW").X, 1e-9, "south-west 45 degrees to the right");
        Check.That(marks.All(m => Math.Abs(m.X) <= 200), "all inside the tape");
    }

    [TestMethod]
    public void AcrossNorth_TheTapeWraps_AndNorthIsFoundFromEitherSide()
    {
        foreach (var centre in new[] { 350.0, 359.0, 0.0, 1.0, 10.0 })
        {
            var marks = CompassTape.Marks(centre, 3.0, 150);
            var north = marks.Single(m => m.Label == "N");
            double expected = ((0 - centre + 540) % 360 - 180) * 3.0;
            Check.Near(expected, north.X, 1e-9, $"north when looking at {centre}");
            Check.That(marks.Count(m => m.Azimuth == 0) == 1, "north once");
            for (int i = 1; i < marks.Count; i++) Check.That(marks[i].X > marks[i - 1].X, "left to right");
        }
    }

    [TestMethod]
    public void EveryDirection_FindsAtLeastOneCompassPoint_Within90Degrees()
    {
        for (double centre = 0; centre < 360; centre += 7.3)
        {
            var marks = CompassTape.Marks(centre, 2.0, 180);   // 90 degrees each side
            Check.That(marks.Any(m => m.Label != null), $"a compass point is on the tape when looking at {centre}");
        }
    }

    [TestMethod]
    public void TheMarksAreEveryFiveDegrees_AndEveryFifteenIsMajor()
    {
        var marks = CompassTape.Marks(100, 4.0, 100);
        Check.That(marks.All(m => Math.Abs(m.Azimuth % 5) < 1e-9), "five-degree steps");
        Check.That(marks.Where(m => m.Major).All(m => Math.Round(m.Azimuth) % 15 == 0), "majors every fifteen");
        Check.That(marks.Count >= 9, $"enough marks: {marks.Count}");
    }

    [TestMethod]
    public void NonsenseInput_GivesNoMarks_NotACrash()
    {
        Check.That(CompassTape.Marks(double.NaN, 2, 100).Count == 0 && CompassTape.Marks(10, 0, 100).Count == 0 && CompassTape.Marks(10, 2, 0).Count == 0, "no marks");
    }

    [TestMethod]
    public void TheReadout_NamesTheNearestPoint()
    {
        Check.Equal("N 0\u00b0", CompassTape.Readout(359.9), "just short of north");
        Check.Equal("E 90\u00b0", CompassTape.Readout(90), "east");
        Check.Equal("SW 225\u00b0", CompassTape.Readout(225), "south-west");
        Check.Equal("NW 330\u00b0", CompassTape.Readout(-30), "negative wraps");
    }
}
