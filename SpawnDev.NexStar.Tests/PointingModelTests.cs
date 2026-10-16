using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>The offsets between the sky's true frame and an unaligned mount's own.</summary>
public class PointingModelTests
{
    [TestMethod]
    public void WithoutAnAlignment_TheMountsValuesAreTheTruth()
    {
        var m = new PointingModel();
        Check.That(!m.IsSet, "a new model is not set");
        var (az, alt) = m.ToMount(123.4, 56.7);
        Check.Near(123.4, az, 1e-12, "azimuth"); Check.Near(56.7, alt, 1e-12, "altitude");
    }

    [TestMethod]
    public void AlignOnOneObject_GivesTheOffsets_AndGotosLandOnTheTruth()
    {
        var m = new PointingModel();
        // Vega is truly at 250 / 40 but the mount, powered on facing another way, says 71 / 37
        m.AlignOn(250, 40, 71, 37);
        Check.That(m.IsSet, "set");
        Check.Near(-179, m.AzimuthOffset, 1e-9, "azimuth offset");
        Check.Near(-3, m.AltitudeOffset, 1e-9, "altitude offset");
        // the object it was aligned on maps to where the mount says it is
        var (az, alt) = m.ToMount(250, 40);
        Check.Near(71, az, 1e-9, "goto azimuth for the object it was aligned on"); Check.Near(37, alt, 1e-9, "goto altitude");
        // another object: true 100 / 20 is mount 281 / 17
        var (az2, alt2) = m.ToMount(100, 20);
        Check.Near(281, az2, 1e-9, "azimuth"); Check.Near(17, alt2, 1e-9, "altitude");
        // and the mount's reading goes back to the truth
        var (ta, tl) = m.FromMount(281, 17);
        Check.Near(100, ta, 1e-9, "true azimuth"); Check.Near(20, tl, 1e-9, "true altitude");
    }

    [TestMethod]
    public void TheOffsetWrapsThroughNorth_InBothDirections()
    {
        var m = new PointingModel();
        m.AlignOn(10, 30, 350, 30);   // the mount reads 20 degrees less
        Check.Near(-20, m.AzimuthOffset, 1e-9, "offset");
        var (az, _) = m.ToMount(5, 30);
        Check.Near(345, az, 1e-9, "5 degrees true is 345 on the mount");
        var (back, _) = m.FromMount(345, 30);
        Check.Near(5, back, 1e-9, "and back");
        var (az2, _) = m.ToMount(359, 30);
        Check.Near(339, az2, 1e-9, "359 true");
    }

    [TestMethod]
    public void TheRoundTrip_IsExact_ForRandomOffsetsAndTargets()
    {
        var rng = new Random(77);
        int checkedCount = 0;
        for (int i = 0; i < 2000; i++)
        {
            var m = new PointingModel();
            m.AlignOn(rng.NextDouble() * 360, rng.NextDouble() * 150 - 60, rng.NextDouble() * 360, rng.NextDouble() * 150 - 60);
            var az = rng.NextDouble() * 360; var alt = rng.NextDouble() * 120 - 30;
            var (ma, ml) = m.ToMount(az, alt);
            if (ml <= -90 || ml >= 90) continue;   // clamped: the mount cannot reach it
            var (ba, bl) = m.FromMount(ma, ml);
            var dAz = Math.Abs(((ba - az + 540) % 360) - 180);
            Check.That(dAz < 1e-9 && Math.Abs(bl - alt) < 1e-9, $"round trip {az}/{alt} -> {ma}/{ml} -> {ba}/{bl}");
            checkedCount++;
        }
        Check.That(checkedCount > 500, $"only {checkedCount} of the cases were reachable: the loop proves nothing");
    }

    [TestMethod]
    public void TheAltitudeIsClamped_ToWhatTheMountCanReach()
    {
        var m = new PointingModel();
        m.AlignOn(0, 10, 0, 30);   // +20
        var (_, alt) = m.ToMount(0, 85);
        Check.Near(90, alt, 1e-12, "past the zenith is the zenith");
    }

    [TestMethod]
    public void SavingAndRestoring_KeepsTheOffsets_AndBadDataGivesTheIdentity()
    {
        var m = new PointingModel();
        m.AlignOn(250, 40, 71, 37);
        var restored = PointingModel.FromJson(m.ToJson());
        Check.That(restored.IsSet, "restored is set");
        Check.Near(m.AzimuthOffset, restored.AzimuthOffset, 1e-12, "azimuth"); Check.Near(m.AltitudeOffset, restored.AltitudeOffset, 1e-12, "altitude");
        foreach (var bad in new[] { null, "", "not json", "{}", "{\"Az\":1,\"Alt\":2,\"IsSet\":false}" })
        {
            var r = PointingModel.FromJson(bad);
            Check.That(!r.IsSet && r.AzimuthOffset == 0 && r.AltitudeOffset == 0, $"bad data '{bad}' gives the identity model");
        }
    }

    [TestMethod]
    public void AnAlignmentOnNonsense_IsRefused()
    {
        var m = new PointingModel();
        try { m.AlignOn(double.NaN, 10, 0, 0); Check.That(false, "NaN accepted"); }
        catch (ArgumentException) { }
        Check.That(!m.IsSet, "still not set");
    }
}
