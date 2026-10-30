using System.Text.RegularExpressions;

namespace NugetProxy.Services
{
    /// <summary>
    /// A validated flat container request. Only the shapes NuGet itself asks for are accepted,
    /// so a request path never reaches the file system unchecked.
    /// </summary>
    public sealed partial class PackagePath
    {
        // NuGet package ids are letters, digits, '.', '_' and '-'; versions add '+' for build
        // metadata. Neither may start with '.', which also rules out "." and "..".
        [GeneratedRegex(@"^[a-z0-9_-][a-z0-9._-]{0,99}$")]
        private static partial Regex IdPattern();

        [GeneratedRegex(@"^[a-z0-9_-][a-z0-9.+_-]{0,63}$")]
        private static partial Regex VersionPattern();

        public string Id { get; }
        public string? Version { get; }
        public string? FileName { get; }

        /// <summary>The package's version list: {id}/index.json. It changes whenever a version is published.</summary>
        public bool IsVersionList => Version == null;

        /// <summary>The path relative to the upstream flat container and to the cache root, always lower case.</summary>
        public string RelativePath => IsVersionList ? $"{Id}/index.json" : $"{Id}/{Version}/{FileName}";

        private PackagePath(string id, string? version, string? fileName)
        {
            Id = id;
            Version = version;
            FileName = fileName;
        }

        /// <summary>
        /// Accepts {id}/index.json, {id}/{version}/{id}.{version}.nupkg and {id}/{version}/{id}.nuspec.
        /// Package files never change once published, so only those two are cached.
        /// </summary>
        public static bool TryParse(string? path, out PackagePath? packagePath)
        {
            packagePath = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // The flat container protocol is lower case; clients already send it that way.
            var segments = path.ToLowerInvariant().Split('/');

            if (segments.Length == 2 && segments[1] == "index.json" && IdPattern().IsMatch(segments[0]))
            {
                packagePath = new PackagePath(segments[0], null, null);
                return true;
            }

            if (segments.Length == 3 && IdPattern().IsMatch(segments[0]) && VersionPattern().IsMatch(segments[1]))
            {
                string id = segments[0], version = segments[1], fileName = segments[2];
                if (fileName == $"{id}.{version}.nupkg" || fileName == $"{id}.nuspec")
                {
                    packagePath = new PackagePath(id, version, fileName);
                    return true;
                }
            }

            return false;
        }
    }
}
