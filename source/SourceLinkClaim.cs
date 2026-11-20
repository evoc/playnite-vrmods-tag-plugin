using System;

namespace Playnite.VRMods
{
    /// <summary>
    /// One row this plugin wrote into one game's <c>Links</c>, recorded so it stays ours afterwards:
    /// which source asked for it, the name it was given, and the URL it was given. This is the ledger
    /// entry behind <see cref="VrModTagSettings.AddedLinksByGame"/>, and the reason a link we created
    /// in an older version is still removable when the match that produced it is gone.
    ///
    /// The claim is <see cref="SourceId"/> plus <see cref="Url"/>. <see cref="Name"/> is stored only so
    /// removal can be name-and-URL scoped: a link the user typed themselves that happens to share a URL
    /// with one of ours carries a different name, and is never touched.
    ///
    /// Stored inside one settings string per game: claims are joined by ';', and each claim's three
    /// parts by <see cref="VrModNameMatcher.DecisionKeySeparator"/> (the separator decision keys
    /// already use). Both characters can legally appear in a URL, so the parts are escaped on the way
    /// in and unescaped on the way out — a ';-splitting' scheme that guesses would corrupt one map
    /// entry into two, and a corrupt ledger entry means a row we can no longer prove is ours.
    /// </summary>
    public readonly struct SourceLinkClaim : IEquatable<SourceLinkClaim>
    {
        /// <summary>Claims inside one game's value, and the character that separates them.</summary>
        internal const char ClaimSeparator = ';';

        /// <summary>The part separator inside one claim: the two-character decision-key separator.</summary>
        internal const string PartSeparator = VrModNameMatcher.DecisionKeySeparator;

        /// <summary>Which source wanted the row. Backs "this source no longer wants it" removals.</summary>
        public string SourceId { get; }

        /// <summary>The name the row was written with. Names are ours, so they are safe to rewrite.</summary>
        public string Name { get; }

        /// <summary>The URL the row was written with, already in <see cref="WebUrl.NormalizeUrl"/> form.</summary>
        public string Url { get; }

        public SourceLinkClaim(string sourceId, string name, string url)
        {
            SourceId = sourceId ?? string.Empty;
            Name = name ?? string.Empty;
            Url = url ?? string.Empty;
        }

        /// <summary>
        /// True when this claim describes the same row as another one. Two claims of different sources
        /// for one URL are deliberately distinct: the union rule keeps the row while either claim
        /// survives, and each source's own claim is dropped when that source stops wanting it.
        /// </summary>
        public bool SameClaim(SourceLinkClaim other)
            => string.Equals(SourceId, other.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Url, other.Url, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when this claim describes the row <paramref name="linkName"/> + <paramref name="url"/>
        /// actually is. Both halves must match: URL alone would also match a link the user typed, which
        /// is the one thing a removal must never do. Comparison is case-insensitive, which is how the
        /// reconcile already decides whether a URL is present on the game. Both arguments are nullable
        /// because they come straight off Playnite's <c>Link</c>, whose <c>Name</c> and <c>Url</c> are.
        /// </summary>
        public bool OwnsRow(string? linkName, string? url)
            => !string.IsNullOrEmpty(url)
            && string.Equals(Url, url, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, linkName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool Equals(SourceLinkClaim other)
            => SameClaim(other) && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj is SourceLinkClaim other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // OrdinalIgnoreCase, matching Equals: two claims that compare equal must agree here, and
            // this targets net462, where HashCode.Combine and the StringComparison overloads of
            // string.GetHashCode do not exist.
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(SourceId ?? string.Empty);
            hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Url ?? string.Empty);
            hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Name ?? string.Empty);
            return hash;
        }

        public static bool operator ==(SourceLinkClaim left, SourceLinkClaim right) => left.Equals(right);

        public static bool operator !=(SourceLinkClaim left, SourceLinkClaim right) => !left.Equals(right);

        /// <summary>The stored form of one claim, with both separators escaped inside the parts.</summary>
        internal string Encode()
            => Escape(SourceId) + PartSeparator + Escape(Name) + PartSeparator + Escape(Url);

        /// <summary>The stored form of a game's whole claim list.</summary>
        internal static string EncodeAll(SourceLinkClaim[] claims)
            => string.Join(ClaimSeparator.ToString(), Array.ConvertAll(claims, c => c.Encode()));

        /// <summary>
        /// One game's stored value, decoded. A part that cannot be decoded is skipped rather than
        /// guessed at: a claim we cannot read must never become a removal instruction, and dropping it
        /// only means a row falls back to the shape test the ledger is layered on top of.
        /// </summary>
        internal static SourceLinkClaim[] Parse(string? storedValue)
        {
            // Written as an explicit null test rather than string.IsNullOrEmpty: on net462 that helper
            // carries no [NotNullWhen(false)] annotation, so the compiler cannot prove the dereference
            // below is safe and warns - a false positive that would otherwise hide a real one.
            if (storedValue is null || storedValue.Length == 0)
                return Array.Empty<SourceLinkClaim>();

            var parts = storedValue.Split(ClaimSeparator);
            var claims = new SourceLinkClaim[parts.Length];
            var count = 0;
            foreach (var part in parts)
            {
                if (part.Length == 0)
                    continue;
                if (!TryParseClaim(part, out var claim))
                    continue;
                claims[count++] = claim;
            }

            Array.Resize(ref claims, count);
            return claims;
        }

        private static bool TryParseClaim(string part, out SourceLinkClaim claim)
        {
            claim = default;
            var first = part.IndexOf(PartSeparator, StringComparison.Ordinal);
            if (first < 0)
                return false;

            // Only the first two separators split the claim. The URL is whatever is left, so a URL that
            // happens to contain the separator after escaping round-trips without special casing.
            var second = part.IndexOf(PartSeparator, first + PartSeparator.Length, StringComparison.Ordinal);
            if (second < 0)
                return false;

            var sourceId = Unescape(part.Substring(0, first));
            var name = Unescape(part.Substring(first + PartSeparator.Length, second - first - PartSeparator.Length));
            var url = Unescape(part.Substring(second + PartSeparator.Length));
            if (sourceId.Length == 0 || url.Length == 0)
                return false;

            claim = new SourceLinkClaim(sourceId, name, url);
            return true;
        }

        /// <summary>
        /// Escapes the two characters that carry structure (';' and '|'), plus the escape character
        /// itself. Written as a two-character sequence so the mapping is its own inverse — no percent
        /// decoding involved, because a URL already contains percent-escapes of its own.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            if (value.IndexOfAny(new[] { '\\', ClaimSeparator, '|' }) < 0)
                return value;

            var builder = new System.Text.StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("$$"); break;
                    case ClaimSeparator: builder.Append("$s"); break;
                    case '|': builder.Append("$b"); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.ToString();
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('$') < 0)
                return value;

            var builder = new System.Text.StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '$' && i + 1 < value.Length)
                {
                    switch (value[i + 1])
                    {
                        case '$': builder.Append('\\'); i++; continue;
                        case 's': builder.Append(ClaimSeparator); i++; continue;
                        case 'b': builder.Append('|'); i++; continue;
                    }
                }

                builder.Append(value[i]);
            }

            return builder.ToString();
        }
    }
}
