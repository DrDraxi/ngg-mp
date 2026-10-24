using System.Collections.Generic;
using NGGMultiplayer.UI.Native;
using Xunit;

namespace NGGMultiplayer.Tests.UI
{
    public class PlayerListTextTests
    {
        [Fact]
        public void Lines_NameTagsWhereAndPing()
        {
            var text = PlayerListText.Build(new List<MenuPlayer>
            {
                new MenuPlayer { Name = "Michal", IsHost = true, IsLocal = true, PingMs = 0, Where = "Walking, stroke 3" },
                new MenuPlayer { Name = "Bob", PingMs = 42, Where = "joining" }
            });
            var lines = text.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Michal <size=70%><color=#FFD27A>HOST</color></size> <size=70%><color=#FFFFFFAA>(you)</color></size>", lines[0]);
            Assert.Contains("<color=#B4FFFD>Walking, stroke 3</color>", lines[0]);
            Assert.DoesNotContain(" ms", lines[0]);
            Assert.StartsWith("Bob  ", lines[1]);
            Assert.Contains(">Joining<", lines[1]);
            Assert.Contains("42 ms", lines[1]);
        }

        [Fact]
        public void Names_WithTags_AreShownAsTyped()
        {
            Assert.Equal("<noparse><b>x</ noparse></noparse>", PlayerListText.Name("<b>x</noparse>"));
            Assert.Equal("plain", PlayerListText.Name("plain"));
            Assert.Equal(string.Empty, PlayerListText.Build(new List<MenuPlayer>()));
        }
    }
}
