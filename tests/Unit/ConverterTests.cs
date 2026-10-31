using System;
using System.Windows;
using System.Windows.Media;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the settings-UI value converters. Unit tests run without a WPF Application,
    /// so the theme lookups miss and the converters return their static fallback brushes —
    /// the prefix → color mapping is what is pinned here.
    /// </summary>
    [TestFixture]
    public class ErrorToBrushConverterTests
    {
        private readonly ErrorToBrushConverter _converter = new ErrorToBrushConverter();

        [TestCase("Error: fetch failed")]
        [TestCase("error: fetch failed")]
        [TestCase("ERROR: fetch failed")]
        public void ErrorText_ReturnsErrorRed(string text)
            => Assert.That(_converter.Convert(text, null, null, null), Is.EqualTo(Brushes.Red));

        [TestCase("No data: 0 entries")]
        [TestCase("no data: 0 entries")]
        public void NoDataText_ReturnsAttentionOrange(string text)
            => Assert.That(_converter.Convert(text, null, null, null), Is.EqualTo(Brushes.Orange));

        [TestCase("Last scan: 1/1/2024 12:00 PM")]
        [TestCase("last scan: 1/1/2024 12:00 PM")]
        public void LastScanText_ReturnsHealthyGreen(string text)
            => Assert.That(_converter.Convert(text, null, null, null), Is.EqualTo(Brushes.Green));

        [TestCase("Never scanned")]
        [TestCase("")]
        public void OtherText_ReturnsNeutralGray(string text)
            => Assert.That(_converter.Convert(text, null, null, null), Is.EqualTo(Brushes.Gray));

        [Test]
        public void NullOrNonString_ReturnsNeutralGray()
        {
            Assert.That(_converter.Convert(null, null, null, null), Is.EqualTo(Brushes.Gray));
            Assert.That(_converter.Convert(42, null, null, null), Is.EqualTo(Brushes.Gray));
        }

        [Test]
        public void ConvertBack_Throws()
        {
            Assert.Throws<NotImplementedException>(() => _converter.ConvertBack(Brushes.Red, null, null, null));
        }
    }

    [TestFixture]
    public class BoolToVisConverterTests
    {
        private readonly BoolToVisConverter _converter = new BoolToVisConverter();

        [Test]
        public void TrueMapsToVisible_FalseMapsToCollapsed()
        {
            Assert.That(_converter.Convert(true, null, null, null), Is.EqualTo(Visibility.Visible));
            Assert.That(_converter.Convert(false, null, null, null), Is.EqualTo(Visibility.Collapsed));
        }

        [Test]
        public void InvertParameter_InvertsMapping()
        {
            Assert.That(_converter.Convert(true, null, "Invert", null), Is.EqualTo(Visibility.Collapsed));
            Assert.That(_converter.Convert(false, null, "Invert", null), Is.EqualTo(Visibility.Visible));
        }

        [Test]
        public void NonBool_IsCollapsed()
        {
            Assert.That(_converter.Convert(null, null, null, null), Is.EqualTo(Visibility.Collapsed));
            Assert.That(_converter.Convert("true", null, null, null), Is.EqualTo(Visibility.Collapsed));
        }

        [Test]
        public void ConvertBack_Throws()
        {
            Assert.Throws<NotImplementedException>(() => _converter.ConvertBack(Visibility.Visible, null, null, null));
        }
    }
}
