using System;
using System.Collections.Generic;
using Playnite.SDK.Models;

namespace Playnite.VRMods
{
    /// <summary>
    /// The one way this plugin changes which tags a game has.
    ///
    /// The tag list is always handed to Playnite as a new list. It is never added to or removed from
    /// in place, and that is not a style preference: Playnite's views of a game - the Tags field of
    /// the game you currently have selected, most of all - refresh from a change notification that
    /// only the <see cref="Game.TagIds"/> setter raises (it raises the derived Tags field the field
    /// binds to). A list edited where it stands therefore lands correctly in games.db and leaves the
    /// screen showing the tags the game had when the user selected it, until he clicks another game
    /// and back. A write that looks finished and is not is the worst kind to ship.
    ///
    /// This is the change the views listen to, which is not the change the database listens to: a
    /// caller that wants the new tags in games.db still owes
    /// <see cref="Playnite.SDK.IGameDatabaseAPI"/>.<c>Games.Update(game)</c> afterwards. The two are
    /// separate notifications and both are needed.
    /// </summary>
    internal static class VrModGameTags
    {
        /// <summary>
        /// Replaces a game's tag ids with <paramref name="tagIds"/>, in the order given. A null list
        /// means the game has no tags, which is written as an empty list rather than as null.
        /// </summary>
        internal static void SetTags(Game game, IEnumerable<Guid>? tagIds)
        {
            // A fresh list every time, even when the caller already built one: a caller that passes a
            // list it also holds on to could then "change" the game's tags by editing its own copy.
            game.TagIds = tagIds == null ? new List<Guid>() : new List<Guid>(tagIds);
        }
    }
}
