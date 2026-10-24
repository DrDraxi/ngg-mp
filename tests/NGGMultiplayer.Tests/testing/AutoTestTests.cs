using System.Linq;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    public class AutoTestTests
    {
        [Fact]
        public void Script_parses_into_commands_that_end_with_quit()
        {
            var commands = AutoTest.Commands();
            Assert.True(commands.Count > 40);
            Assert.Equal("quit", commands.Last().Line);
            Assert.DoesNotContain(commands, c => string.IsNullOrWhiteSpace(c.Line));
        }

        [Fact]
        public void Script_enters_the_story_through_the_menu_buttons()
        {
            var lines = AutoTest.Commands().Select(c => c.Line).ToList();
            Assert.True(lines.IndexOf("click Play") < lines.IndexOf("screenshot main"));
            Assert.True(lines.IndexOf("click Save and Quit") < lines.IndexOf("quit"));
        }
    }
}
