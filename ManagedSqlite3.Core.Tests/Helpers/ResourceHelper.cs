using System.IO;
using System.Reflection;

namespace Sqlite3RoLib.Tests.Helpers
{
    public static class ResourceHelper
    {
        public static Stream OpenResource(string name)
        {
            return typeof(ResourceHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
        }
    }
}
