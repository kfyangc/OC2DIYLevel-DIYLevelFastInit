using System;
using System.Collections.Generic;

namespace DIYLevelFastInit
{
    /// <summary>Resolves unique asset basenames without retaining Unity objects.</summary>
    internal sealed class LevelAssetIndex
    {
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal LevelAssetIndex(IEnumerable<string> assetNames)
        {
            if (assetNames == null) throw new ArgumentNullException("assetNames");
            foreach (string path in assetNames)
            {
                if (string.IsNullOrEmpty(path)) continue;
                int start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
                int end = path.LastIndexOf('.');
                if (end < start) end = path.Length;
                if (end == start) continue;
                string name = path.Substring(start, end - start);
                if (_paths.ContainsKey(name)) _paths[name] = null;
                else _paths.Add(name, path);
            }
        }

        internal bool TryResolve(string metadataName, out string fullAssetPath)
        {
            fullAssetPath = null;
            return metadataName != null && _paths.TryGetValue(metadataName, out fullAssetPath) && fullAssetPath != null;
        }
    }
}
