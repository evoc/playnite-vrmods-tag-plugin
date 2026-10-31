using System;
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
    /// The UI-hop half of the threading contract.
    ///
    /// Playnite runs a global progress action on a worker thread and shows its window with
    /// <c>Window.ShowDialog()</c>: <c>ProgressViewViewModel.ActivateProgress</c> starts the worker task
    /// and then <c>window.CreateAndOpenDialog(this)</c>; <c>DesktopDialogs.ActivateGlobalProgress</c>
    /// marshals the activation with <c>Invoke</c>, i.e. <c>Dispatcher.Invoke</c>. A nested dispatcher
    /// frame runs while the worker waits, so the UI thread is NOT idle during that wait - it pumps
    /// queued work. A scan that changes the library queues Playnite's library-changed handler, and in
    /// this plugin that handler is <see cref="VrModTagService.NotifyLibraryUpdated"/>, which takes the
    /// new-game lock.
    ///
    /// So a worker that holds the new-game lock and then hops to the dispatcher is waiting on a UI
    /// thread that is waiting on a lock it is holding: the worker cannot release the lock until the hop
    /// returns, and the UI thread cannot run the handler until the lock is free. <c>Dispatcher.Invoke</c>
    /// carries no timeout, and Playnite's own watchdog deliberately stays silent about this case
    /// ("Dispatcher.Wait may be waiting for the UI thread ... we don't want to report this as an error"),
    /// so a hang here is not an exception anyone downstream can act on. The plugin fails it in its own
    /// code instead: <c>VrModTagService.RunOnUiThread</c> calls <see cref="VrModTagService.EnsureUiHopIsSafe"/>
    /// before it marshals, and only holds taken through <see cref="VrModTagService.NewGameLock"/> count.
    ///
    /// These tests cover the guard and the lock semantics the swap depends on. The marshaling branch
    /// itself is not exercisable here: with no WPF <c>Application</c> in a test process
    /// <c>RunOnUiThread</c> takes its inline path and never consults the dispatcher.
    /// </summary>
    [TestFixture]
    public class VrModUiHopGuardTests
    {
        private IPlayniteAPI _api = null!;
        private RecordingLogger _logger = null!;
        private VrModSourceRegistry _registry = null!;

        [SetUp]
        public void SetUp()
        {
            _api = Substitute.For<IPlayniteAPI>();
            _logger = new RecordingLogger();
            _registry = new VrModSourceRegistry();
        }

        private VrModTagService CreateService()
            => new VrModTagService(_api, new VrModTagSettings(), _logger, _registry, 50);

        [Test]
        public void HopIsAllowedWhenNoNewGameLockIsHeld()
        {
            var service = CreateService();

            Assert.That(() => service.EnsureUiHopIsSafe(), Throws.Nothing,
                "a scan thread that holds nothing may wait on Playnite's UI thread - that is how " +
                "Game.Links mutations are marshaled. Log:\n" + _logger.Dump());
            Assert.That(_logger.Dump(), Does.Not.Contain("ERROR"),
                "the guard must stay silent on the allowed path. Log:\n" + _logger.Dump());
        }

        [Test]
        public void HopIsRefusedAndLoggedWhileTheCurrentThreadHoldsTheNewGameLock()
        {
            var service = CreateService();

            using (service.NewGameLock())
            {
                Assert.That(() => service.EnsureUiHopIsSafe(),
                    Throws.InstanceOf<InvalidOperationException>(),
                    "holding the new-game lock and waiting on Playnite's dispatcher is the inversion " +
                    "that used to stall a drain for the whole hop timeout. Log:\n" + _logger.Dump());

                Assert.That(_logger.Dump(), Does.Contain("refused to wait on Playnite's UI thread"),
                    "the refusal has to name its cause in the log Playnite writes to extensions.log. " +
                    "Log:\n" + _logger.Dump());
                Assert.That(_logger.Dump(), Does.Contain("new-game lock"),
                    "the log has to name the lock, not just 'a lock'. Log:\n" + _logger.Dump());
            }

            Assert.That(VrModTagService.CurrentThreadHoldsNewGameLock, Is.False,
                "refusing the hop must not release the caller's hold early: the caller's using scope " +
                "still owns it.");
            Assert.That(() => service.EnsureUiHopIsSafe(), Throws.Nothing,
                "after the caller's scope ends the same thread may hop again.");
        }

        [Test]
        public void NestedHoldsStayVisibleUntilTheOutermostScopeIsReleased()
        {
            var service = CreateService();

            using (service.NewGameLock())
            {
                using (service.NewGameLock())
                {
                    Assert.That(() => service.EnsureUiHopIsSafe(),
                        Throws.InstanceOf<InvalidOperationException>(), "nested hold #2");
                }

                Assert.That(VrModTagService.CurrentThreadHoldsNewGameLock, Is.True,
                    "the inner scope releasing must not clear the outer hold - reentrancy is counted, " +
                    "not collapsed");
                Assert.That(() => service.EnsureUiHopIsSafe(),
                    Throws.InstanceOf<InvalidOperationException>(), "nested hold #1 still held");
            }

            Assert.That(VrModTagService.CurrentThreadHoldsNewGameLock, Is.False, "both released");
        }

        [Test]
        public void AHoldOnOneThreadDoesNotBlindAnotherThreadToItsOwnHop()
        {
            var service = CreateService();

            using (service.NewGameLock())
            {
                var otherThreadSawHold = RunOnNewThread(() => VrModTagService.CurrentThreadHoldsNewGameLock);
                Assert.That(otherThreadSawHold, Is.False,
                    "the held count is per thread: the question the guard asks is whether THIS thread " +
                    "is about to wait on the dispatcher while it holds the lock.");

                var otherThreadHop = RunOnNewThread(() =>
                {
                    try { service.EnsureUiHopIsSafe(); return (Exception?)null; }
                    catch (Exception e) { return e; }
                });
                Assert.That(otherThreadHop, Is.Null,
                    "a thread that holds nothing is not blocked by another thread's hold.");
            }
        }

        /// <summary>
        /// The ten <c>lock (_newGameLock)</c> statements became <c>using (NewGameLock())</c>. That swap
        /// is only safe if the scope really is a Monitor with the same mutual exclusion, otherwise the
        /// new-game paths would run concurrently while looking serialized.
        /// </summary>
        [Test]
        public void NewGameLockExcludesAnotherThreadExactlyLikeTheLockStatementItReplaced()
        {
            var service = CreateService();
            var heldByOtherThread = new ManualResetEventSlim(false);

            var waiter = new Thread(() =>
            {
                using (service.NewGameLock())
                {
                    heldByOtherThread.Set();
                }
            })
            { IsBackground = true };

            var hold = service.NewGameLock();
            try
            {
                waiter.Start();
                Assert.That(heldByOtherThread.Wait(400), Is.False,
                    "another thread took the new-game lock while this thread held it - the hold is not " +
                    "mutually exclusive. Log:\n" + _logger.Dump());
            }
            finally
            {
                hold.Dispose();
            }

            Assert.That(heldByOtherThread.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "after the hold was released the waiting thread never got the lock - the release is not " +
                "reaching the monitor. Log:\n" + _logger.Dump());
        }

        /// <summary>
        /// An unbalanced <c>Monitor.Exit</c> throws <see cref="SynchronizationLockException"/> in the
        /// middle of a library event, so the release has to be single-shot even if something disposes
        /// the scope twice.
        /// </summary>
        [Test]
        public void ReleasingTheNewGameLockTwiceDoesNotUnlockSomethingElseOrThrow()
        {
            var service = CreateService();

            var scope = service.NewGameLock();
            scope.Dispose();
            Assert.That(() => scope.Dispose(), Throws.Nothing, "second Dispose");
            Assert.That(VrModTagService.CurrentThreadHoldsNewGameLock, Is.False,
                "the depth must not go negative");

            // Still usable afterwards: taking and releasing the lock again works.
            Assert.That(() => { using (service.NewGameLock()) { } }, Throws.Nothing,
                "the monitor must still be takeable after a double release. Log:\n" + _logger.Dump());
        }

        private static T RunOnNewThread<T>(Func<T> work)
        {
            T result = default!;
            var thread = new Thread(() => result = work()) { IsBackground = true };
            thread.Start();
            Assert.That(thread.Join(TimeSpan.FromSeconds(5)), Is.True, "the helper thread did not finish");
            return result;
        }

        private class RecordingLogger : ILogger
        {
            private readonly object _sync = new();
            private readonly System.Collections.Generic.List<string> _lines = new();

            public string Dump()
            {
                lock (_sync)
                {
                    return string.Join("\n", _lines);
                }
            }

            private void Add(string line)
            {
                lock (_sync)
                {
                    _lines.Add(line);
                }
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
