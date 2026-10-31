using System;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The vocabularies behind the plugin's narrowed catch filters, which is where the CA1031 rule
    /// is answered rather than silenced. A filter says two things at once: which failures count
    /// as "the environment did something hostile" - an unreachable host, a locked file, a payload of a
    /// shape nobody predicted - and which therefore do NOT count: a fault in this plugin's own code.
    /// Those propagate to the handler that reports the source as failed, instead of being dressed up
    /// as a data problem and quietly absorbed as "no results this run".
    ///
    /// These predicates are that decision written down, so they are pinned: adding a type to one is a
    /// deliberate widening with a failing test attached, not a side effect of someone making a catch
    /// "a bit safer".
    /// </summary>
    [TestFixture]
    public class CatchVocabularyTests
    {
        [Test]
        public void RequestPlumbing_FailuresThatNeverReachedAnHttpResponseAreAbsorbed()
        {
            Exception[] environment =
            {
                new HttpRequestException("no connection could be made"),
                new IOException("connection closed prematurely"),
                new UnauthorizedAccessException("policy"),
                new TaskCanceledException("the client's own timeout"),
                new ObjectDisposedException("System.Net.Http.HttpClient"),
                new NotSupportedException("the configured proxy is not supported"),
                new UriFormatException("the source URL is not a URI this stack will build")
            };

            foreach (Exception ex in environment)
            {
                Assert.That(
                    BaseVrModParser.IsRequestPlumbingFailure(ex),
                    Is.True,
                    $"{ex.GetType().Name} is a request-plumbing failure and must keep failing the request, not the scan");
            }
        }

        [Test]
        public void RequestPlumbing_OwnFaultsAreNotReportedAsTransportFailures()
        {
            Exception[] ours =
            {
                new InvalidOperationException("a state machine in this plugin is wrong"),
                new InvalidCastException("a cast the code should never have made"),
                new NullReferenceException("a reference this plugin forgot"),
                new FormatException("a format string this plugin wrote")
            };

            foreach (Exception ex in ours)
            {
                Assert.That(
                    BaseVrModParser.IsRequestPlumbingFailure(ex),
                    Is.False,
                    $"{ex.GetType().Name} is this plugin's own fault; describing it as a transport failure hides it");
            }
        }

        [Test]
        public void JsonPayload_ShapeAndParseFailuresAreAbsorbed()
        {
            Exception[] payload =
            {
                new JsonException("malformed JSON"),
                new InvalidOperationException("a JSON value of a kind the parser did not expect"),
                // FormatException, and its derived UriFormatException, is what reading a value the
                // payload does not actually hold produces: a shape problem, so it belongs here.
                new FormatException("a value this parser cannot read"),
                new UriFormatException("FormatException-derived, so the same case")
            };

            foreach (Exception ex in payload)
            {
                Assert.That(
                    PcvrCentralParser.IsJsonShapeFailure(ex),
                    Is.True,
                    $"{ex.GetType().Name} means the site changed its payload, which is a no-data run, not a crash");
            }
        }

        [Test]
        public void JsonPayload_FaultsThatAreNotAboutThePayloadAreNotAbsorbed()
        {
            Exception[] ours =
            {
                new HttpRequestException("the fetch itself failed - that is plumbing, not JSON"),
                new NotSupportedException("not a payload problem at all"),
                new DivideByZeroException("a fault in this parser's own arithmetic")
            };

            foreach (Exception ex in ours)
            {
                Assert.That(
                    PcvrCentralParser.IsJsonShapeFailure(ex),
                    Is.False,
                    $"{ex.GetType().Name} is not a payload-shape failure");
            }
        }

        [Test]
        public void PathsAndFiles_OtherPeoplesFileSystemIsAbsorbed()
        {
            Exception[] environment =
            {
                new IOException("the queue file is locked by the running Playnite instance"),
                new UnauthorizedAccessException("the directory is not readable"),
                new PathTooLongException("a path over MAX_PATH"),
                new ArgumentException("a path string this stack cannot resolve"),
                new NotSupportedException("a path form this stack cannot resolve"),
                new SecurityException("the security policy refused the read")
            };

            foreach (Exception ex in environment)
            {
                Assert.That(
                    VrModTagPlugin.IsPathOrFileFailure(ex),
                    Is.True,
                    $"{ex.GetType().Name} on a file Playnite owns means 'we cannot know', which is the safe answer");
            }
        }

        [Test]
        public void PathsAndFiles_OwnFaultsAreNotReportedAsNotQueued()
        {
            Exception[] ours =
            {
                new InvalidOperationException("a check this plugin got wrong"),
                new InvalidCastException("a cast this plugin should never make")
            };

            foreach (Exception ex in ours)
            {
                Assert.That(
                    VrModTagPlugin.IsPathOrFileFailure(ex),
                    Is.False,
                    $"{ex.GetType().Name} must not be answered with 'no uninstall is queued'");
            }
        }
    }
}
