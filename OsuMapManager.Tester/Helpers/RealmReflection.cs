namespace OsuMapManager.Tester.Helpers;

public static class RealmReflection
{
    public static int PInt(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v == null ? 0 : (int)v; } catch { return 0; } }
    public static dynamic? PObj(dynamic o, string n) { try { return o.GetType().GetProperty(n)?.GetValue(o); } catch { return null; } }
    public static string? PStr(dynamic o, string n) { try { return o.GetType().GetProperty(n)?.GetValue(o) as string; } catch { return null; } }
    public static double PDbl(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; } }
    public static DateTimeOffset? PDate(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v is DateTimeOffset d ? d : null; } catch { return null; } }
    public static int? PKeyCount(dynamic bm) { try { var d = PObj(bm, "Difficulty"); if (d != null) return (int)Math.Round(PDbl(d, "CircleSize")); } catch { } return null; }
    public static string T(string s, int m) => s.Length <= m ? s : s[..(m - 3)] + "...";
}
