using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The built-in remote test run (docs/TESTING.md 2.6 plus a bot run), for a test PC where nobody may write the
    /// command file: with <c>[Debug] AutoTest</c> on, the runner queues these lines itself once the main menu is up.
    /// </summary>
    public static class AutoTest
    {
        public const string Script = @"
scene
screenshot main_menu
key F7
wait 1
screenshot f7_window
scene
mpwindow close
buttons
click Play
wait 2
buttons
screenshot after_play
wait 10
scene
screenshot main
check all
savecheck reset
host
wait 2
bot add Tester
wait 5
status
bot walk on
wait 4
screenshot bot_walking
bot shoot
wait 4
screenshot bot_shot
check colliders
dump
bot leave
wait 2
leave
wait 2
savecheck
key Escape
wait 1
screenshot pause
scene
check pause
check tab
buttons
click Settings
wait 1
screenshot settings
scene
check pause
buttons
click Back
wait 1
screenshot settings_closed
scene
buttons
click Save and Quit
wait 8
scene
screenshot menu_again
quit
";

        /// <summary>Seconds on the main menu before the run starts, so the menu has settled.</summary>
        public const double MenuSettleSeconds = 5.0;

        /// <summary>The script's commands, in order (blank lines and comments dropped).</summary>
        public static List<HarnessCommand> Commands()
        {
            var commands = new List<HarnessCommand>();
            foreach (var line in Script.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                var command = HarnessCommand.Parse(line.TrimEnd('\r'));
                if (command != null)
                    commands.Add(command);
            }
            return commands;
        }
    }
}
