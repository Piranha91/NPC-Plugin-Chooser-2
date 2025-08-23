using System.Text.RegularExpressions;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
    /// Represents a program version string with support for major, minor, patch, and a single character revision (e.g., 2.0.8a).
    /// This class can be compared with other ProgramVersion instances or version strings.
    /// </summary>
    public class ProgramVersion : IComparable<ProgramVersion>, IEquatable<ProgramVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public int Revision { get; } // 'a' = 1, 'b' = 2, etc. 0 if no letter.

        /// <summary>
        /// Parses a version string into its component parts.
        /// </summary>
        /// <param name="versionString">The version string, e.g., "2.0.8b".</param>
        public ProgramVersion(string versionString)
        {
            // Default to 0.0.0 if the string is empty, which is useful for new settings files.
            if (string.IsNullOrWhiteSpace(versionString))
            {
                Major = 0; Minor = 0; Patch = 0; Revision = 0;
                return;
            }

            // Use regex to robustly parse the version string.
            // This captures three number groups and an optional trailing letter.
            var match = Regex.Match(versionString, @"^(\d+)\.(\d+)\.(\d+)([a-z])?$");

            if (match.Success)
            {
                Major = int.Parse(match.Groups[1].Value);
                Minor = int.Parse(match.Groups[2].Value);
                Patch = int.Parse(match.Groups[3].Value);

                if (match.Groups[4].Success)
                {
                    // Convert letter to a number: 'a' -> 1, 'b' -> 2, etc.
                    Revision = match.Groups[4].Value[0] - 'a' + 1;
                }
                else
                {
                    Revision = 0; // No revision letter
                }
            }
            else
            {
                // Fallback for simpler or unexpected formats, though regex should handle most valid cases.
                var parts = versionString.Split('.');
                Major = parts.Length > 0 && int.TryParse(parts[0], out var major) ? major : 0;
                Minor = parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0;
                
                if (parts.Length > 2)
                {
                    var lastPart = parts[2];
                    var revisionChar = lastPart.FirstOrDefault(char.IsLetter);

                    if (revisionChar != default(char))
                    {
                        var numberPart = new string(lastPart.TakeWhile(char.IsDigit).ToArray());
                        Patch = int.TryParse(numberPart, out var patch) ? patch : 0;
                        Revision = char.ToLower(revisionChar) - 'a' + 1;
                    }
                    else
                    {
                        Patch = int.TryParse(lastPart, out var patch) ? patch : 0;
                        Revision = 0;
                    }
                }
                else
                {
                    Patch = 0;
                    Revision = 0;
                }
            }
        }

        /// <summary>
        /// Compares this version to another version.
        /// </summary>
        public int CompareTo(ProgramVersion? other)
        {
            if (other is null) return 1;
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
            if (Revision != other.Revision) return Revision.CompareTo(other.Revision);
            return 0;
        }
        
        // Operator overloads for intuitive comparison
        public static bool operator <(ProgramVersion a, ProgramVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(ProgramVersion a, ProgramVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(ProgramVersion a, ProgramVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(ProgramVersion a, ProgramVersion b) => a.CompareTo(b) >= 0;
        public static bool operator ==(ProgramVersion? a, ProgramVersion? b) => a is null ? b is null : a.Equals(b);
        public static bool operator !=(ProgramVersion? a, ProgramVersion? b) => !(a == b);

        // Allows a string to be used where a ProgramVersion is expected.
        public static implicit operator ProgramVersion(string versionString) => new(versionString);

        public bool Equals(ProgramVersion? other)
        {
            if (other is null) return false;
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch && Revision == other.Revision;
        }

        public override bool Equals(object? obj) => obj is ProgramVersion other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision);

        public override string ToString()
        {
            var s = $"{Major}.{Minor}.{Patch}";
            if (Revision > 0)
            {
                s += (char)('a' + Revision - 1);
            }
            return s;
        }
    }