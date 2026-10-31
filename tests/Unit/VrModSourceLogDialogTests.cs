using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the per-source "last run" log dialog (<see cref="VrModSourceLogDialog"/>): the
    /// shape of what a user sees after a scan — the failing source named, the result, the failure
    /// reason as one inline line under Result, a log that is the stretchy element, nothing below
    /// the log, and text that can be selected.
    ///
    /// These build the real dialog rather than a copy of its XAML. A check that re-declares the
    /// markup in another project instead of instantiating this dialog drifts without failing: the
    /// never-scanned hint below was written by the constructor but left Collapsed (the XAML
    /// default), so a never-scanned source opened the dialog and saw only its own name. Only the
    /// real dialog can catch that, so the real dialog is what these tests construct. (Live layout
    /// — measured heights and text selection in a shown window — is out of reach for a
    /// construction-only test and is verified by hand against a running Playnite.)
    ///
    /// Windows are constructed but never shown: no HWND, no desktop needed, nothing to close.
    /// Layout-dependent claims (row heights, resize behaviour, selection) are the harness's job;
    /// what is asserted here is structural: visibility, containment, row placement, control types.
    /// </summary>
    [TestFixture]
    public class VrModSourceLogDialogTests
    {
        private const string SourceName = "REFramework";
        private const string FailureReason = "HTTP 429 (Too Many Requests)";

        /// <summary>A last-run record as the tag service would leave it after one scan.</summary>
        private static SourceStatus Run(
            bool succeeded, int entries, string? error, params string[] logLines)
        {
            return new SourceStatus
            {
                LastScanTime = new DateTime(2026, 10, 29, 8, 12, 34, DateTimeKind.Utc),
                LastResult = succeeded,
                LastEntryCount = entries,
                LastError = error,
                LastTagCount = 61,
                LastLinkCount = 12,
                LastTagCountApplied = 44,
                LastLinkCountApplied = 9,
                LastRunLog = new List<string>(logLines)
            };
        }

        private static T Element<T>(VrModSourceLogDialog dialog, string name)
        {
            var found = dialog.FindName(name);
            Assert.That(found, Is.Not.Null, $"the dialog has no element named '{name}'");
            Assert.That(found, Is.InstanceOf<T>(), $"'{name}' is not a {typeof(T).Name}");
            return (T)found;
        }

        // ---------------------------------------------------------------------------
        // A source that has never been scanned since this Playnite run.
        // ---------------------------------------------------------------------------

        [Test]
        [Apartment(ApartmentState.STA)]
        public void NeverScannedSource_ShowsTheHintAndNothingElse()
        {
            var dialog = new VrModSourceLogDialog(SourceName, null);

            // The regression this test exists for: the constructor wrote the hint text while the
            // element stayed Collapsed (its XAML default), so the dialog rendered an empty body.
            var hint = Element<TextBlock>(dialog, "NeverScannedText");
            Assert.That(hint.Visibility, Is.EqualTo(Visibility.Visible),
                "a never-scanned source must be told it has not been scanned, not shown an empty dialog");
            Assert.That(hint.Text, Does.Contain("has not been scanned yet"));

            Assert.That(Element<FrameworkElement>(dialog, "DetailGrid").Visibility, Is.EqualTo(Visibility.Collapsed),
                "there are no counts to show for a run that never happened");
            Assert.That(Element<TextBox>(dialog, "ErrorText").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Element<FrameworkElement>(dialog, "DebugLogBox").Visibility, Is.EqualTo(Visibility.Collapsed));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void DialogNamesTheSource_HeadingAndTitle()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(false, 0, FailureReason));

            Assert.That(Element<TextBlock>(dialog, "SourceNameText").Text, Is.EqualTo(SourceName),
                "the heading is the failing source's own name");
            Assert.That(dialog.Title, Does.Contain(SourceName));
        }

        // ---------------------------------------------------------------------------
        // The failure reason: one inline line under Result, not an error pane.
        // ---------------------------------------------------------------------------

        [Test]
        [Apartment(ApartmentState.STA)]
        public void FailedRun_ReasonSitsInlineDirectlyUnderResult()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(false, 0, FailureReason, "request 1 of 3", "giving up"));

            var error = Element<TextBox>(dialog, "ErrorText");
            Assert.That(error.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(error.Text, Is.EqualTo(FailureReason), "the cause the user has to act on is shown verbatim");

            // Inline means inside the detail block, on the row directly below Result — the
            // alternative shape the user reported (a pane of its own under the log) is impossible
            // from this structure, which the next test pins down from the other side.
            Assert.That(error.Parent, Is.SameAs(Element<FrameworkElement>(dialog, "DetailGrid")));
            var result = Element<FrameworkElement>(dialog, "ResultText");
            Assert.That(Grid.GetRow(error), Is.EqualTo(Grid.GetRow(result) + 1));
            Assert.That(Grid.GetColumnSpan(error), Is.EqualTo(2), "the line spans label and value columns");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void FailedRun_ReasonIsASelectableLineNotABox()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(false, 0, FailureReason));
            var error = Element<TextBox>(dialog, "ErrorText");

            // A TextBlock on .NET Framework cannot be selected, so the reason is a read-only
            // TextBox with no chrome: it looks like a line of text and copies like one.
            Assert.That(error.IsReadOnly, Is.True);
            Assert.That(error.BorderThickness, Is.EqualTo(new Thickness(0)), "no box around the line");
            Assert.That(error.TextWrapping, Is.EqualTo(TextWrapping.Wrap), "a long HTTP reason must not be cut off");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void SuccessfulRun_ShowsNoErrorLine()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(true, 87, null, "200 OK", "42 mod rows parsed"));

            Assert.That(Element<TextBlock>(dialog, "ResultText").Text, Is.EqualTo("Success"));
            Assert.That(Element<TextBox>(dialog, "ErrorText").Visibility, Is.EqualTo(Visibility.Collapsed),
                "an error line with nothing to say is noise");
            Assert.That(Element<FrameworkElement>(dialog, "DebugLogBox").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Element<TextBox>(dialog, "DebugLogText").Text, Is.EqualTo("200 OK\n42 mod rows parsed"));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void EmptySuccessfulRun_ReadsAsNoData()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(true, 0, null, "200 OK", "no rows matched"));

            // The settings grid's three states, so the dialog and the grid agree.
            Assert.That(Element<TextBlock>(dialog, "ResultText").Text, Is.EqualTo("No data"));
            Assert.That(Element<TextBox>(dialog, "ErrorText").Visibility, Is.EqualTo(Visibility.Collapsed),
                "no data is not a failure and must not look like one");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void RunWithoutLogLines_CollapsesTheLogSectionButKeepsTheReason()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(false, 0, FailureReason));

            Assert.That(Element<FrameworkElement>(dialog, "DebugLogBox").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Element<FrameworkElement>(dialog, "DebugLogLabel").Visibility, Is.EqualTo(Visibility.Collapsed),
                "no 'Debug log' heading above an absent log");
            Assert.That(Element<TextBox>(dialog, "ErrorText").Visibility, Is.EqualTo(Visibility.Visible),
                "the reason is shown even when the run logged nothing");
        }

        // ---------------------------------------------------------------------------
        // The log is the stretchy element and nothing hangs below it.
        // ---------------------------------------------------------------------------

        [Test]
        [Apartment(ApartmentState.STA)]
        public void TheLogIsTheLastRowAndTheOnlyStretchyOne()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(false, 0, FailureReason, "request 1 of 3"));
            var logBox = Element<FrameworkElement>(dialog, "DebugLogBox");
            Assert.That(logBox.Parent, Is.InstanceOf<Grid>(), "the log box sits directly in the dialog's root grid");
            var root = (Grid)logBox.Parent;

            var logRow = Grid.GetRow(logBox);
            Assert.That(logRow, Is.EqualTo(root.RowDefinitions.Count - 1),
                "the log is the last row: there is no row left below it for an error pane");
            Assert.That(root.RowDefinitions[logRow].Height.IsStar, Is.True,
                "the log row is the one that grows with the window");

            // Exactly one star row: resizing gives its extra height to the log and to nothing else.
            var starRows = root.RowDefinitions.Where(r => r.Height.IsStar).ToList();
            Assert.That(starRows.Count, Is.EqualTo(1));

            // Every element in a non-star row is a fixed-height row of text or a heading.
            var inStarRow = root.Children.Cast<UIElement>().Where(c => Grid.GetRow(c) == logRow).ToList();
            Assert.That(inStarRow, Is.EquivalentTo(new[] { logBox }), "only the log box sits in the stretchy row");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void NeverScannedWindowIsSmall_AWindowWithALogKeepsItsRoom()
        {
            var neverScanned = new VrModSourceLogDialog(SourceName, null);
            Assert.That(neverScanned.Height, Is.LessThan(200),
                "there is no log to fill a 360 DIU window with, and a dialog 88% blank reads as broken");
            Assert.That(neverScanned.MinHeight, Is.LessThan(200),
                "the 300 DIU floor exists to give a log room, and there is no log here");

            var withLog = new VrModSourceLogDialog(SourceName, Run(true, 87, null, "200 OK", "42 mod rows parsed"));
            Assert.That(withLog.Height, Is.EqualTo(360), "a logged run keeps the roomy default so the log can stretch");
            Assert.That(withLog.MinHeight, Is.EqualTo(300));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void TheLogIsAReadOnlyTextBoxThatAcceptsMultilineText()
        {
            var dialog = new VrModSourceLogDialog(SourceName, Run(true, 87, null, "line one", "line two"));
            var log = Element<TextBox>(dialog, "DebugLogText");

            // Selectable (a TextBox, not a TextBlock) and scrolls its own content, which is why
            // the dialog needs no outer ScrollViewer.
            Assert.That(log.IsReadOnly, Is.True);
            Assert.That(log.AcceptsReturn, Is.True);
            Assert.That(log.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(log.VerticalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Auto));
        }
    }
}
