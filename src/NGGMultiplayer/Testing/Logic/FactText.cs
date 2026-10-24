using System;
using System.Globalization;
using System.Text;
using NGGMultiplayer.Game.Progression;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Fact values (docs/GAME_INTEGRATION.md 2.3) as short readable text for the harness's results and dumps. Total:
    /// a value that does not decode for its kind is shown as hex. No Unity types (unit tested).
    /// </summary>
    public static class FactText
    {
        public const int MaxTextChars = 160;

        public static string Format(string kind, byte[] value)
        {
            if (value == null)
                return "null";
            string text;
            switch (kind)
            {
                case FactKinds.Track:
                    text = FactCodec.TryDecodeTrack(value, out var track) ? track.Set + "#" + track.Index.ToString(CultureInfo.InvariantCulture) : null;
                    break;
                case FactKinds.Bounty:
                    text = FactCodec.TryDecodeFlags(value, out byte flags)
                        ? ((flags & FactCodec.BountyDone) != 0 ? "done" : "not done") + ((flags & FactCodec.BountyOnEasy) != 0 ? "+easy" : string.Empty)
                        : null;
                    break;
                case FactKinds.DoNotReload:
                case FactKinds.Easy:
                    text = FactCodec.TryDecodeFlags(value, out byte marker) ? marker.ToString(CultureInfo.InvariantCulture) : null;
                    break;
                case FactKinds.RunBool:
                    text = FactCodec.TryDecodeBool(value, out bool b) ? (b ? "true" : "false") : null;
                    break;
                case FactKinds.RunInt:
                case FactKinds.RunHostInt:
                case FactKinds.Bank:
                case FactKinds.Seed:
                    text = FactCodec.TryDecodeInt(value, out int i) ? i.ToString(CultureInfo.InvariantCulture) : null;
                    break;
                case FactKinds.Obj:
                    text = FactCodec.TryDecodeObj(value, out var obj)
                        ? (obj.Active ? "active" : "inactive") + (obj.HasRotation ? " " + obj.Rotation : string.Empty)
                        : null;
                    break;
                case FactKinds.PreHit:
                    text = FactCodec.TryDecodeStringSet(value, out var set) ? "[" + string.Join(", ", set) + "]" : null;
                    break;
                case FactKinds.Unlocks:
                    text = FactCodec.TryDecodeBoolList(value, out var list) ? BoolList(list) : null;
                    break;
                case FactKinds.Money:
                    text = FactCodec.TryDecodeFlags(value, out byte state) ? MoneyState(state) : null;
                    break;
                case FactKinds.Auth:
                case FactKinds.ScoreCard:
                    text = TryUtf8(value);
                    break;
                default:
                    text = null;
                    break;
            }
            return Clip(text ?? Hex(value));
        }

        public static string Hex(byte[] value)
        {
            if (value == null)
                return "null";
            if (value.Length == 0)
                return "0x";
            var sb = new StringBuilder(2 + value.Length * 2);
            sb.Append("0x");
            int count = Math.Min(value.Length, MaxTextChars / 2);
            for (int i = 0; i < count; i++)
                sb.Append(value[i].ToString("x2", CultureInfo.InvariantCulture));
            if (count < value.Length)
                sb.Append("...(" + value.Length.ToString(CultureInfo.InvariantCulture) + " bytes)");
            return sb.ToString();
        }

        private static string BoolList(System.Collections.Generic.List<bool> list)
        {
            var sb = new StringBuilder(list.Count + 2);
            sb.Append('[');
            foreach (bool b in list)
                sb.Append(b ? '1' : '0');
            sb.Append(']');
            return sb.ToString();
        }

        private static string MoneyState(byte state)
        {
            switch (state)
            {
                case 0: return "none";
                case 1: return "deducted";
                case 2: return "returned";
                default: return null;
            }
        }

        private static string TryUtf8(byte[] value)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(value);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string Clip(string text) =>
            text.Length <= MaxTextChars ? text : text.Substring(0, MaxTextChars) + "...";
    }
}
