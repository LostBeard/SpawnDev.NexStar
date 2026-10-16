using SpawnDev.NexStar.Sky;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>What a person chose to see survives a reload, and bad saved data never breaks the chart.</summary>
public class ViewSettingsTests
{
    [TestMethod]
    public void TheChoices_SurviveASaveAndRestore()
    {
        var s = new ViewSettings
        {
            Layers = SkyLayers.Stars | SkyLayers.AltAzGrid | SkyLayers.ConstellationBoundaries,
            StarNames = false, ConstellationNames = false, SolarSystem = false, Night = false, StarSize = 1.75f,
        };
        var r = ViewSettings.FromJson(s.ToJson());
        Check.Equal(s.Layers, r.Layers, "layers");
        Check.That(!r.StarNames && !r.ConstellationNames && !r.SolarSystem && !r.Night, "the switches");
        Check.Near(1.75, r.StarSize, 1e-6, "star size");
    }

    [TestMethod]
    public void NothingSaved_OrGarbage_GivesTheDefaults()
    {
        foreach (var bad in new[] { null, "", "   ", "not json", "[]", "{" })
        {
            var r = ViewSettings.FromJson(bad);
            Check.Equal(SkyLayers.Default, r.Layers, $"layers for '{bad}'");
            Check.That(r.Night && r.StarNames && r.SolarSystem, $"defaults for '{bad}'");
            Check.Near(1.0, r.StarSize, 1e-6, $"star size for '{bad}'");
        }
    }

    [TestMethod]
    public void OutOfRangeValues_AreBroughtBackIntoRange()
    {
        var r = ViewSettings.FromJson("{\"Layers\":2147483647,\"StarSize\":99}");
        Check.That((r.Layers & ~(SkyLayers.AltAzGrid | SkyLayers.EquatorialGrid | SkyLayers.GroundAndHorizon | SkyLayers.Stars | SkyLayers.ConstellationLines | SkyLayers.ConstellationBoundaries | SkyLayers.DeepSky)) == 0, "no unknown layer bits");
        Check.Near(2.5, r.StarSize, 1e-6, "star size is clamped high");
        var low = ViewSettings.FromJson("{\"StarSize\":-3}");
        Check.Near(0.5, low.StarSize, 1e-6, "and low");
    }
}
