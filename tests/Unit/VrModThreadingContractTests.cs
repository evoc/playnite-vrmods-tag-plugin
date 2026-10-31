using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Threading contract: the lock and UI-hop rules this plugin is built on.
    ///
    /// Two rules the plugin must actually hold, not just document:
    ///
    /// 1. <c>_newGameLock</c> is a leaf lock. Before this round <c>SeedKnownGames</c> and
    ///    <c>NotifyLibraryUpdated</c> enumerated <c>Database.Games</c> INSIDE it. Playnite raises its
    ///    library-change events on Playnite's own thread, and the new-game drain runs on a timer
    ///    thread, so a slow library walk (a real one reads a LiteDB result set: seconds on a big
    ///    library) held a lock that every other new-game path has to take. The first test below is the
    ///    regression test for that shape: it makes the walk slow and checks a library event does not
    ///    have to wait for it.
    ///
    /// 2. The scan entry points block their caller (progress window, modal dialog,
    ///    <c>GetAwaiter().GetResult()</c>) and therefore must not run on Playnite's UI thread. That was
    ///    a comment. <see cref="VrModTagPlugin.ScanBodyMayRunOnCallerThread"/> is the decision, so it
    ///    can be tested without a WPF application.
    /// </summary>
    [TestFixture]
    public class VrModThreadingContractTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IGameDatabaseAPI _mockDb = null!;
        private IPlayniteAPI _mockApi = null!;
        private RecordingLogger _logger = null!;
        private VrModSourceRegistry _registry = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            _mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            _logger = new RecordingLogger();

            _mockDb.Games.Returns(_games);
            _mockDb.Tags.Returns(_tags);
            _mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(_mockDb);

            _registry = new VrModSourceRegistry();
        }

        private VrModTagService CreateService(int debounceMs = 50)
            => new VrModTagService(_mockApi, new VrModTagSettings(), _logger, _registry, debounceMs);

        private void RegisterVampyrSource()
        {
            _registry.Register(new TestParser("src-threading", new[]
            {
                new VrModEntry { GameName = "Vampyr", Category = "UEVR" }
            }));
        }

        // --- 1. The leaf-lock rule ----------------------------------------------------------

        /// <summary>
        /// A slow pass over the library (here: 20 rows x 60 ms, and `Database.Games` really is walked
        /// twice on this path - once to snapshot, once by the drain) must not hold up a library-change
        /// event arriving on another thread.
        ///
        /// Before the fix this test fails: `NotifyLibraryUpdated` held `_newGameLock` for the whole
        /// walk, so the event thread blocked on that lock for the walk's remaining ~1 s. On a real
        /// library that window is seconds, and the thread being blocked is Playnite's own.
        /// </summary>
        [Test]
        public void LibraryEventDuringASlowLibraryWalk_IsNotHeldUpByTheWalk()
        {
            const int rows = 20;
            var perRow = TimeSpan.FromMilliseconds(60);

            RegisterVampyrSource();
            var service = CreateService();
            for (var i = 0; i < rows; i++)
                _games.Add("Known Game " + i);

            // Startup order, as the plugin does it: seed (fast) and subscribe, then the library gets
            // walked again by a later update pass - that walk is what we make slow.
            service.SeedKnownGames();
            _games.EnumerationDelay = perRow;

            var walkFinished = new ManualResetEventSlim(false);
            var walkThread = new Thread(() =>
            {
                service.NotifyLibraryUpdated();
                walkFinished.Set();
            })
            { IsBackground = true };
            walkThread.Start();

            // Act inside the walk: rows * perRow ~= 1.2 s, so 150 ms in it is definitely still running.
            Thread.Sleep(150);
            Assert.That(walkFinished.IsSet, Is.False,
                "the library walk finished before the test could act inside it - raise the row count or the per-row delay, otherwise this test proves nothing.");

            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            var sw = Stopwatch.StartNew();
            _games.RaiseItemCollectionChanged(addedItems: new[] { game });
            sw.Stop();

            // The event handler's own work is HashSet + queue + a timer change: milliseconds. It only
            // costs seconds if it has to wait behind the library walk.
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(400)),
                $"Playnite's library event waited {sw.ElapsedMilliseconds} ms for a library walk that should not hold the new-game lock. Log:\n" + _logger.Dump());

            Assert.That(walkFinished.Wait(TimeSpan.FromSeconds(20)), Is.True, "the library walk did not complete.");

            // And the queued game is still scanned: moving the walk out of the lock did not lose it.
            var tagged = SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(25));
            Assert.That(tagged, Is.True,
                "the game added during the library walk was not tagged: the new-game path lost it. Log:\n" + _logger.Dump());
            Assert.That(_logger.Dump(), Does.Not.Contain("faulted"),
                "the new-game path faulted. Log:\n" + _logger.Dump());
        }

        /// <summary>
        /// The same rule on the other path that used to enumerate under the lock: a library-update
        /// pass must still find a game that was added after the seed, and still queue exactly it.
        /// </summary>
        [Test]
        public void NotifyLibraryUpdated_StillQueuesGamesThatAppearAfterTheSeed()
        {
            RegisterVampyrSource();
            var service = CreateService();

            _games.Add("Already Known");
            service.SeedKnownGames();

            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            service.NotifyLibraryUpdated();

            var tagged = SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(25));
            Assert.That(tagged, Is.True,
                "a game discovered by the library-update diff was not queued and tagged. Log:\n" + _logger.Dump());
            Assert.That(_logger.Dump(), Does.Contain("1 new game(s) queued"),
                "the library-update pass should report exactly the one new game. Log:\n" + _logger.Dump());
        }

        // --- 2. The blocking-entry-point rule -----------------------------------------------

        /// <summary>
        /// The scan bodies block their caller, so they may only block a thread that is not Playnite's
        /// UI thread. With no WPF UI (unit tests, headless host) inline execution is fine and is what
        /// every other test in this suite relies on.
        /// </summary>
        [TestCase(true, true, false, TestName = "UiThreadWithUi_MustNotBlockHere")]
        [TestCase(true, false, true, TestName = "BackgroundThreadWithUi_MayBlockHere")]
        [TestCase(false, true, true, TestName = "NoUiAtAll_MayRunInline")]
        [TestCase(false, false, true, TestName = "NoUiAndNotOnUiThread_MayRunInline")]
        public void ScanBodyMayRunOnCallerThread_DecidesByUiAndThread(bool hasWpfUi, bool callerIsOnUiThread, bool expected)
            => Assert.That(
                VrModTagPlugin.ScanBodyMayRunOnCallerThread(hasWpfUi, callerIsOnUiThread),
                Is.EqualTo(expected),
                "a scan body run on Playnite's UI thread blocks that thread: the progress window and " +
                "the modal review dialog are shown on it, so the entry point has to re-dispatch.");

        // Helpers, duplicated per fixture as the other new-game tests do (this suite has no shared
        // mock fixture to inherit).
        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;

            public TestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries;
            }

            public override string? ChangeIdentifier => null;

            public override System.Threading.Tasks.Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => System.Threading.Tasks.Task.FromResult(_entries);

            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        private class RecordingLogger : ILogger
        {
            // Writes arrive from drain/timer threads while the test thread asserts: lock, and hand out
            // a copy of the lines.
            private readonly object _sync = new();
            private readonly List<string> _lines = new();

            public List<string> Lines
            {
                get { lock (_sync) return new List<string>(_lines); }
            }

            public string Dump()
            {
                lock (_sync) return string.Join("\n", _lines);
            }

            private void Add(string line)
            {
                lock (_sync) _lines.Add(line);
            }

            public void Info(string message) => Add("INFO " + message);
            public void Debug(string message) => Add("DEBUG " + message);
            public void Warn(string message) => Add("WARN " + message);
            public void Error(string message) => Add("ERROR " + message);
            public void Error(Exception ex) => Add("ERROR " + ex);
            public void Info(Exception ex, string message) => Add("INFO " + message + " " + ex);
            public void Debug(Exception ex, string message) => Add("DEBUG " + message + " " + ex);
            public void Warn(Exception ex, string message) => Add("WARN " + message + " " + ex);
            public void Error(Exception ex, string message) => Add("ERROR " + message + " " + ex);
            public void Trace(string message) => Add("TRACE " + message);
            public void Trace(Exception ex, string message) => Add("TRACE " + message + " " + ex);
        }
    }
}
