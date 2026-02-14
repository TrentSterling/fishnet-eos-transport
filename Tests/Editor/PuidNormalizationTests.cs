using NUnit.Framework;
using FishNet.Transport.EOSNative.Migration;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    /// <summary>
    /// OVERKILL tests for PUID normalization.
    /// PUIDs come from conn.GetAddress() and ProductUserId.ToString() — format mismatches cause
    /// migration failures, duplicate spawns, and voice player wiring bugs.
    /// </summary>
    public class PuidNormalizationTests
    {
        #region Null and Empty

        [Test]
        public void Null_ReturnsNull()
        {
            Assert.IsNull(PuidUtils.NormalizePuid(null));
        }

        [Test]
        public void Empty_ReturnsEmpty()
        {
            Assert.AreEqual("", PuidUtils.NormalizePuid(""));
        }

        #endregion

        #region Case Normalization

        [Test]
        public void Lowercase_Unchanged()
        {
            Assert.AreEqual("abc123def456", PuidUtils.NormalizePuid("abc123def456"));
        }

        [Test]
        public void Uppercase_Lowered()
        {
            Assert.AreEqual("abc123def456", PuidUtils.NormalizePuid("ABC123DEF456"));
        }

        [Test]
        public void MixedCase_Lowered()
        {
            Assert.AreEqual("abc123def456", PuidUtils.NormalizePuid("AbC123dEf456"));
        }

        #endregion

        #region Whitespace Trimming

        [Test]
        public void LeadingSpace_Trimmed()
        {
            Assert.AreEqual("abc123", PuidUtils.NormalizePuid(" abc123"));
        }

        [Test]
        public void TrailingSpace_Trimmed()
        {
            Assert.AreEqual("abc123", PuidUtils.NormalizePuid("abc123 "));
        }

        [Test]
        public void LeadingAndTrailingSpaces_Trimmed()
        {
            Assert.AreEqual("abc123", PuidUtils.NormalizePuid("  abc123  "));
        }

        [Test]
        public void Tab_Trimmed()
        {
            Assert.AreEqual("abc123", PuidUtils.NormalizePuid("\tabc123\t"));
        }

        [Test]
        public void Newline_Trimmed()
        {
            Assert.AreEqual("abc123", PuidUtils.NormalizePuid("\nabc123\r\n"));
        }

        [Test]
        public void InternalSpace_Preserved()
        {
            // Internal whitespace should NOT be stripped (though PUIDs shouldn't have it)
            Assert.AreEqual("abc 123", PuidUtils.NormalizePuid("abc 123"));
        }

        #endregion

        #region Combined Case + Whitespace

        [Test]
        public void UppercaseWithSpaces_NormalizedFully()
        {
            Assert.AreEqual("abc123def456", PuidUtils.NormalizePuid("  ABC123DEF456  "));
        }

        [Test]
        public void MixedCaseWithTabs_NormalizedFully()
        {
            Assert.AreEqual("abcdef", PuidUtils.NormalizePuid("\tAbCdEf\t"));
        }

        #endregion

        #region Realistic EOS PUIDs

        [Test]
        public void RealisticPuid_32CharHex_AlreadyNormalized()
        {
            string puid = "0002a8b3c4d5e6f70089abcdef012345";
            Assert.AreEqual(puid, PuidUtils.NormalizePuid(puid));
        }

        [Test]
        public void RealisticPuid_32CharHex_UpperCase()
        {
            Assert.AreEqual(
                "0002a8b3c4d5e6f70089abcdef012345",
                PuidUtils.NormalizePuid("0002A8B3C4D5E6F70089ABCDEF012345"));
        }

        [Test]
        public void RealisticPuid_WithWhitespace()
        {
            Assert.AreEqual(
                "0002a8b3c4d5e6f70089abcdef012345",
                PuidUtils.NormalizePuid(" 0002A8B3C4D5E6F70089ABCDEF012345 "));
        }

        #endregion

        #region Idempotency

        [Test]
        public void DoubleNormalize_SameResult()
        {
            string input = "  ABC123DEF456  ";
            string first = PuidUtils.NormalizePuid(input);
            string second = PuidUtils.NormalizePuid(first);
            Assert.AreEqual(first, second);
        }

        [Test]
        public void TripleNormalize_SameResult()
        {
            string input = "\tAbC123\n";
            string result = PuidUtils.NormalizePuid(PuidUtils.NormalizePuid(PuidUtils.NormalizePuid(input)));
            Assert.AreEqual("abc123", result);
        }

        #endregion

        #region Equality After Normalization

        [Test]
        public void DifferentCasing_EqualAfterNormalize()
        {
            string a = PuidUtils.NormalizePuid("ABC123");
            string b = PuidUtils.NormalizePuid("abc123");
            Assert.AreEqual(a, b);
        }

        [Test]
        public void DifferentWhitespace_EqualAfterNormalize()
        {
            string a = PuidUtils.NormalizePuid("  abc123  ");
            string b = PuidUtils.NormalizePuid("abc123");
            Assert.AreEqual(a, b);
        }

        [Test]
        public void ConnAddressVsToString_EqualAfterNormalize()
        {
            // Simulates: conn.GetAddress() returns uppercase, ProductUserId.ToString() returns lowercase
            string fromAddress = PuidUtils.NormalizePuid("0002A8B3C4D5E6F7");
            string fromToString = PuidUtils.NormalizePuid("0002a8b3c4d5e6f7");
            Assert.AreEqual(fromAddress, fromToString);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void SingleChar_Normalized()
        {
            Assert.AreEqual("a", PuidUtils.NormalizePuid("A"));
        }

        [Test]
        public void AllSpaces_BecomesEmpty()
        {
            Assert.AreEqual("", PuidUtils.NormalizePuid("   "));
        }

        [Test]
        public void NumericOnly_Unchanged()
        {
            Assert.AreEqual("1234567890", PuidUtils.NormalizePuid("1234567890"));
        }

        [Test]
        public void SpecialChars_Preserved()
        {
            // PUIDs shouldn't have special chars, but normalization shouldn't strip them
            Assert.AreEqual("abc-123_def", PuidUtils.NormalizePuid("ABC-123_DEF"));
        }

        #endregion
    }
}
