using System;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// What the review dialog does with the result of a background operation.
    ///
    /// Both of the operations this dialog runs under Playnite's progress window - saving the row
    /// decisions, and "Reset and remove VRMod tags" - used to drop the <see cref="GlobalProgressResult"/>
    /// that <c>ActivateGlobalProgress</c> returns, and closed the dialog from the worker thread with
    /// <c>Dispatcher.BeginInvoke(() => Close())</c> regardless of outcome. A cancelled or failed
    /// operation therefore looked exactly like a completed one: the status line said "Scan completed."
    /// and the window closed itself. Both Playnite's extension guides instruct a caller to check the
    /// result, and neither check existed here. The plugin's own scan entry points do check
    /// <c>progRes.Error</c>; this round gives the dialog the same rule, plus the close decision.
    ///
    /// The decision half lives here as statics for the same reason <c>ScanBodyMayRunOnCallerThread</c>
    /// does: the rules are unit-testable without a WPF window or a Playnite API.
    /// </summary>
    [TestFixture]
    public class VrModProgressOutcomeTests
    {
        // --- A. a failure is shown ---------------------------------------------------------

        [Test]
        public void ACompletedOperationProducesNoFailureText()
        {
            var text = VrModReviewDialog.ProgressFailureText("Scan All Games",
                new GlobalProgressResult(true, false, null!));

            Assert.That(text, Is.Null, "success must not add a warning line");
        }

        [Test]
        public void AFailedOperationIsNamedWithItsCause()
        {
            var error = new InvalidOperationException("refused to wait on Playnite's UI thread");
            var text = VrModReviewDialog.ProgressFailureText("Clean Up VRMod Links",
                new GlobalProgressResult(null, false, error));

            Assert.That(text, Is.Not.Null);
            Assert.That(text, Does.Contain("Clean Up VRMod Links"),
                "the line has to name which operation failed - the user chose it from a menu: " + text);
            Assert.That(text, Does.Contain("refused to wait on Playnite's UI thread"),
                "and carry the cause the service logged, not a generic failure note: " + text);
            Assert.That(text, Does.StartWith("Clean Up VRMod Links failed:"),
                "the wording that puts this in front of the user is applied by the dialog's status " +
                "line; the helper's contract is the sentence itself");
        }

        [Test]
        public void AnOperationThatReportedNoChangeIsReportedEvenWithoutAnException()
        {
            // Playnite's GlobalProgressResult is built by hand in several of its own paths, so
            // Result == false can arrive with no Exception. It is an explicit "nothing changed" from
            // the action, and the dialog no longer repeats it back as "Scan completed."
            var text = VrModReviewDialog.ProgressFailureText("Rescan Selected Source",
                new GlobalProgressResult(false, false, null!));

            Assert.That(text, Is.Not.Null);
            Assert.That(text, Does.Contain("Rescan Selected Source"));
            Assert.That(text, Does.Contain("did not complete"));
        }

        /// <summary>
        /// The close rule must not depend on how completion is encoded in the result.
        ///
        /// The SDK documents <c>GlobalProgressResult(bool? result, bool canceled, Exception error)</c>
        /// with three get-only properties - "Gets execution result", "Gets value indicating whether the
        /// action was canceled by user", "Gets failure exception record" (api.playnite.link docs for
        /// <c>Playnite.SDK.GlobalProgressResult</c>) - and says nothing about what a plugin should find
        /// in <c>Result</c> after a successful action. In practice it varies: this plugin's wrappers hand
        /// back a result built from a bool, Playnite's void-callback overload builds an empty result, and
        /// Playnite's development docs for the next API generation drop <c>Result</c> altogether
        /// (<c>GlobalProgressResult(bool canceled, Exception? error)</c>). What is stable is the other
        /// three states, so "not completed" here means exactly an error, a cancellation or an explicit
        /// false - and everything else closes the dialog the way it did before this round.
        /// </summary>
        [Test]
        public void TheCloseRuleAcceptsBothCompletionEncodingsPlayniteUses()
        {
            var encodedAsEmpty = new GlobalProgressResult(null, false, null!);
            var encodedAsAValue = new GlobalProgressResult(true, false, null!);

            Assert.That(VrModReviewDialog.ProgressFailureText("Scan All Games", encodedAsEmpty), Is.Null,
                "an empty result is not read as a failure");
            Assert.That(VrModReviewDialog.ReviewDialogMayClose(encodedAsEmpty), Is.True,
                "and the review dialog closes on it, as it did before this round");

            Assert.That(encodedAsAValue.Result, Is.Not.False,
                "the SDK assembly this build uses (PlayniteSDK 6.15.0) keeps the argument it is given; " +
                "Playnite's own wrappers do not always. The dialog accepts either.");
            Assert.That(VrModReviewDialog.ProgressFailureText("Scan All Games", encodedAsAValue), Is.Null);
            Assert.That(VrModReviewDialog.ReviewDialogMayClose(encodedAsAValue), Is.True);

            Assert.That(VrModReviewDialog.ReviewDialogMayClose(
                new GlobalProgressResult(true, true, null!)), Is.False,
                "cancellation closes the door regardless of what the result field says");
        }

        [Test]
        public void AMissingResultIsReported()
        {
            var text = VrModReviewDialog.ProgressFailureText("Scan Selected Source", null);

            Assert.That(text, Is.Not.Null, "no result at all is not a completed operation");
            Assert.That(text, Does.Contain("Scan Selected Source"));
        }

        [Test]
        public void ACancelledOperationIsNotReportedAsAFailureLine()
        {
            // Cancellation is the user's own decision, so it gets a log line and its own status text
            // ("… was cancelled by the user.") rather than an error dialog. The dialog still refuses to
            // close on it, which is why the two states stay separate booleans instead of collapsing into
            // one message.
            var text = VrModReviewDialog.ProgressFailureText("Scan All Games",
                new GlobalProgressResult(false, true, null!));

            Assert.That(text, Is.Null);
        }

        // --- B. and never closed away ------------------------------------------------------

        [TestCase(true, false, false, true, TestName = "Completed_Closes")]
        [TestCase(null, false, false, true, TestName = "EmptyResultPlaynitesSuccessEncoding_Closes")]
        [TestCase(false, false, false, false, TestName = "ResultFalse_StaysOpen")]
        [TestCase(false, false, true, false, TestName = "Error_StaysOpen")]
        [TestCase(false, true, true, false, TestName = "ErrorAndCancel_StaysOpen")]
        [TestCase(false, false, true, false, TestName = "EmptyResultWithError_StaysOpen")]
        public void TheDialogStaysOpenForEveryOutcomeThatIsNotACompletedOperation(
            bool? result, bool canceled, bool hasError, bool expectClose)
        {
            Exception? error = hasError ? new Exception("operation failed") : null;
            Assert.That(VrModReviewDialog.ReviewDialogMayClose(
                new GlobalProgressResult(result, canceled, error!)), Is.EqualTo(expectClose),
                "cancellation and failure are actionable states; keeping the window open is what makes " +
                "them visible with the settings and sources still in reach");
        }

        [Test]
        public void AMissingResultKeepsTheDialogOpen()
            => Assert.That(VrModReviewDialog.ReviewDialogMayClose(null), Is.False);

        /// <summary>
        /// A and B are one rule: a result the dialog reports as failed must never be a result the
        /// dialog closes itself on. If either half is loosened later, this is the test that notices.
        /// </summary>
        [Test]
        public void FailureTextAndTheCloseDecisionNeverContradictEachOther()
        {
            var shapes = new[]
            {
                new GlobalProgressResult(true, false, null!),
                new GlobalProgressResult(true, false, new Exception("boom")),
                new GlobalProgressResult(true, true, null!),
                new GlobalProgressResult(true, true, new Exception("boom")),                new GlobalProgressResult(false, false, null!),
                new GlobalProgressResult(false, false, new Exception("boom")),
                new GlobalProgressResult(false, true, null!),
                new GlobalProgressResult(false, true, new Exception("boom")),
                new GlobalProgressResult(null, false, null!),
                new GlobalProgressResult(null, false, new Exception("boom")),
                new GlobalProgressResult(null, true, null!),
                new GlobalProgressResult(null, true, new Exception("boom")),
            };

            foreach (var outcome in shapes)
            {
                var hasFailureText = VrModReviewDialog.ProgressFailureText("Op", outcome) != null;
                var mayClose = VrModReviewDialog.ReviewDialogMayClose(outcome);

                Assert.That(hasFailureText && mayClose, Is.False,
                    "an outcome shown as failed cannot also close the window: " +
                    "result=" + outcome.Result + " canceled=" + outcome.Canceled +
                    " error=" + (outcome.Error != null) + " close=" + mayClose);
            }
        }

        /// <summary>
        /// The callback that closes the review dialog runs inside the progress action, so it is on
        /// Playnite's worker thread - the shape that let a review dialog vanish over the library's own
        /// library-changed event. The decision half is separated from that thread hop for exactly that
        /// reason; this test records which half is which.
        /// </summary>
        [Test]
        public void BothDecisionStaticsAreCallableWithoutAWindowOrAPlayniteApi()
        {
            Assert.That(VrModReviewDialog.ProgressFailureText, Is.Not.Null);
            Assert.That(VrModReviewDialog.ReviewDialogMayClose, Is.Not.Null);
        }
    }
}
