using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for BaseVrModParser.ReadBoundedUtf8Async — the size guard on fetched web data
    /// that keeps a compromised or misbehaving source from growing the process out of memory.
    /// </summary>
    [TestFixture]
    public class BaseVrModParserFetchTests
    {
        [Test]
        public async Task ReadBounded_ContentLengthAboveMax_ThrowsWithoutReading()
        {
            var stream = new MemoryStream(new byte[1024]);
            InvalidOperationException? ex = null;
            try
            {
                await BaseVrModParser.ReadBoundedUtf8Async(stream, 1024 * 1024L + 1, 1024 * 1024);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            Assert.That(ex, Is.Not.Null, "Expected InvalidOperationException");
            if (ex is null)
                return;
            Assert.That(ex.Message, Does.Contain("Response too large"));
            Assert.That(stream.Position, Is.Zero, "No bytes should be read when Content-Length is already over the cap");
        }

        [Test]
        public async Task ReadBounded_StreamExceedsMax_Throws()
        {
            var bytes = new byte[100];
            new Random(42).NextBytes(bytes);
            var stream = new MemoryStream(bytes);
            // No Content-Length (chunked response): the cap is enforced while reading.
            InvalidOperationException? ex = null;
            try
            {
                await BaseVrModParser.ReadBoundedUtf8Async(stream, null, 10);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            Assert.That(ex, Is.Not.Null, "Expected InvalidOperationException");
            if (ex is null)
                return;
            Assert.That(ex.Message, Does.Contain("Response too large"));
        }

        [Test]
        public async Task ReadBounded_ExactlyMax_ReturnsAll()
        {
            var bytes = Encoding.UTF8.GetBytes(new string('a', 64));
            var stream = new MemoryStream(bytes);
            var text = await BaseVrModParser.ReadBoundedUtf8Async(stream, null, bytes.Length);
            Assert.That(text, Is.EqualTo(new string('a', 64)));
        }

        [Test]
        public async Task ReadBounded_WithinLimit_RoundTripsUtf8()
        {
            const string original = "héllo wörld — 游戏 <tag> & \"quotes\"";
            var bytes = Encoding.UTF8.GetBytes(original);
            var stream = new MemoryStream(bytes);
            var text = await BaseVrModParser.ReadBoundedUtf8Async(stream, bytes.Length, 4096);
            Assert.That(text, Is.EqualTo(original));
        }

        [Test]
        public async Task ReadBounded_Empty_ReturnsEmptyString()
        {
            var stream = new MemoryStream(Array.Empty<byte>());
            var text = await BaseVrModParser.ReadBoundedUtf8Async(stream, null, 1024);
            Assert.That(text, Is.Empty);
        }

        [Test]
        public void MaxResponseBytes_IsBounded()
        {
            // Guards against the cap being removed or set so low that real source pages
            // (a few hundred KB) would be rejected.
            Assert.That(BaseVrModParser.MaxResponseBytes, Is.InRange(1024 * 1024, 1024 * 1024 * 1024));
        }
    }
}
