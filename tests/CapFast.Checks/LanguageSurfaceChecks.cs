using CapFast.Core;
public static class LanguageSurfaceChecks
{
    public static void Run()
    {
        var route = new LanguageSurfaceRouting();
        void Check(bool result) { if (!result) throw new Exception("Language surface retention failed."); }
        Check(route.First == "en/sw" && route.Second == "fr");
        Check(route.Apply(new("sw", "en", "fr", 1)) == (false, false));
        Check(route.Slot("en") == 0 && route.Slot("sw") == 0 && route.Slot("fr") == 1);
        Check(route.Apply(new("en", "sw", "fr", 2)) == (false, false));
        Check(route.Apply(null) == (false, false) && route.Second == "fr");
        Check(route.Apply(new("fr", "en", "sw", 3)) == (false, false));
        Check(route.Apply(new("de", "en", "fr", 4)) == (false, false));
        route.BeginRun();
        Check(!route.HasAutoAssignment);
        Check(route.ApplyDrain(new("en", "sw", "fr", 8)) == (false, false));
        Check(route.Slot("sw") >= 0 && route.Slot("fr") >= 0 && route.HasAutoAssignment);
        Check(route.ApplyDrain(new("sw", "en", "fr", 7)) == (false, false) && route.Slot("sw") >= 0);
        route.BeginRun();
        Check(route.ApplyDrain(new("en", "sw", "fr", 9)) == (false, false) && route.Slot("sw") >= 0);
        route.BeginRun();
        Check(!route.HasAutoAssignment && route.Slot("sw") >= 0 && route.Slot("fr") >= 0);
        Check(route.ApplyDrain(new("sw", "en", "fr", 10)) == (false, false) && route.Slot("en") >= 0);
        Check(LanguageSurfaceRouting.Label(route.First) == "English / Swahili");
        Check(LanguageSurfaceRouting.Status(null).Contains("Detecting"));
        Check(LanguageSurfaceRouting.Status(new("de", "en", "fr", 5)) == "German → English + Français");
    }
}
