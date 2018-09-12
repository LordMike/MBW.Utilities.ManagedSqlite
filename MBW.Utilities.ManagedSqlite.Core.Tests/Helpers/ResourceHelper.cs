using System.IO;
using System.Reflection;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers
{
    public static class ResourceHelper
    {
        public static Stream OpenResource(string name)
        {
            return typeof(ResourceHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
        }
    }
}
