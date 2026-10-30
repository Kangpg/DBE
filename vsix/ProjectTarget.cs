using System;
using System.Collections.Generic;

namespace Dbe.VisualStudio
{
    // Formats the solution hierarchy supplied by VS, not a filesystem path or UI caption.
    // MSBuild's documented solution target escaping preserves solution-only dependencies
    // and configuration mappings that a direct .vcxproj invocation would lose.
    public static class ProjectTarget
    {
        public static string Create(IEnumerable<string> hierarchy, bool rebuild)
        {
            var parts = new List<string>();
            foreach (string name in hierarchy) {
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[]{'\\','/',':',',','"','\r','\n','*','?'}) >= 0)
                    throw new ArgumentException("Unsupported solution project or folder name.");
                string part = name;
                foreach (char character in "%$@;.()'") part = part.Replace(character, '_');
                parts.Add(part);
            }
            if (parts.Count == 0) throw new ArgumentException("Select one C++ project.");
            string target = string.Join("\\", parts);
            foreach (string reserved in new[]{"Build","Rebuild","Clean","Publish"})
                if (string.Equals(target, reserved, StringComparison.OrdinalIgnoreCase)) {target = "Solution:" + target;break;}
            // The generated solution metaproject exposes Build as the bare project
            // target; unlike Rebuild, an explicit ':Build' target is not generated.
            return target + (rebuild ? ":Rebuild" : "");
        }
    }
}
