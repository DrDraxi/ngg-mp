// Stubs: game utility classes. Call them fully qualified (Utilities.CleanString.RemoveClone(x)); that compiles
// whether Utilities is a namespace or a class.
namespace Utilities
{
    // CONFIRMED (#27): public static class in namespace Utilities.
    public static class CleanString
    {
        public static string RemoveClone(string s) { throw null; }  // CONFIRMED (#27)
    }

    // CONFIRMED (#27): public static class in namespace Utilities.
    public static class Loc
    {
        public static string GetString(string key) { throw null; }  // CONFIRMED (#27)
    }
}
