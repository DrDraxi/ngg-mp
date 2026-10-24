using System;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using Xunit;

namespace NGGMultiplayer.Tests.Game
{
    public class FactTableTests
    {
        [Fact]
        public void Set_ReportsChanges_AndStoresCopies()
        {
            var table = new FactTable();
            var value = new byte[] { 1, 2 };
            Assert.True(table.Set("bo", "BO_Glass", value));
            Assert.False(table.Set("bo", "BO_Glass", new byte[] { 1, 2 }));
            value[0] = 9;
            Assert.Equal(new byte[] { 1, 2 }, table.Get("bo", "BO_Glass"));
            var read = table.Get("bo", "BO_Glass");
            read[1] = 7;
            Assert.Equal(new byte[] { 1, 2 }, table.Get("bo", "BO_Glass"));
            Assert.True(table.Set("bo", "BO_Glass", new byte[] { 3 }));
            Assert.Equal(1, table.Count);
        }

        [Fact]
        public void NullAndEmptyValues_AreEqual()
        {
            var table = new FactTable();
            Assert.True(table.Set("seed", "", null));
            Assert.False(table.Set("seed", "", Array.Empty<byte>()));
            Assert.True(table.TryGet("seed", "", out var value));
            Assert.Empty(value);
        }

        [Fact]
        public void Set_RejectsWhatASaveDeltaCannotCarry()
        {
            var table = new FactTable();
            Assert.Throws<ArgumentException>(() => table.Set("", "k", null));
            Assert.Throws<ArgumentException>(() => table.Set("a\0b", "k", null));
            Assert.Throws<ArgumentException>(() => table.Set("k", null, null));
            Assert.Throws<ArgumentException>(() => table.Set("k", "k", new byte[SaveDelta.MaxValueBytes + 1]));
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void KindsAndKeys_DoNotCollide()
        {
            var table = new FactTable();
            table.Set("ab", "c", new byte[] { 1 });
            table.Set("a", "bc", new byte[] { 2 });
            Assert.Equal(new byte[] { 1 }, table.Get("ab", "c"));
            Assert.Equal(new byte[] { 2 }, table.Get("a", "bc"));
            Assert.Equal(new[] { "c" }, table.KeysOf("ab"));
            Assert.Equal(new[] { "bc" }, table.KeysOf("a"));
            Assert.Single(table.GetEntries("a"));
        }

        [Fact]
        public void Encoding_IsDeterministic_RegardlessOfInsertionOrder()
        {
            var a = new FactTable();
            a.Set("rb", "m_driverUnlocked", FactTable.EncodeBool(true));
            a.Set("bank", "", FactTable.EncodeInt(1234));
            a.Set("obj", "guid-1", new byte[] { 1 });
            var b = new FactTable();
            b.Set("obj", "guid-1", new byte[] { 1 });
            b.Set("bank", "", FactTable.EncodeInt(1234));
            b.Set("rb", "m_driverUnlocked", FactTable.EncodeBool(true));
            Assert.Equal(a.ToArray(), b.ToArray());
            Assert.Equal(a.ComputeCrc(), b.ComputeCrc());
            b.Set("bank", "", FactTable.EncodeInt(1235));
            Assert.NotEqual(a.ComputeCrc(), b.ComputeCrc());
        }

        [Fact]
        public void RoundTrip_PreservesEverything()
        {
            var table = new FactTable();
            table.Set("track", "main", FactTable.EncodeString("ML_NormalGolf"));
            table.Set("unl", "balls", new byte[] { 3, 1, 0, 1 });
            table.Set("seed", "", FactTable.EncodeInt(-7));
            var copy = FactTable.FromArray(table.ToArray());
            Assert.Equal(3, copy.Count);
            Assert.Equal("ML_NormalGolf", copy.GetString("track", "main"));
            Assert.Equal(-7, copy.GetInt("seed", ""));
            Assert.Equal(new byte[] { 3, 1, 0, 1 }, copy.Get("unl", "balls"));
            Assert.Equal(table.ComputeCrc(), copy.ComputeCrc());
        }

        [Fact]
        public void Deserialize_RejectsMalformedInput()
        {
            Assert.Throws<ProtocolException>(() => FactTable.FromArray(new byte[] { 2, 0 }));
            Assert.Throws<ProtocolException>(() => FactTable.FromArray(new byte[] { 1, 200 }));

            var w = new NetWriter();
            w.WriteVarUInt(FactTable.FormatVersion);
            w.WriteVarUInt(2);
            for (int i = 0; i < 2; i++)
            {
                w.WriteString("bo");
                w.WriteString("BO_x");
                w.WriteBytes(new byte[] { 1 });
            }
            Assert.Throws<ProtocolException>(() => FactTable.FromArray(w.ToArray()));

            var trailing = new FactTable().ToArray();
            Array.Resize(ref trailing, trailing.Length + 1);
            Assert.Throws<ProtocolException>(() => FactTable.FromArray(trailing));
        }

        [Fact]
        public void ReplaceWith_AndClone_AreIndependent()
        {
            var table = new FactTable();
            table.Set("bo", "BO_1", new byte[] { 1 });
            var clone = table.Clone();
            table.Set("bo", "BO_2", new byte[] { 1 });
            Assert.Equal(1, clone.Count);
            clone.ReplaceWith(table);
            Assert.Equal(2, clone.Count);
            clone.ReplaceWith(null);
            Assert.Equal(0, clone.Count);
            Assert.Equal(2, table.Count);
        }

        [Fact]
        public void ValueHelpers_RoundTrip_AndFallBack()
        {
            Assert.True(FactTable.DecodeBool(FactTable.EncodeBool(true)));
            Assert.False(FactTable.DecodeBool(FactTable.EncodeBool(false), true));
            Assert.True(FactTable.DecodeBool(null, true));
            Assert.Equal(int.MinValue, FactTable.DecodeInt(FactTable.EncodeInt(int.MinValue)));
            Assert.Equal(5, FactTable.DecodeInt(new byte[] { 1 }, 5));
            Assert.Equal("Höle 5", FactTable.DecodeString(FactTable.EncodeString("Höle 5")));
            var table = new FactTable();
            Assert.Equal(42, table.GetInt("ri", "bountyAccessLevel", 42));
            Assert.True(table.GetBool("rb", "x", true));
            Assert.Equal("none", table.GetString("track", "main", "none"));
        }
    }
}
